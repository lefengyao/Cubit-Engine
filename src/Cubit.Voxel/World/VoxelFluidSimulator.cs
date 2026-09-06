namespace Cubit.Voxel.World;

/// <summary>由 VoxelWorld 拥有的有界、确定性动态流体计划刻模拟器。</summary>
internal sealed class VoxelFluidSimulator
{
    private readonly record struct UpdateKey(int X, int Y, int Z, ushort FluidId);
    private readonly record struct Update(UpdateKey Key, BlockState Expected, long DueTick, long Order);

    private readonly VoxelFluidSimulationOptions _options;
    private readonly List<Update> _updates = [];
    private readonly HashSet<UpdateKey> _queued = [];
    private readonly HashSet<(int X, int Y, int Z)> _activations = [];
    private readonly HashSet<(int X, int Z)> _dirtyChunks = [];
    private long _nextOrder;

    public VoxelFluidSimulator(VoxelFluidSimulationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public int ScheduledCount => _updates.Count;

    public int QueueOverflowCount { get; private set; }

    public IReadOnlyList<(int X, int Y, int Z)> ActivationCoordinates => _activations
        .OrderBy(item => item.X).ThenBy(item => item.Y).ThenBy(item => item.Z).ToArray();

    public (int X, int Z)[] ConsumeDirtyChunks()
    {
        var keys = _dirtyChunks.ToArray();
        _dirtyChunks.Clear();
        return keys;
    }

    public void RestoreActivations(IEnumerable<(int X, int Y, int Z)> coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        foreach (var coordinate in coordinates.OrderBy(item => item.X).ThenBy(item => item.Y).ThenBy(item => item.Z))
        {
            if (Chunk.IsValidY(coordinate.Y))
            {
                _activations.Add(coordinate);
            }
        }
    }

    public void OnBlockChanged(
        VoxelWorld world,
        int x,
        int y,
        int z,
        BlockState previous,
        BlockState next)
    {
        if (!_options.Enabled || !Chunk.IsValidY(y))
        {
            return;
        }

        var previousFluid = VoxelFluidState.TryDecode(previous, out _);
        var nextFluid = VoxelFluidState.TryDecode(next, out _);
        var coordinate = (x, y, z);
        if (nextFluid && !previousFluid)
        {
            _activations.Add(coordinate);
            Schedule(world, x, y, z, next);
        }
        else if (!nextFluid)
        {
            _activations.Remove(coordinate);
        }
        else if (nextFluid)
        {
            _activations.Add(coordinate);
            Schedule(world, x, y, z, next);
        }
    }

    public void SeedChunk(VoxelWorld world, int chunkX, int chunkZ)
    {
        if (!_options.Enabled)
        {
            return;
        }

        foreach (var coordinate in _activations
                     .Where(item => ChunkStore.WorldToChunk(item.X, item.Z) == (chunkX, chunkZ))
                     .OrderBy(item => item.X).ThenBy(item => item.Y).ThenBy(item => item.Z))
        {
            var block = world.GetBlock(coordinate.X, coordinate.Y, coordinate.Z);
            if (VoxelFluidState.TryDecode(block, out _))
            {
                Schedule(world, coordinate.X, coordinate.Y, coordinate.Z, block);
            }
        }
    }

    public void Advance(VoxelWorld world)
    {
        if (!_options.Enabled || _updates.Count == 0)
        {
            return;
        }

        var due = _updates
            .Where(update => update.DueTick <= world.TickCount)
            .OrderBy(update => update.DueTick)
            .ThenBy(update => update.Order)
            .Take(_options.MaxUpdatesPerTick)
            .ToArray();
        if (due.Length == 0)
        {
            return;
        }

        foreach (var update in due)
        {
            _updates.Remove(update);
            _queued.Remove(update.Key);
            Process(world, update);
        }
    }

    private void Process(VoxelWorld world, Update update)
    {
        var current = world.GetBlock(update.Key.X, update.Key.Y, update.Key.Z);
        if (current != update.Expected || !VoxelFluidState.TryDecode(current, out var fluid))
        {
            return;
        }

        var below = (update.Key.X, update.Key.Y - 1, update.Key.Z);
        if (Chunk.IsValidY(below.Item2) && world.GetBlock(below.Item1, below.Item2, below.Item3).IsAir)
        {
            var falling = VoxelFluidState.FromLevel(update.Key.FluidId, VoxelFluidState.SourceLevel, falling: true);
            if (world.SetBlockDeferred(below.Item1, below.Item2, below.Item3, falling, out var affected))
            {
                foreach (var key in affected)
                {
                    _dirtyChunks.Add(key);
                }
                ScheduleNeighborhood(world, below.Item1, below.Item2, below.Item3, falling);
                return;
            }
        }

        if (fluid.Level > 1)
        {
            var nextLevel = (byte)(fluid.Level - 1);
            foreach (var (dx, dz) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
            {
                var target = (update.Key.X + dx, update.Key.Y, update.Key.Z + dz);
                if (!world.GetBlock(target.Item1, target.Item2, target.Item3).IsAir)
                {
                    continue;
                }

                var flowing = VoxelFluidState.FromLevel(update.Key.FluidId, nextLevel);
                if (world.SetBlockDeferred(target.Item1, target.Item2, target.Item3, flowing, out var affected))
                {
                    foreach (var key in affected)
                    {
                        _dirtyChunks.Add(key);
                    }
                    ScheduleNeighborhood(world, target.Item1, target.Item2, target.Item3, flowing);
                }
            }
        }

        if (fluid.Level < VoxelFluidState.SourceLevel && !fluid.Falling && !HasSupport(world, update.Key.X, update.Key.Y, update.Key.Z, update.Key.FluidId, fluid.Level))
        {
            if (world.SetBlockDeferred(update.Key.X, update.Key.Y, update.Key.Z, BlockState.Air, out var affected))
            {
                _activations.Remove((update.Key.X, update.Key.Y, update.Key.Z));
                foreach (var key in affected)
                {
                    _dirtyChunks.Add(key);
                }
            }
        }
    }

    private static bool HasSupport(VoxelWorld world, int x, int y, int z, ushort fluidId, byte level)
    {
        var above = world.GetBlock(x, y + 1, z);
        if (VoxelFluidState.TryDecode(above, out var aboveFluid) && above.Id == fluidId && (aboveFluid.Falling || aboveFluid.Level >= level + 1))
        {
            return true;
        }

        foreach (var (dx, dz) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
        {
            var side = world.GetBlock(x + dx, y, z + dz);
            if (VoxelFluidState.TryDecode(side, out var sideFluid) && side.Id == fluidId && sideFluid.Level > level)
            {
                return true;
            }
        }

        return false;
    }

    private void ScheduleNeighborhood(VoxelWorld world, int x, int y, int z, BlockState expected)
    {
        Schedule(world, x, y, z, expected);
        foreach (var (dx, dz) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
        {
            var nx = x + dx;
            var nz = z + dz;
            var neighbour = world.GetBlock(nx, y, nz);
            if (VoxelFluidState.TryDecode(neighbour, out _))
            {
                Schedule(world, nx, y, nz, neighbour);
            }
        }
    }

    private void Schedule(VoxelWorld world, int x, int y, int z, BlockState expected)
    {
        if (!VoxelFluidState.TryDecode(expected, out var fluid))
        {
            return;
        }

        var key = new UpdateKey(x, y, z, expected.Id);
        var existingIndex = _updates.FindIndex(update => update.Key.Equals(key));
        if (existingIndex >= 0)
        {
            var existing = _updates[existingIndex];
            if (existing.Expected == expected && existing.DueTick <= world.TickCount + _options.FluidTickDelay)
            {
                return;
            }

            _updates.RemoveAt(existingIndex);
            _queued.Remove(key);
        }

        if (_updates.Count >= _options.MaxQueuedUpdates)
        {
            QueueOverflowCount++;
            return;
        }

        _updates.Add(new Update(key, expected, world.TickCount + _options.FluidTickDelay, _nextOrder++));
        _queued.Add(key);
        _activations.Add((x, y, z));
    }
}
