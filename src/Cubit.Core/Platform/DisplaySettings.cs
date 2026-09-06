namespace Cubit.Core.Platform;

/// <summary>渲染矩形（整数像素坐标，原点在左上角）。</summary>
public readonly record struct IntRect(int X, int Y, int Width, int Height);

/// <summary>
/// 引擎显示设置（借鉴 Godot DisplayServer / Viewport stretch）：
/// 逻辑窗口尺寸决定渲染分辨率；KeepAspect 时，物理宽高比不匹配则居中留黑边（letterbox），画面永不变形。
/// 本类为纯逻辑，便于无头单测（displaycheck）。
/// </summary>
public sealed class DisplaySettings
{
    /// <summary>把指定内容按原始宽高比居中放入容器，供通用 Viewport/纹理预览使用。</summary>
    public static IntRect ComputeAspectFitRect(int containerWidth, int containerHeight, int contentWidth, int contentHeight)
    {
        if (containerWidth <= 0 || containerHeight <= 0 || contentWidth <= 0 || contentHeight <= 0)
        {
            return new IntRect(0, 0, 0, 0);
        }

        var scale = MathF.Min(containerWidth / (float)contentWidth, containerHeight / (float)contentHeight);
        var width = Math.Max(1, (int)(contentWidth * scale));
        var height = Math.Max(1, (int)(contentHeight * scale));
        return new IntRect((containerWidth - width) / 2, (containerHeight - height) / 2, width, height);
    }

    /// <summary>逻辑窗口宽度（引擎设置，桌面与移动端一致）。</summary>
    public int LogicalWidth { get; set; } = 1280;

    /// <summary>逻辑窗口高度（引擎设置，桌面与移动端一致）。</summary>
    public int LogicalHeight { get; set; } = 720;

    /// <summary>保持宽高比：true = letterbox 防变形；false = 铺满（可能拉伸）。</summary>
    public bool KeepAspect { get; set; } = true;

    /// <summary>
    /// 按物理表面尺寸计算居中渲染区域（纯函数）。
    /// 返回的矩形宽高比 ≈ 逻辑宽高比（KeepAspect 时），偏移保证居中。
    /// </summary>
    public IntRect ComputeRenderRect(int physicalWidth, int physicalHeight)
    {
        if (physicalWidth <= 0 || physicalHeight <= 0)
        {
            return new IntRect(0, 0, physicalWidth, physicalHeight);
        }

        if (!KeepAspect || LogicalWidth <= 0 || LogicalHeight <= 0)
        {
            return new IntRect(0, 0, physicalWidth, physicalHeight);
        }

        var logicalAspect = LogicalWidth / (float)LogicalHeight;
        var physicalAspect = physicalWidth / (float)physicalHeight;
        int renderWidth, renderHeight;
        if (physicalAspect > logicalAspect)
        {
            // 物理更宽：高度铺满，宽度按逻辑比例收缩 → 左右黑边
            renderHeight = physicalHeight;
            renderWidth = (int)(physicalHeight * logicalAspect);
        }
        else
        {
            // 物理更高：宽度铺满，高度按逻辑比例收缩 → 上下黑边
            renderWidth = physicalWidth;
            renderHeight = (int)(physicalWidth / logicalAspect);
        }

        if (renderWidth <= 0 || renderHeight <= 0)
        {
            return new IntRect(0, 0, physicalWidth, physicalHeight);
        }

        return new IntRect(
            (physicalWidth - renderWidth) / 2,
            (physicalHeight - renderHeight) / 2,
            renderWidth,
            renderHeight);
    }
}
