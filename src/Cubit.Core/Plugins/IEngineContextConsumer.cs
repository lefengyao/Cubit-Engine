namespace Cubit.Core.Plugins;

/// <summary>需要项目或插件服务的节点在进入场景树前接收统一引擎上下文。</summary>
public interface IEngineContextConsumer
{
    void SetEngineContext(EngineContext context);
}
