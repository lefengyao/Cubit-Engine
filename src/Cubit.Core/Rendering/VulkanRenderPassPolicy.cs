using Silk.NET.Vulkan;

namespace Cubit.Core.Rendering;

/// <summary>集中定义 Vulkan 场景颜色附件的布局契约，供交换链与离屏目标共用。</summary>
internal static class VulkanRenderPassPolicy
{
    internal static RenderPass SelectSceneRenderPass(
        bool offscreen,
        RenderPass swapchainRenderPass,
        RenderPass offscreenRenderPass) => offscreen ? offscreenRenderPass : swapchainRenderPass;

    internal static AttachmentDescription CreateSceneColorAttachment(Format format, bool offscreen) => new()
    {
        Format = format,
        Samples = SampleCountFlags.Count1Bit,
        LoadOp = AttachmentLoadOp.Clear,
        StoreOp = AttachmentStoreOp.Store,
        StencilLoadOp = AttachmentLoadOp.DontCare,
        StencilStoreOp = AttachmentStoreOp.DontCare,
        InitialLayout = ImageLayout.Undefined,
        FinalLayout = offscreen ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.PresentSrcKhr,
    };

    internal static SubpassDependency CreateOffscreenReadDependency() => new()
    {
        SrcSubpass = 0,
        DstSubpass = uint.MaxValue,
        SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit,
        DstStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.EarlyFragmentTestsBit,
        SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
        DstAccessMask = AccessFlags.ShaderReadBit
            | AccessFlags.DepthStencilAttachmentReadBit
            | AccessFlags.DepthStencilAttachmentWriteBit,
        DependencyFlags = DependencyFlags.ByRegionBit,
    };

    internal static AttachmentDescription CreateSwapchainOverlayColorAttachment(Format format) => new()
    {
        Format = format,
        Samples = SampleCountFlags.Count1Bit,
        LoadOp = AttachmentLoadOp.Load,
        StoreOp = AttachmentStoreOp.Store,
        StencilLoadOp = AttachmentLoadOp.DontCare,
        StencilStoreOp = AttachmentStoreOp.DontCare,
        InitialLayout = ImageLayout.PresentSrcKhr,
        FinalLayout = ImageLayout.PresentSrcKhr,
    };

    internal static AttachmentDescription CreateOverlayDepthAttachment() => new()
    {
        Format = Format.D32Sfloat,
        Samples = SampleCountFlags.Count1Bit,
        LoadOp = AttachmentLoadOp.DontCare,
        StoreOp = AttachmentStoreOp.DontCare,
        StencilLoadOp = AttachmentLoadOp.DontCare,
        StencilStoreOp = AttachmentStoreOp.DontCare,
        InitialLayout = ImageLayout.Undefined,
        FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
    };

    internal static PipelineDepthStencilStateCreateInfo CreateOverlayDepthStencilState() => new()
    {
        SType = StructureType.PipelineDepthStencilStateCreateInfo,
        DepthTestEnable = false,
        DepthWriteEnable = false,
        DepthCompareOp = CompareOp.Always,
        DepthBoundsTestEnable = false,
        StencilTestEnable = false,
    };

    internal static bool CanDestroySwapchainResources(Result fenceWaitResult) => fenceWaitResult == Result.Success;

    internal static bool CanReuseFrameSlot(Result fenceWaitResult) => fenceWaitResult == Result.Success;

    internal static bool MustResetFenceBeforeSubmit(bool wasPending, Result fenceWaitResult) =>
        wasPending && fenceWaitResult == Result.Success;

    internal static bool RequiresFormatRebuild(Format previous, Format current) =>
        previous != default && previous != current;
}
