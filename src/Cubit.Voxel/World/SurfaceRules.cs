using System.Text.Json;

namespace Cubit.Voxel.World;

/// <summary>
/// 表面规则（借鉴 MC SurfaceRules，docs/04）：有序规则列表，先生成高度图后逐列套用。
/// 规则按顺序匹配，命中即用该方块；未命中回退 deep。
/// </summary>
public sealed class SurfaceRules
{
    public List<Rule> Rules { get; set; } = [];

    public sealed class Rule
    {
        /// <summary>触发条件：top / belowTop / deep；underSea 为附加条件（地表低于海平面）。</summary>
        public string When { get; set; } = "deep";

        /// <summary>belowTop 时距地表的深度（格）。</summary>
        public int Depth { get; set; } = 3;

        public bool UnderSea { get; set; }

        public string Block { get; set; } = "stone";
    }

    /// <summary>默认规则：与 1.0 硬编码行为一致（草/沙/土/石）。</summary>
    public static SurfaceRules Default { get; } = new()
    {
        Rules =
        [
            new Rule { When = "bedrock", Block = "基岩" },
            new Rule { When = "top", UnderSea = true, Block = "沙子" },
            new Rule { When = "top", Block = "草方块" },
            new Rule { When = "belowTop", Depth = 3, UnderSea = true, Block = "沙子" },
            new Rule { When = "belowTop", Depth = 3, Block = "泥土" },
            new Rule { When = "deep", Block = "石头" },
        ],
    };

    public static SurfaceRules FromJson(string json) =>
        JsonSerializer.Deserialize<SurfaceRules>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? Default;

    /// <summary>解析某列的方块：噪声地形从 Chunk.MinY 延伸到 height，height 为地表高度。</summary>
    public ushort Resolve(int y, int height, int seaLevel)
    {
        foreach (var rule in Rules)
        {
            if (!Matches(rule, y, height, seaLevel))
            {
                continue;
            }

            var id = BlockRegistry.FindId(rule.Block);
            if (id is null)
            {
                throw new InvalidOperationException($"表面规则引用了未注册方块: {rule.Block}");
            }

            return id.Value;
        }

        return BlockRegistry.Stone;
    }

    private static bool Matches(Rule rule, int y, int height, int seaLevel)
    {
        if (rule.UnderSea && height > seaLevel + 1)
        {
            return false;
        }

        return rule.When switch
        {
            "bedrock" => y == Chunk.MinY,
            "top" => y == height,
            "belowTop" => y >= height - rule.Depth && y < height,
            "deep" => true,
            _ => false,
        };
    }
}
