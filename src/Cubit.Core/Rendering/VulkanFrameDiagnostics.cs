using Silk.NET.Vulkan;

namespace Cubit.Core.Rendering;

/// <summary>统一格式化首帧交换链诊断，便于在真机上定位 acquire、submit 与 present 的边界。</summary>
internal static class VulkanFrameDiagnostics
{
    internal static string FormatStartup(
        uint graphicsQueueFamily,
        uint presentQueueFamily,
        uint width,
        uint height,
        uint imageCount,
        PresentModeKHR presentMode,
        CompositeAlphaFlagsKHR compositeAlpha) =>
        $"[Cubit] Vulkan 交换链诊断: graphicsFamily={graphicsQueueFamily}, presentFamily={presentQueueFamily}, " +
        $"extent={width}x{height}, images={imageCount}, presentMode={presentMode}, compositeAlpha={compositeAlpha}";

    internal static string FormatFrame(Result acquire, uint? imageIndex, Result? submit, Result? present) =>
        $"[Cubit] Vulkan 首帧诊断: acquire={acquire}, image={DescribeImage(imageIndex)}, " +
        $"submit={DescribeResult(submit)}, present={DescribeResult(present)}";

    private static string DescribeImage(uint? imageIndex) => imageIndex?.ToString() ?? "<not-acquired>";

    private static string DescribeResult(Result? result) => result?.ToString() ?? "<not-run>";
}
