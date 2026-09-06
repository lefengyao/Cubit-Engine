using Cubit.Core.Plugins;
using Cubit.Core.Scene;

namespace Cubit.Core.Project;

/// <summary>项目在 Scene-First 运行前声明插件、类型与项目服务的显式入口。</summary>
public interface IProjectRuntimeBootstrap
{
    /// <summary>返回本项目允许使用的插件实例；最终启用集合仍由 project.cubit.json 过滤。</summary>
    IEnumerable<IEnginePlugin> CreateAvailablePlugins();

    /// <summary>在主场景反序列化前注册项目 Node/Resource 类型。</summary>
    void RegisterProjectTypes(CubitProject project, SceneRegistry registry);

    /// <summary>在已声明插件启动后注册项目运行服务；不得创建作者节点。</summary>
    void ConfigureRuntime(EngineContext context);

    /// <summary>在已反序列化根节点进入树前进行项目配置；不得补齐缺失作者节点。</summary>
    void ConfigureLoadedScene(Node root, EngineContext context);

    /// <summary>创建可选的项目原始输入路由器；没有领域输入时返回 null。</summary>
    IProjectInputRouter? CreateInputRouter(EngineContext context);
}

/// <summary>项目将平台原始控制映射为自身玩法输入的边界。</summary>
public interface IProjectInputRouter
{
    void Handle(ProjectInputEvent inputEvent);
}
