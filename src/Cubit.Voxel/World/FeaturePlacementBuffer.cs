namespace Cubit.Voxel.World;

/// <summary>一项待提交的稳定方块写入；坐标使用世界坐标，方块使用稳定 Key。</summary>
public readonly record struct FeatureBlockPlacement(int X, int Y, int Z, string BlockKey);

/// <summary>
/// 体素特征的不可变发布结果。worker 只能构建 Builder，主线程在阶段边界消费已发布数组。
/// </summary>
public sealed class FeaturePlacementBuffer
{
    private static readonly FeatureBlockPlacement[] EmptyPlacements = [];
    private readonly FeatureBlockPlacement[] _placements;

    private FeaturePlacementBuffer(FeatureBlockPlacement[] placements)
    {
        _placements = placements;
    }

    public static FeaturePlacementBuffer Empty { get; } = new(EmptyPlacements);

    public IReadOnlyList<FeatureBlockPlacement> Placements => _placements;

    public bool IsEmpty => _placements.Length == 0;

    public sealed class Builder
    {
        private readonly Func<int, int, int, bool> _canPlace;
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        private readonly Dictionary<(int X, int Y, int Z), string> _placements = [];
        private bool _invalid;
        private bool _built;

        internal Builder(Func<int, int, int, bool> canPlace)
        {
            _canPlace = canPlace;
        }

        public void Add(int x, int y, int z, string blockKey)
        {
            EnsureOwnerThread();
            if (_built)
            {
                throw new InvalidOperationException("FeaturePlacementBuffer 已发布，不能继续写入");
            }

            if (string.IsNullOrWhiteSpace(blockKey))
            {
                throw new ArgumentException("特征写入必须使用稳定方块 Key", nameof(blockKey));
            }

            if (!_canPlace(x, y, z))
            {
                _invalid = true;
                return;
            }

            var coordinate = (x, y, z);
            if (_placements.TryGetValue(coordinate, out var existing))
            {
                // 同一特征内树干先于树叶写入；重复坐标保留先到者，避免树叶覆盖树干。
                return;
            }

            _placements.Add(coordinate, blockKey);
        }

        public FeaturePlacementBuffer Build()
        {
            EnsureOwnerThread();
            if (_built)
            {
                throw new InvalidOperationException("FeaturePlacementBuffer Builder 不能重复发布");
            }

            _built = true;
            if (_invalid || _placements.Count == 0)
            {
                return Empty;
            }

            var placements = _placements
                .Select(pair => new FeatureBlockPlacement(pair.Key.X, pair.Key.Y, pair.Key.Z, pair.Value))
                .OrderBy(placement => placement.X)
                .ThenBy(placement => placement.Y)
                .ThenBy(placement => placement.Z)
                .ThenBy(placement => placement.BlockKey, StringComparer.Ordinal)
                .ToArray();
            return new FeaturePlacementBuffer(placements);
        }

        private void EnsureOwnerThread()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                throw new InvalidOperationException("FeaturePlacementBuffer Builder 只能由创建它的 worker 使用");
            }
        }
    }

    public static Builder CreateBuilder(ChunkGenerationContext context) =>
        new(context.CanPlace ?? ((_, _, _) => true));
}
