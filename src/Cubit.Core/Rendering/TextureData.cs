namespace Cubit.Core.Rendering;

/// <summary>通用 RGBA8 纹理采样策略。</summary>
public enum TextureFilter
{
    Nearest,
    Linear,
}

/// <summary>RGBA8 数据的解释颜色空间。</summary>
public enum TextureColorSpace
{
    Srgb,
    Linear,
}

/// <summary>渲染服务器接收的通用纹理数据；不携带图集或游戏领域语义。</summary>
public sealed class TextureData
{
    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Rgba8 { get; }

    public TextureFilter Filter { get; }

    public TextureColorSpace ColorSpace { get; }

    public TextureData(int width, int height, byte[] rgba8, TextureFilter filter = TextureFilter.Linear, TextureColorSpace colorSpace = TextureColorSpace.Srgb)
    {
        ArgumentNullException.ThrowIfNull(rgba8);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "纹理尺寸必须为正数");
        }

        var expectedLength = checked(width * height * 4);
        if (rgba8.Length != expectedLength)
        {
            throw new ArgumentException($"RGBA8 数据长度必须为 {expectedLength}", nameof(rgba8));
        }

        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), "未知纹理采样策略");
        }

        if (!Enum.IsDefined(colorSpace))
        {
            throw new ArgumentOutOfRangeException(nameof(colorSpace), "未知纹理颜色空间");
        }

        Width = width;
        Height = height;
        Rgba8 = rgba8.ToArray();
        Filter = filter;
        ColorSpace = colorSpace;
    }
}

