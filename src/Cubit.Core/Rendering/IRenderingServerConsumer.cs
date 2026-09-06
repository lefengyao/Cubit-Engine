namespace Cubit.Core.Rendering;

/// <summary>需要在进入场景树前由宿主注入渲染服务器的节点契约。</summary>
public interface IRenderingServerConsumer
{
    RenderingServer? Server { get; set; }
}
