namespace Cubit.Core.Project;

/// <summary>Cubit 项目清单；描述项目身份、主场景入口和可寻址作者场景目录。</summary>
public sealed class CubitProjectManifest
{
    public int FormatVersion { get; init; }

    public string Name { get; init; } = "";

    public string MainScene { get; init; } = "";

    /// <summary>可选作者场景目录；为空时由 ProjectSceneCatalog 建立单场景兼容目录。</summary>
    public List<CubitProjectSceneReference> Scenes { get; init; } = [];

    public List<CubitPluginReference> Plugins { get; init; } = [];

    /// <summary>可选项目 feature/content/presentation/tool 模块目录；不触发隐式程序集加载。</summary>
    public List<CubitProjectModuleReference> Modules { get; init; } = [];

    /// <summary>可选的显式运行时预览入口；普通打开项目时不会执行。</summary>
    public CubitProjectPreviewReference? Preview { get; init; }

    /// <summary>可选的正式运行入口；平台宿主只能按此声明加载项目代码。</summary>
    public CubitProjectRuntimeReference? Runtime { get; init; }

    /// <summary>可选编辑器集成入口；只用于注册作者节点，不启动项目运行服务。</summary>
    public CubitProjectEditorReference? Editor { get; init; }

    /// <summary>可选的桌面呈现入口；仅桌面渲染宿主按声明加载，Android 不加载桌面 UI。</summary>
    public CubitProjectDesktopPresentationReference? DesktopPresentation { get; init; }
}
