using System.Text.Json;

namespace Cubit.Mcp;

/// <summary>
/// MCP 引擎宿主抽象：把引擎（场景树/世界/运行控制/截图）暴露给 MCP 工具。
/// 由具体宿主（游戏场景/无头工具）实现，Cubit.Mcp 不依赖具体游戏。
/// </summary>
public interface IMcpEngineHost
{
    // engine
    string EngineInfo();

    // scene
    string DescribeScene();

    string GetSceneProperty(string nodePath, string property);

    string SetSceneProperty(string nodePath, string property, string jsonValue);

    string ListSceneProperties(string nodePath);

    string CallSceneMethod(string nodePath, string method, string? jsonArgs);

    string ListGroups();

    // scene-edit
    string AddSceneNode(string parentPath, string type, string name);

    string RemoveSceneNode(string nodePath);

    string SaveScene(string path);

    /// <summary>加载 .cscene 场景文件；apply=true 替换运行树，apply=false 仅预览。</summary>
    string LoadScene(string path, bool apply);

    /// <summary>恢复默认场景。</summary>
    string ResetScene();

    // run
    string RunStatus();

    string RunCommand(string command);

    string StepTick();

    string SetPaused(bool paused);

    string SetSpeed(float rate);

    // input
    string InputSetAction(string action, bool pressed);

    string InputSetMoveAxis(float x, float y);

    string InputAddLookDelta(float dx, float dy);

    string InputSnapshot();
    // capture
    string CaptureScreenshot(string path);

    string CaptureStats();
}
