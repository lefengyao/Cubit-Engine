using Cubit.Audio;
using Cubit.Core.Input;
using Cubit.Core.Jobs;
using Cubit.Core.Plugins;
using Cubit.Core.Project;
using Cubit.Core.Rendering;
using Cubit.Core.Scene;

namespace Cubit.Sample;

/// <summary>
/// 平台运行会话：只加载项目清单声明的 runtime 与 mainScene，绝不创建或解释项目作者节点。
/// </summary>
public sealed class GameScene : IDisposable
{
    private readonly ProjectRuntimeBootstrapSession _session;
    private readonly AudioServer? _audioServer;
    private readonly AudioFramePump? _audioFramePump;

    public GameScene(
        IRenderBackend renderer,
        CubitProject project,
        InputServer inputServer,
        IAudioOutputBackend? audioBackend = null,
        JobSystemOptions? jobSystemOptions = null,
        VulkanContext? projectPresentationVulkan = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(inputServer);

        _session = ProjectRuntimeBootstrapLoader.Create(
            project,
            renderer,
            context => ConfigureHostServices(context, inputServer, audioBackend, projectPresentationVulkan),
            jobSystemOptions);

        if (_session.Runtime.Context.TryGetService<AudioServer>(out var audioServer) && audioServer is not null)
        {
            _audioServer = audioServer;
            if (audioBackend is not null)
            {
                _audioFramePump = new AudioFramePump(audioServer);
            }
        }
    }

    public SceneTree Tree => _session.Runtime.Tree;

    public RenderingServer RenderingServer => _session.Runtime.RenderingServer;

    public ProjectRuntime Runtime => _session.Runtime;

    public IProjectRuntimeBootstrap Bootstrap => _session.Bootstrap;

    /// <summary>平台覆盖层仅读取项目注册的通用调试快照接口。</summary>
    public IProjectDebugOverlaySource? DebugOverlaySource =>
        Runtime.Context.TryGetService<IProjectDebugOverlaySource>(out var source) ? source : null;

    /// <summary>重新从项目清单指定的 mainScene 建立运行树。</summary>
    public void ReloadMainScene() => _session.Runtime.ReloadMainScene();

    /// <summary>加载指定场景并通过项目运行时统一注入服务；用于 MCP 的显式场景切换。</summary>
    public void LoadSceneFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = SceneIO.LoadFromJson(path, Runtime.SceneRegistry);
        _session.Runtime.ReplaceScene(root);
    }

    /// <summary>将物理控制交给项目自己的输入路由；平台不解释其玩法含义。</summary>
    public void RouteProjectInput(ProjectInputEvent inputEvent) => _session.RouteInput(inputEvent);

    public void ProcessFrame(double delta)
    {
        // 运行时新增的作者播放器在下一帧进入已声明的 AudioServer，不由宿主创建节点。
        if (_audioServer is not null)
        {
            AudioSceneBinding.Attach(Tree.Root, _audioServer);
        }

        _session.Runtime.ProcessFrame(delta);
        _audioFramePump?.RenderFrame(delta);
    }

    public void Dispose() => _session.Dispose();

    private static void ConfigureHostServices(
        EngineContext context,
        InputServer inputServer,
        IAudioOutputBackend? audioBackend,
        VulkanContext? projectPresentationVulkan)
    {
        context.AddService(inputServer);
        if (audioBackend is not null)
        {
            context.AddService<IAudioOutputBackend>(audioBackend);
        }

        if (projectPresentationVulkan is not null)
        {
            context.AddService(projectPresentationVulkan);
        }
    }
}
