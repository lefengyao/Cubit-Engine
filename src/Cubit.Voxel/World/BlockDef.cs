using System.Text.Json.Serialization;
using Cubit.Core.Scene;

namespace Cubit.Voxel.World;

/// <summary>
/// 方块定义（继承 Resource）：一个方块一条定义（纹理、物理标记），可 JSON 序列化。
/// 逐个注册、逐个实现，见 <see cref="BlockRegistry"/>。
/// </summary>
public sealed class BlockDef : Resource
{
    /// <summary>运行时数字 ID；只用于当前会话和兼容旧存档，不作为内容身份。</summary>
    [Export("运行时 ID")]
    public ushort Id { get; init; }

    /// <summary>稳定内容 ID，格式为 namespace:block_name。</summary>
    [Export("稳定 ID")]
    public string Key { get; init; } = "";

    [Export("名称")]
    public required string Name { get; init; }

    /// <summary>顶面纹理名（assets 内文件名，不含 .png）。</summary>
    [Export("顶面纹理")]
    public string TopTexture { get; init; } = "";

    /// <summary>侧面纹理名。</summary>
    [Export("侧面纹理")]
    public string SideTexture { get; init; } = "";

    /// <summary>底面纹理名；留空则用侧面。</summary>
    [Export("底面纹理")]
    public string BottomTexture { get; init; } = "";

    /// <summary>应用到方块纹理的 RGB 色调；#ffffff 表示保持原图颜色。</summary>
    [Export("渲染色调")]
    public string Tint { get; init; } = "#ffffff";

    /// <summary>色调应用的立方体面；默认全部面，保持既有方块与交叉面兼容。</summary>
    [Export("色调面")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BlockTintFaces TintFaces { get; init; } = BlockTintFaces.All;

    /// <summary>方块使用的通用网格形状。</summary>
    [Export("渲染形状")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BlockRenderShape RenderShape { get; init; } = BlockRenderShape.Cube;

    /// <summary>是否参与实体碰撞。</summary>
    [Export("实体")]
    public bool Solid { get; init; } = true;

    /// <summary>是否遮挡相邻面。</summary>
    [Export("遮挡")]
    public bool Opaque { get; init; } = true;

    /// <summary>发光等级 0~15（方块光光源；0 = 不发光）。</summary>
    [Export("发光等级")]
    public int LightLevel { get; init; } = 0;

    /// <summary>光线通过方块时的衰减等级 0~15；流体必须大于 0。</summary>
    [Export("透光衰减")]
    public int LightAttenuation { get; init; } = 0;

    /// <summary>规范化内容包中的十六进制 RGB 色调。</summary>
    public static string NormalizeTint(string? tint)
    {
        var value = string.IsNullOrWhiteSpace(tint) ? "#ffffff" : tint.Trim().ToLowerInvariant();
        if (value.Length != 7 || value[0] != '#' ||
            !uint.TryParse(value[1..], System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidDataException($"方块渲染色调必须为 #rrggbb: {tint}");
        }

        return value;
    }

    /// <summary>将内容包色调转换为 RGB 数值，供图集和网格路径使用。</summary>
    public static uint GetTintRgb(string? tint) => uint.Parse(
        NormalizeTint(tint)[1..],
        System.Globalization.NumberStyles.AllowHexSpecifier,
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>拒绝未定义的色调面位，避免内容包静默改变图集语义。</summary>
    public static BlockTintFaces NormalizeTintFaces(BlockTintFaces faces) =>
        (faces & ~BlockTintFaces.All) == 0
            ? faces
            : throw new InvalidDataException($"方块色调面包含未知位: {faces}");
}
