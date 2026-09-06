using System.Numerics;
using Cubit.Core.Scene;
using Cubit.Physics.Scene;
using Cubit.Physics.Shapes;
using Cubit.Voxel.World;

namespace Cubit.Voxel.Physics;

/// <summary>
/// 把局部已加载体素地形映射为通用物理插件的静态盒碰撞体。
/// 未加载区块和碰撞窗口边界始终保守地视为不可通过，避免流式世界出现可穿透空洞。
/// </summary>
public sealed class VoxelTerrainBody3D : StaticBody3D
{
    private const float BoundaryThickness = 1f;
    private readonly List<CollisionShape3D> _generatedShapes = [];
    private readonly List<CollisionShape3D> _boundaryShapes = [];
    private readonly Dictionary<(int X, int Z), List<CollisionShape3D>> _chunkShapes = [];
    private readonly Dictionary<(int X, int Z, int Section), List<CollisionShape3D>> _sectionShapes = [];
    private readonly Dictionary<(int X, int Z, int Section), int> _sectionFineColliderCounts = [];
    private readonly Dictionary<(int X, int Z), int> _chunkFineColliderCounts = [];
    private readonly Dictionary<(int X, int Z), long> _syncedChunkRevisions = [];
    private readonly Dictionary<(int X, int Z), bool> _syncedChunkPresence = [];
    private readonly HashSet<(int X, int Z)> _fallbackChunks = [];
    private VoxelWorldNode? _worldNode;
    private CharacterBody3D? _focus;
    private VoxelWorld? _syncedWorld;
    private ulong _syncedTerrainRevision = ulong.MaxValue;
    private int _syncedChunkX = int.MinValue;
    private int _syncedChunkZ = int.MinValue;
    private int _fineColliderCount;
    private bool _hasWindow;
    private bool _usesConservativeFallback;

    [Export("世界路径")]
    public string WorldPath { get; set; } = "/Root/World";

    [Export("角色路径")]
    public string FocusPath { get; set; } = "/Root/Physics/Player";

    [Export("碰撞区块半径")]
    public int CollisionRadius { get; set; } = 1;

    [Export("细粒度碰撞体上限")]
    public int MaxColliderCount { get; set; } = 2048;

    /// <summary>当前由桥接节点生成的物理形状数（包括保守回退盒）。</summary>
    public int GeneratedColliderCount => _generatedShapes.Count;

    /// <summary>当前窗口内是否有未加载或因预算降级为整块阻挡的区块。</summary>
    public bool UsesConservativeFallback => _usesConservativeFallback;

    /// <summary>碰撞窗口实际重建次数，供性能诊断和回归自检读取。</summary>
    public int RebuildCount { get; private set; }

    /// <summary>最近一次地形碰撞同步耗时，供项目性能诊断读取。</summary>
    public double LastSynchronizationMilliseconds { get; private set; }

    protected override void Ready()
    {
        if (CollisionRadius < 0)
        {
            throw new InvalidOperationException("碰撞区块半径不能为负数");
        }

        if (MaxColliderCount <= 0)
        {
            throw new InvalidOperationException("细粒度碰撞体上限必须为正数");
        }

        _worldNode = GetNode<VoxelWorldNode>(WorldPath)
            ?? throw new InvalidOperationException($"体素碰撞桥找不到世界节点: {WorldPath}");
        _focus = GetNode<CharacterBody3D>(FocusPath)
            ?? throw new InvalidOperationException($"体素碰撞桥找不到角色节点: {FocusPath}");
        Synchronize(force: true);
    }

    protected override void PhysicsProcess(double delta) => Synchronize(force: false);

    private void Synchronize(bool force)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
        var world = _worldNode?.World
            ?? throw new InvalidOperationException("体素碰撞桥的世界尚未初始化");
        var focus = _focus
            ?? throw new InvalidOperationException("体素碰撞桥的角色尚未初始化");
        var (chunkX, chunkZ) = ChunkStore.WorldToChunk(
            (int)MathF.Floor(focus.GlobalPosition.X),
            (int)MathF.Floor(focus.GlobalPosition.Z));
        var revision = world.GetTerrainWindowRevision(chunkX, chunkZ, CollisionRadius);
        if (!force && revision == _syncedTerrainRevision &&
            ReferenceEquals(world, _syncedWorld) &&
            chunkX == _syncedChunkX && chunkZ == _syncedChunkZ)
        {
            return;
        }

        if (force || !_hasWindow || !ReferenceEquals(world, _syncedWorld))
        {
            RebuildWindow(world, chunkX, chunkZ);
        }
        else if (chunkX != _syncedChunkX || chunkZ != _syncedChunkZ)
        {
            var deltaX = Math.Abs(chunkX - _syncedChunkX);
            var deltaZ = Math.Abs(chunkZ - _syncedChunkZ);
            if (deltaX <= 1 && deltaZ <= 1)
            {
                ShiftWindow(world, _syncedChunkX, _syncedChunkZ, chunkX, chunkZ);
            }
            else
            {
                RebuildWindow(world, chunkX, chunkZ);
            }
        }
        else
        {
            RebuildChangedChunks(world, chunkX, chunkZ);
        }

        CaptureWindowState(world, chunkX, chunkZ);
        _syncedWorld = world;
        _syncedTerrainRevision = revision;
        _syncedChunkX = chunkX;
        _syncedChunkZ = chunkZ;
        }
        finally
        {
            LastSynchronizationMilliseconds =
                (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private void RebuildWindow(VoxelWorld world, int centerChunkX, int centerChunkZ)
    {
        ClearGeneratedShapes();
        _chunkShapes.Clear();
        _sectionShapes.Clear();
        _sectionFineColliderCounts.Clear();
        _chunkFineColliderCounts.Clear();
        _fallbackChunks.Clear();
        _fineColliderCount = 0;
        _usesConservativeFallback = false;

        for (var chunkZ = centerChunkZ - CollisionRadius; chunkZ <= centerChunkZ + CollisionRadius; chunkZ++)
        {
            for (var chunkX = centerChunkX - CollisionRadius; chunkX <= centerChunkX + CollisionRadius; chunkX++)
            {
                RebuildChunk(world, chunkX, chunkZ);
            }
        }

        var boundaryBoxes = new List<TerrainBox>();
        AddBoundaryWalls(
            boundaryBoxes,
            centerChunkX - CollisionRadius,
            centerChunkX + CollisionRadius,
            centerChunkZ - CollisionRadius,
            centerChunkZ + CollisionRadius);
        AddShapes(boundaryBoxes, null);
        _usesConservativeFallback = _fallbackChunks.Count > 0;
        _hasWindow = true;
        RebuildCount++;
    }

    private void ShiftWindow(VoxelWorld world, int oldCenterChunkX, int oldCenterChunkZ, int newCenterChunkX, int newCenterChunkZ)
    {
        var oldKeys = new HashSet<(int X, int Z)>();
        var newKeys = new HashSet<(int X, int Z)>();
        for (var chunkZ = oldCenterChunkZ - CollisionRadius; chunkZ <= oldCenterChunkZ + CollisionRadius; chunkZ++)
        {
            for (var chunkX = oldCenterChunkX - CollisionRadius; chunkX <= oldCenterChunkX + CollisionRadius; chunkX++)
            {
                oldKeys.Add((chunkX, chunkZ));
            }
        }

        for (var chunkZ = newCenterChunkZ - CollisionRadius; chunkZ <= newCenterChunkZ + CollisionRadius; chunkZ++)
        {
            for (var chunkX = newCenterChunkX - CollisionRadius; chunkX <= newCenterChunkX + CollisionRadius; chunkX++)
            {
                newKeys.Add((chunkX, chunkZ));
            }
        }

        foreach (var key in oldKeys.Except(newKeys))
        {
            RemoveChunkShapes(key);
        }

        foreach (var key in newKeys.Except(oldKeys))
        {
            RebuildChunk(world, key.X, key.Z);
        }

        foreach (var key in oldKeys.Intersect(newKeys))
        {
            var revisionChanged = world.GetTerrainChunkRevision(key.X, key.Z) !=
                _syncedChunkRevisions.GetValueOrDefault(key);
            var presenceChanged = (world.Store.GetChunk(key.X, key.Z) is not null) !=
                _syncedChunkPresence.GetValueOrDefault(key);
            if (revisionChanged || presenceChanged)
            {
                RebuildChunk(world, key.X, key.Z);
            }
        }

        RemoveBoundaryShapes();
        var boundaryBoxes = new List<TerrainBox>();
        AddBoundaryWalls(
            boundaryBoxes,
            newCenterChunkX - CollisionRadius,
            newCenterChunkX + CollisionRadius,
            newCenterChunkZ - CollisionRadius,
            newCenterChunkZ + CollisionRadius);
        AddShapes(boundaryBoxes, null);
        _usesConservativeFallback = _fallbackChunks.Count > 0;
        RebuildCount++;
    }

    private void RebuildChangedChunks(VoxelWorld world, int centerChunkX, int centerChunkZ)
    {
        var changed = new List<(int X, int Z)>();
        for (var chunkZ = centerChunkZ - CollisionRadius; chunkZ <= centerChunkZ + CollisionRadius; chunkZ++)
        {
            for (var chunkX = centerChunkX - CollisionRadius; chunkX <= centerChunkX + CollisionRadius; chunkX++)
            {
                var key = (chunkX, chunkZ);
                var currentRevision = world.GetTerrainChunkRevision(chunkX, chunkZ);
                var currentPresence = world.Store.GetChunk(chunkX, chunkZ) is not null;
                if (currentRevision != _syncedChunkRevisions.GetValueOrDefault(key) ||
                    currentPresence != _syncedChunkPresence.GetValueOrDefault(key))
                {
                    changed.Add(key);
                }
            }
        }

        foreach (var (chunkX, chunkZ) in changed)
        {
            RebuildChunk(world, chunkX, chunkZ);
        }

        _usesConservativeFallback = _fallbackChunks.Count > 0;
        if (changed.Count > 0)
        {
            RebuildCount++;
        }
    }

    private void RebuildChunk(VoxelWorld world, int chunkX, int chunkZ)
    {
        var key = (chunkX, chunkZ);
        var changedSections = world.ConsumeTerrainChangedSectionMask(chunkX, chunkZ);
        if (changedSections != 0 && !_fallbackChunks.Contains(key) &&
            _sectionShapes.Keys.Any(section => section.X == chunkX && section.Z == chunkZ))
        {
            RebuildChangedSections(world, chunkX, chunkZ, changedSections);
            return;
        }

        RemoveChunkShapes(key);
        var boxes = new List<TerrainBox>();
        var fineColliderCount = _fineColliderCount;
        var fallback = false;
        lock (world.Store.SyncRoot)
        {
            var chunk = world.Store.GetChunk(chunkX, chunkZ);
            if (chunk is null || !AppendChunkBoxes(chunk, chunkX, chunkZ, boxes, ref fineColliderCount))
            {
                boxes.Clear();
                AddFullChunkBox(boxes, chunkX, chunkZ);
                fallback = true;
            }
        }

        AddShapes(boxes, key);
        if (fallback)
        {
            _fallbackChunks.Add(key);
        }
        else
        {
            _fallbackChunks.Remove(key);
        }

        _chunkFineColliderCounts[key] = fallback ? 0 : fineColliderCount - _fineColliderCount;
        _fineColliderCount = fallback ? _fineColliderCount : fineColliderCount;
        if (!fallback)
        {
            RegisterSectionFineColliderCounts(key);
        }
    }

    private void RebuildChangedSections(VoxelWorld world, int chunkX, int chunkZ, ulong sectionMask)
    {
        var key = (chunkX, chunkZ);
        var chunk = world.Store.GetChunk(chunkX, chunkZ);
        if (chunk is null)
        {
            RebuildChunk(world, chunkX, chunkZ);
            return;
        }

        for (var sectionIndex = 0; sectionIndex < Chunk.SectionCount; sectionIndex++)
        {
            if ((sectionMask & (1UL << sectionIndex)) == 0)
            {
                continue;
            }

            RemoveSectionShapes(key, sectionIndex);
            var boxes = new List<TerrainBox>();
            var fineColliderCount = _fineColliderCount;
            var minY = Chunk.MinY + sectionIndex * Chunk.SectionSizeY;
            var maxYExclusive = minY + Chunk.SectionSizeY;
            var fallback = false;
            lock (world.Store.SyncRoot)
            {
                if (!AppendChunkBoxes(chunk, chunkX, chunkZ, boxes, ref fineColliderCount, minY, maxYExclusive))
                {
                    boxes.Clear();
                    AddFullSectionBox(boxes, chunkX, chunkZ, sectionIndex);
                    fallback = true;
                }
            }

            AddShapes(boxes, key, sectionIndex);
            var addedFineColliderCount = fallback ? 0 : fineColliderCount - _fineColliderCount;
            _sectionFineColliderCounts[(chunkX, chunkZ, sectionIndex)] = addedFineColliderCount;
            _chunkFineColliderCounts[key] = _chunkFineColliderCounts.GetValueOrDefault(key) + addedFineColliderCount;
            _fineColliderCount = fallback ? _fineColliderCount : fineColliderCount;
            if (fallback)
            {
                _fallbackChunks.Add(key);
            }
        }
    }

    private void AddShapes(List<TerrainBox> boxes, (int X, int Z)? chunkKey, int? sectionIndex = null)
    {
        var shapes = chunkKey is { } key ? new List<CollisionShape3D>(boxes.Count) : null;
        foreach (var box in boxes)
        {
            var shape = new CollisionShape3D
            {
                Name = $"TerrainBox{_generatedShapes.Count}",
                Position = box.Position,
                Shape = new BoxShape3D { Size = box.Size },
            };
            AddChild(shape);
            _generatedShapes.Add(shape);
            shapes?.Add(shape);
            if (chunkKey is null)
            {
                _boundaryShapes.Add(shape);
            }

            if (chunkKey is { } sectionKey)
            {
                var mappedSection = sectionIndex ?? GetBoxSectionIndex(box);
                if (mappedSection >= 0)
                {
                    var sectionKeyWithIndex = (sectionKey.X, sectionKey.Z, mappedSection);
                    if (!_sectionShapes.TryGetValue(sectionKeyWithIndex, out var sectionShapes))
                    {
                        sectionShapes = [];
                        _sectionShapes.Add(sectionKeyWithIndex, sectionShapes);
                    }

                    sectionShapes.Add(shape);
                }
            }
        }

        if (chunkKey is { } chunk)
        {
            if (!_chunkShapes.TryGetValue(chunk, out var chunkShapes))
            {
                chunkShapes = [];
                _chunkShapes.Add(chunk, chunkShapes);
            }

            chunkShapes.AddRange(shapes!);
        }
    }

    private void RemoveChunkShapes((int X, int Z) key)
    {
        if (_chunkShapes.Remove(key, out var shapes))
        {
            foreach (var shape in shapes)
            {
                RemoveChild(shape);
                _generatedShapes.Remove(shape);
            }
        }

        foreach (var sectionKey in _sectionShapes.Keys
                     .Where(section => section.X == key.X && section.Z == key.Z)
                     .ToArray())
        {
            _sectionShapes.Remove(sectionKey);
            _sectionFineColliderCounts.Remove(sectionKey);
        }

        _fineColliderCount -= _chunkFineColliderCounts.GetValueOrDefault(key);
        _chunkFineColliderCounts.Remove(key);
        _fallbackChunks.Remove(key);
    }

    private void RemoveSectionShapes((int X, int Z) key, int sectionIndex)
    {
        var sectionKey = (key.X, key.Z, sectionIndex);
        if (_sectionShapes.Remove(sectionKey, out var shapes))
        {
            foreach (var shape in shapes)
            {
                RemoveChild(shape);
                _generatedShapes.Remove(shape);
                _chunkShapes[key].Remove(shape);
            }
        }

        var fineColliderCount = _sectionFineColliderCounts.GetValueOrDefault(sectionKey);
        _sectionFineColliderCounts.Remove(sectionKey);
        _chunkFineColliderCounts[key] = Math.Max(
            0,
            _chunkFineColliderCounts.GetValueOrDefault(key) - fineColliderCount);
        _fineColliderCount -= fineColliderCount;
    }

    private void RegisterSectionFineColliderCounts((int X, int Z) key)
    {
        foreach (var sectionKey in _sectionShapes.Keys
                     .Where(section => section.X == key.X && section.Z == key.Z)
                     .ToArray())
        {
            _sectionFineColliderCounts[sectionKey] = _sectionShapes[sectionKey].Count;
        }
    }

    private void ClearGeneratedShapes()
    {
        foreach (var shape in _generatedShapes)
        {
            RemoveChild(shape);
        }

        _generatedShapes.Clear();
        _boundaryShapes.Clear();
    }

    private void RemoveBoundaryShapes()
    {
        foreach (var shape in _boundaryShapes)
        {
            RemoveChild(shape);
            _generatedShapes.Remove(shape);
        }

        _boundaryShapes.Clear();
    }

    private static int GetBoxSectionIndex(in TerrainBox box)
    {
        var minY = (int)MathF.Floor(box.Position.Y - box.Size.Y * 0.5f + 1e-5f);
        var maxY = (int)MathF.Ceiling(box.Position.Y + box.Size.Y * 0.5f - 1e-5f) - 1;
        if (!Chunk.IsValidY(minY) || !Chunk.IsValidY(maxY) ||
            Chunk.SectionIndex(minY) != Chunk.SectionIndex(maxY))
        {
            return -1;
        }

        return Chunk.SectionIndex(minY);
    }

    private void CaptureWindowState(VoxelWorld world, int centerChunkX, int centerChunkZ)
    {
        _syncedChunkRevisions.Clear();
        _syncedChunkPresence.Clear();
        for (var chunkZ = centerChunkZ - CollisionRadius; chunkZ <= centerChunkZ + CollisionRadius; chunkZ++)
        {
            for (var chunkX = centerChunkX - CollisionRadius; chunkX <= centerChunkX + CollisionRadius; chunkX++)
            {
                var key = (chunkX, chunkZ);
                _syncedChunkRevisions[key] = world.GetTerrainChunkRevision(chunkX, chunkZ);
                _syncedChunkPresence[key] = world.Store.GetChunk(chunkX, chunkZ) is not null;
            }
        }
    }

    /// <summary>
    /// 先在每个 Y 层做稳定的 X/Z 矩形合并，再把相邻层中完全相同的矩形沿 Y 合并。
    /// 不跨 Section 合并，保证局部 Section 重建仍能精确移除自己的派生节点。
    /// </summary>
    private bool AppendChunkBoxes(
        Chunk chunk,
        int chunkX,
        int chunkZ,
        List<TerrainBox> boxes,
        ref int fineColliderCount,
        int minY = Chunk.MinY,
        int maxYExclusive = Chunk.MaxYExclusive)
    {
        Span<byte> solid = stackalloc byte[Chunk.SizeX * Chunk.SizeZ];
        var previousLayer = new Dictionary<VerticalMergeKey, int>();
        var currentLayer = new Dictionary<VerticalMergeKey, int>();
        for (var y = minY; y < maxYExclusive; y++)
        {
            if (y > minY && (y - Chunk.MinY) % Chunk.SectionSizeY == 0)
            {
                // Section 是增量碰撞重建的最小所有权范围，不能让一个盒体
                // 同时挂到两个 Section，否则编辑其中一侧会留下悬空引用。
                previousLayer.Clear();
            }

            currentLayer.Clear();
            solid.Clear();
            for (var z = 0; z < Chunk.SizeZ; z++)
            {
                for (var x = 0; x < Chunk.SizeX; x++)
                {
                    solid[z * Chunk.SizeX + x] = BlockRegistry.IsSolid(chunk.GetBlock(x, y, z).Id) ? (byte)1 : (byte)0;
                }
            }

            for (var z = 0; z < Chunk.SizeZ; z++)
            {
                for (var x = 0; x < Chunk.SizeX; x++)
                {
                    var start = z * Chunk.SizeX + x;
                    if (solid[start] == 0)
                    {
                        continue;
                    }

                    var width = 1;
                    while (x + width < Chunk.SizeX && solid[z * Chunk.SizeX + x + width] != 0)
                    {
                        width++;
                    }

                    var depth = 1;
                    while (z + depth < Chunk.SizeZ && IsRectangleSolid(solid, x, z + depth, width))
                    {
                        depth++;
                    }

                    for (var clearZ = z; clearZ < z + depth; clearZ++)
                    {
                        for (var clearX = x; clearX < x + width; clearX++)
                        {
                            solid[clearZ * Chunk.SizeX + clearX] = 0;
                        }
                    }

                    var box = new TerrainBox(
                        new Vector3(
                            chunkX * Chunk.SizeX + x + width * 0.5f,
                            y + 0.5f,
                            chunkZ * Chunk.SizeZ + z + depth * 0.5f),
                        new Vector3(width, 1f, depth));
                    var mergeKey = new VerticalMergeKey(box.Position.X, box.Position.Z, box.Size.X, box.Size.Z);
                    if (previousLayer.TryGetValue(mergeKey, out var previousIndex))
                    {
                        var previous = boxes[previousIndex];
                        boxes[previousIndex] = previous with
                        {
                            Position = new Vector3(
                                previous.Position.X,
                                previous.Position.Y + box.Size.Y * 0.5f,
                                previous.Position.Z),
                            Size = new Vector3(previous.Size.X, previous.Size.Y + box.Size.Y, previous.Size.Z),
                        };
                        currentLayer[mergeKey] = previousIndex;
                        continue;
                    }

                    if (fineColliderCount >= MaxColliderCount)
                    {
                        return false;
                    }

                    boxes.Add(box);
                    currentLayer[mergeKey] = boxes.Count - 1;
                    fineColliderCount++;
                }
            }

            // 缺失一层后重新出现的矩形不会进入 currentLayer，因此不能跨
            // 空洞合并；交换字典也避免每个 Y 层重复分配临时集合。
            (previousLayer, currentLayer) = (currentLayer, previousLayer);
        }

        return true;
    }

    private static bool IsRectangleSolid(Span<byte> solid, int x, int z, int width)
    {
        for (var offset = 0; offset < width; offset++)
        {
            if (solid[z * Chunk.SizeX + x + offset] == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddFullChunkBox(List<TerrainBox> boxes, int chunkX, int chunkZ) =>
        boxes.Add(new TerrainBox(
            new Vector3(
                chunkX * Chunk.SizeX + Chunk.SizeX * 0.5f,
                Chunk.MinY + Chunk.SizeY * 0.5f,
                chunkZ * Chunk.SizeZ + Chunk.SizeZ * 0.5f),
            new Vector3(Chunk.SizeX, Chunk.SizeY, Chunk.SizeZ)));

    private static void AddFullSectionBox(List<TerrainBox> boxes, int chunkX, int chunkZ, int sectionIndex)
    {
        var minY = Chunk.MinY + sectionIndex * Chunk.SectionSizeY;
        boxes.Add(new TerrainBox(
            new Vector3(
                chunkX * Chunk.SizeX + Chunk.SizeX * 0.5f,
                minY + Chunk.SectionSizeY * 0.5f,
                chunkZ * Chunk.SizeZ + Chunk.SizeZ * 0.5f),
            new Vector3(Chunk.SizeX, Chunk.SectionSizeY, Chunk.SizeZ)));
    }

    private static void AddBoundaryWalls(
        List<TerrainBox> boxes,
        int minChunkX,
        int maxChunkX,
        int minChunkZ,
        int maxChunkZ)
    {
        var minX = minChunkX * Chunk.SizeX;
        var maxX = (maxChunkX + 1) * Chunk.SizeX;
        var minZ = minChunkZ * Chunk.SizeZ;
        var maxZ = (maxChunkZ + 1) * Chunk.SizeZ;
        var width = maxX - minX;
        var depth = maxZ - minZ;
        var wallHeight = Chunk.SizeY;
        var centerY = Chunk.MinY + wallHeight * 0.5f;

        boxes.Add(new TerrainBox(
            new Vector3(minX - BoundaryThickness * 0.5f, centerY, minZ + depth * 0.5f),
            new Vector3(BoundaryThickness, wallHeight, depth + BoundaryThickness * 2f)));
        boxes.Add(new TerrainBox(
            new Vector3(maxX + BoundaryThickness * 0.5f, centerY, minZ + depth * 0.5f),
            new Vector3(BoundaryThickness, wallHeight, depth + BoundaryThickness * 2f)));
        boxes.Add(new TerrainBox(
            new Vector3(minX + width * 0.5f, centerY, minZ - BoundaryThickness * 0.5f),
            new Vector3(width, wallHeight, BoundaryThickness)));
        boxes.Add(new TerrainBox(
            new Vector3(minX + width * 0.5f, centerY, maxZ + BoundaryThickness * 0.5f),
            new Vector3(width, wallHeight, BoundaryThickness)));
    }

    private readonly record struct TerrainBox(Vector3 Position, Vector3 Size);

    private readonly record struct VerticalMergeKey(float X, float Z, float SizeX, float SizeZ);
}
