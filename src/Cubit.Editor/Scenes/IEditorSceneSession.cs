using Cubit.Core.Rendering;
using Cubit.Core.Scene;
using Cubit.Core.Plugins;

namespace Cubit.Editor.Scenes;

/// <summary>编辑器可加载场景的最小会话契约。</summary>
public interface IEditorSceneSession : IDisposable
{
    string DisplayName { get; }

    string? ScenePath { get; }

    SceneTree Tree { get; }

    RenderingServer RenderingServer { get; }

    /// <summary>当前会话可创建的节点和资源类型。</summary>
    SceneRegistry SceneRegistry { get; }

    /// <summary>当前项目会话自己的插件注册表；编辑器扩展只从这里读取。</summary>
    PluginRegistry PluginRegistry { get; }

    void ProcessFrame(double delta);

    void Save();
}
