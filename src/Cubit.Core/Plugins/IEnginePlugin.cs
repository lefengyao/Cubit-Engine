namespace Cubit.Core.Plugins;

/// <summary>引擎插件的确定性注册、启动与停止契约。</summary>
public interface IEnginePlugin
{
    string Id { get; }

    Version Version { get; }

    void Register(PluginRegistry registry);

    void Start(EngineContext context);

    void Stop();
}
