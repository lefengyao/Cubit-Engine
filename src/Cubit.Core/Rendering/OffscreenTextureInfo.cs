using Silk.NET.Vulkan;

namespace Cubit.Core.Rendering;

/// <summary>某个在途帧槽的离屏颜色纹理句柄；版本变化表示描述符必须刷新。</summary>
public readonly record struct OffscreenTextureInfo(
    ulong Version,
    ImageView View,
    Sampler Sampler,
    int Width,
    int Height)
{
    public bool IsReady => Version != 0 && View.Handle != 0 && Sampler.Handle != 0;
}
