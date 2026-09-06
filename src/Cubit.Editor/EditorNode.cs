using System.Numerics;
using System.Reflection;
using Cubit.Core.Platform;
using Cubit.Core.Plugins;
using Cubit.Core.Project;
using Cubit.Core.Rendering;
using Cubit.Core.Scene;
using Cubit.Editor.Contracts;
using Cubit.Editor.Mcp;
using Cubit.Editor.Scenes;
using Hexa.NET.ImGui;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using ImGuiApi = global::Hexa.NET.ImGui.ImGui;
using ImGuiRenderer = global::Cubit.ImGui.Rendering.ImGuiRenderer;

namespace Cubit.Editor;

/// <summary>
/// 编辑器节点（借鉴 Godot EditorNode）：编辑器 = 引擎 + 空项目。
/// 项目场景只在用户显式打开项目后加载，并渲染到中央"视口"面板（离屏纹理）。
/// </summary>
public sealed class EditorNode : IDisposable
{
    private const ulong SceneTextureId = ulong.MaxValue;
    private readonly VulkanContext _vulkan;
    private readonly ImGuiRenderer _imgui;
    private readonly Action<CommandBuffer, Framebuffer, Extent2D, Format, uint> _drawOverlay;
    private IEditorSceneSession? _session;
    private Node? _selected;
    private readonly ulong[] _lastSceneTextureVersions;
    private string _projectDialogPath = Environment.CurrentDirectory;
    private string _statusText = "未打开项目";
    private bool _openProjectPopup;
    private bool _showEngineInfo = false;
    private bool _showSceneTree = true;
    private bool _showInspector = true;
    private bool _showViewport = true;
    private bool _showPluginExtensions;
    private bool _nodeCreatePopup;
    private string _nodeCreateFilter = string.Empty;
    private bool _renamePopup;
    private string _renameBuffer = string.Empty;
    private bool _disposed;
    private bool _controlDown;
    private bool _shiftDown;
    private readonly EditorCommandHistory _commandHistory = new();
    private readonly List<string> _nodeCreateHistory = [];
    private readonly Dictionary<string, object> _editorExtensionInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _projectEditorExtensionInstances = new(StringComparer.OrdinalIgnoreCase);

    internal EditorMcpHost McpHost { get; }

    public EditorCommandHistory CommandHistory => _commandHistory;

    public EditorNode(VulkanContext vulkan)
    {
        _vulkan = vulkan;
        _imgui = new ImGuiRenderer(vulkan);
        _lastSceneTextureVersions = new ulong[vulkan.FramesInFlight];
        _drawOverlay = (cmd, fb, extent, _, frameSlot) =>
        {
            RegisterSceneTexture(frameSlot);
            _imgui.Draw(cmd, fb, extent, frameSlot);
        };
        vulkan.Overlay += _drawOverlay;
        McpHost = new EditorMcpHost(() => _session, OpenProjectFromMcp, vulkan);
    }

    /// <summary>每帧：新建 ImGui 帧 → 绘制编辑器 UI → 结束帧生成绘制数据。</summary>
    public void Update(double delta)
    {
        McpHost.PumpMcpRequests();
        var io = ImGuiApi.GetIO();
        _imgui.NewFrame(io, delta);
        BuildUI();
        _imgui.Render();
        _session?.ProcessFrame(delta);
    }

    /// <summary>把引擎离屏场景纹理注册给 ImGui（视口显示用）。</summary>
    private void RegisterSceneTexture(uint frameSlot)
    {
        var texture = _vulkan.GetOffscreenTexture(frameSlot);
        if (texture.IsReady && texture.Version != _lastSceneTextureVersions[frameSlot])
        {
            _imgui.SetTextureSet(SceneTextureId, frameSlot, texture.View, texture.Sampler);
            _lastSceneTextureVersions[frameSlot] = texture.Version;
        }
    }

    /// <summary>显式打开正式 Cubit 项目；失败时保留当前会话。</summary>
    public bool OpenProject(string path)
    {
        try
        {
            var replacement = new ProjectSceneSession(_vulkan, path);
            ReplaceSession(replacement);
            _projectDialogPath = replacement.Project.RootDirectory;
            _statusText = replacement.HasCamera
                ? $"项目: {replacement.DisplayName}"
                : $"项目: {replacement.DisplayName}（未找到 Camera3D，视口仅显示清屏色）";
            Console.WriteLine($"[CubitEditor] 已打开项目: {replacement.Project.RootDirectory}");
            return true;
        }
        catch (Exception ex)
        {
            _statusText = $"打开项目失败: {ex.Message}";
            Console.Error.WriteLine($"[CubitEditor] {_statusText}");
            return false;
        }
    }

    private void OpenProjectFromMcp(string path)
    {
        if (!OpenProject(path))
        {
            throw new InvalidOperationException($"Editor 无法打开项目: {path}");
        }
    }

    private void ReplaceSession(IEditorSceneSession replacement)
    {
        var previous = _session;
        _session = replacement;
        _selected = null;
        _commandHistory.Clear();
        _nodeCreateHistory.Clear();
        _editorExtensionInstances.Clear();
        _projectEditorExtensionInstances.Clear();
        previous?.Dispose();
    }

    // ---- 输入（桌面 GLFW 回调灌入；编辑器模式玩家输入默认禁用）----

    public void OnMouseMove(float x, float y) => ImGuiApi.GetIO().AddMousePosEvent(x, y);

    public void OnMouseButton(MouseButton button, bool down)
    {
        var index = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            _ => -1,
        };
        if (index >= 0)
        {
            ImGuiApi.GetIO().AddMouseButtonEvent(index, down);
        }
    }

    public void OnMouseWheel(float y) => ImGuiApi.GetIO().AddMouseWheelEvent(0, y);

    public void OnKey(Key key, bool down)
    {
        if (key is Key.ControlLeft or Key.ControlRight)
        {
            _controlDown = down;
        }
        else if (key is Key.ShiftLeft or Key.ShiftRight)
        {
            _shiftDown = down;
        }

        if (down && _controlDown)
        {
            if (key == Key.Z)
            {
                if (_shiftDown)
                {
                    Redo();
                }
                else
                {
                    Undo();
                }
            }
            else if (key == Key.Y)
            {
                Redo();
            }
        }

        if (TryMapKey(key, out var imguiKey))
        {
            ImGuiApi.GetIO().AddKeyEvent(imguiKey, down);
        }
    }

    private static bool TryMapKey(Key key, out ImGuiKey imguiKey)
    {
        imguiKey = key switch
        {
            Key.W => ImGuiKey.W,
            Key.A => ImGuiKey.A,
            Key.S => ImGuiKey.S,
            Key.D => ImGuiKey.D,
            Key.Q => ImGuiKey.Q,
            Key.E => ImGuiKey.E,
            Key.Space => ImGuiKey.Space,
            Key.ShiftLeft or Key.ShiftRight => ImGuiKey.ModShift,
            Key.ControlLeft or Key.ControlRight => ImGuiKey.ModCtrl,
            Key.AltLeft or Key.AltRight => ImGuiKey.ModAlt,
            Key.Enter => ImGuiKey.Enter,
            Key.Escape => ImGuiKey.Escape,
            Key.Tab => ImGuiKey.Tab,
            Key.Z => ImGuiKey.Z,
            Key.Y => ImGuiKey.Y,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            Key.Delete => ImGuiKey.Delete,
            Key.Backspace => ImGuiKey.Backspace,
            _ => (ImGuiKey)(-1),
        };
        return (int)imguiKey >= 0;
    }

    // ---- UI（Godot 风格：菜单栏 + 左侧场景树 + 中央视口 + 右侧属性）----

    private unsafe void BuildUI()
    {
        var io = ImGuiApi.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        if (ImGuiApi.BeginMainMenuBar())
        {
            if (ImGuiApi.BeginMenu("文件"))
            {
                if (ImGuiApi.MenuItem("打开项目..."))
                {
                    _openProjectPopup = true;
                }

                if (ImGuiApi.MenuItem("保存场景") && _session is not null)
                {
                    _session.Save();
                    _statusText = $"已保存: {_session.ScenePath}";
                }

                ImGuiApi.Separator();
                if (ImGuiApi.MenuItem("退出"))
                {
                    // 由宿主处理
                }

                ImGuiApi.EndMenu();
            }

            if (ImGuiApi.BeginMenu("运行"))
            {
                if (_session is IEditorPreviewSession previewSession)
                {
                    if (previewSession.IsPreviewing)
                    {
                        if (ImGuiApi.MenuItem("停止项目预览"))
                        {
                            try
                            {
                                previewSession.StopPreview();
                                _statusText = $"已停止预览: {_session.DisplayName}";
                            }
                            catch (Exception exception)
                            {
                                _statusText = $"停止预览失败: {exception.Message}";
                            }
                        }
                    }
                    else if (previewSession.CanStartPreview && ImGuiApi.MenuItem("运行项目预览"))
                    {
                        try
                        {
                            previewSession.StartPreview();
                            _statusText = $"正在预览: {_session.DisplayName}";
                        }
                        catch (Exception exception)
                        {
                            _statusText = $"启动预览失败: {exception.Message}";
                        }
                    }
                }

                if (ImGuiApi.MenuItem("暂停/继续"))
                {
                    if (_session is not null)
                    {
                        _session.Tree.Paused = !_session.Tree.Paused;
                    }
                }

                if (ImGuiApi.MenuItem("单步 tick"))
                {
                    if (_session is not null)
                    {
                        _session.Tree.ProcessFrame(_session.Tree.PhysicsDelta);
                    }
                }

                ImGuiApi.EndMenu();
            }

            if (_session is ProjectSceneSession sceneSession && ImGuiApi.BeginMenu("场景"))
            {
                foreach (var category in sceneSession.Project.SceneCatalog.Entries
                             .GroupBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!ImGuiApi.BeginMenu(category.Key))
                    {
                        continue;
                    }

                    foreach (var entry in category.OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase))
                    {
                        var marker = string.Equals(entry.Id, sceneSession.CurrentSceneId, StringComparison.OrdinalIgnoreCase)
                            ? " [当前]"
                            : string.Empty;
                        if (ImGuiApi.MenuItem($"{entry.Id}  {entry.Path}{marker}"))
                        {
                            try
                            {
                                sceneSession.OpenScene(entry.Id);
                                _selected = null;
                                _statusText = $"已打开场景: {entry.Path}";
                            }
                            catch (Exception exception)
                            {
                                _statusText = $"打开场景失败: {exception.Message}";
                            }
                        }
                    }

                    ImGuiApi.EndMenu();
                }

                ImGuiApi.EndMenu();
            }

            if (ImGuiApi.BeginMenu("编辑器"))
            {
                if (ImGuiApi.MenuItem("插件扩展", string.Empty, _showPluginExtensions))
                {
                    _showPluginExtensions = !_showPluginExtensions;
                }

                ImGuiApi.EndMenu();
            }

            if (ImGuiApi.BeginMenu("编辑"))
            {
                var undoLabel = _commandHistory.UndoDescription is null
                    ? "撤销"
                    : $"撤销 {_commandHistory.UndoDescription}";
                var redoLabel = _commandHistory.RedoDescription is null
                    ? "重做"
                    : $"重做 {_commandHistory.RedoDescription}";
                if (ImGuiApi.MenuItem(undoLabel, "Ctrl+Z", false, _commandHistory.CanUndo))
                {
                    Undo();
                }

                if (ImGuiApi.MenuItem(redoLabel, "Ctrl+Y", false, _commandHistory.CanRedo))
                {
                    Redo();
                }

                ImGuiApi.EndMenu();
            }

            if (ImGuiApi.BeginMenu("节点"))
            {
                if (ImGuiApi.MenuItem("添加节点...", "Ctrl+A", false, _session is not null))
                {
                    _nodeCreatePopup = true;
                }

                if (ImGuiApi.MenuItem("重命名选中节点", "F2", false, _selected?.Parent is not null))
                {
                    _renameBuffer = _selected?.Name ?? string.Empty;
                    _renamePopup = true;
                }

                if (ImGuiApi.MenuItem("删除选中节点", "Delete", false, _selected?.Parent is not null))
                {
                    DeleteSelectedNode();
                }

                ImGuiApi.EndMenu();
            }

            ImGuiApi.Separator();
            ImGuiApi.TextUnformatted(_statusText);

            ImGuiApi.EndMainMenuBar();
        }

        if (_openProjectPopup)
        {
            ImGuiApi.OpenPopup("打开 Cubit 项目");
            _openProjectPopup = false;
        }

        if (ImGuiApi.BeginPopupModal("打开 Cubit 项目", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGuiApi.SetNextItemWidth(620f);
            ImGuiApi.InputText("项目目录", ref _projectDialogPath, 1024);
            if (ImGuiApi.Button("打开"))
            {
                if (OpenProject(_projectDialogPath))
                {
                    ImGuiApi.CloseCurrentPopup();
                }
            }

            ImGuiApi.SameLine();
            if (ImGuiApi.Button("取消"))
            {
                ImGuiApi.CloseCurrentPopup();
            }

            ImGuiApi.EndPopup();
        }

        if (_nodeCreatePopup)
        {
            ImGuiApi.OpenPopup("添加节点");
            _nodeCreatePopup = false;
        }

        if (ImGuiApi.BeginPopupModal("添加节点", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGuiApi.SetNextItemWidth(420f);
            ImGuiApi.InputText("搜索", ref _nodeCreateFilter, 256);
            var entries = _session is null
                ? Array.Empty<NodeTypeEntry>()
                : GetNodeTypeEntriesForProject(
                    _session.SceneRegistry,
                    _session.PluginRegistry,
                    (_session as ProjectSceneSession)?.ProjectEditorRegistry);
            var matchingEntries = entries
                .Where(entry => string.IsNullOrWhiteSpace(_nodeCreateFilter) ||
                    entry.TypeName.Contains(_nodeCreateFilter, StringComparison.OrdinalIgnoreCase) ||
                    entry.Owner.Contains(_nodeCreateFilter, StringComparison.OrdinalIgnoreCase) ||
                    entry.Group.Contains(_nodeCreateFilter, StringComparison.OrdinalIgnoreCase))
                .Take(80)
                .ToArray();
            var recentEntries = _nodeCreateHistory
                .Select(typeName => matchingEntries.FirstOrDefault(entry =>
                    entry.TypeName.Equals(typeName, StringComparison.OrdinalIgnoreCase)))
                .Where(entry => entry is not null)
                .Cast<NodeTypeEntry>()
                .ToArray();
            if (recentEntries.Length > 0)
            {
                ImGuiApi.TextUnformatted("最近创建");
                foreach (var entry in recentEntries)
                {
                    if (ImGuiApi.Selectable($"{entry.DisplayName ?? entry.TypeName}  [{entry.Owner}]##recent_{entry.TypeName}") &&
                        CreateNode(entry.TypeName) is not null)
                    {
                        ImGuiApi.CloseCurrentPopup();
                        _nodeCreateFilter = string.Empty;
                    }
                }

                ImGuiApi.Separator();
            }

            foreach (var group in matchingEntries.GroupBy(entry => entry.Group, StringComparer.OrdinalIgnoreCase))
            {
                ImGuiApi.TextUnformatted(group.Key);
                foreach (var entry in group)
                {
                    if (ImGuiApi.Selectable($"{entry.DisplayName ?? entry.TypeName}  [{entry.Owner}]##{entry.TypeName}") &&
                        CreateNode(entry.TypeName) is not null)
                    {
                        ImGuiApi.CloseCurrentPopup();
                        _nodeCreateFilter = string.Empty;
                    }
                }
            }

            if (ImGuiApi.Button("取消"))
            {
                ImGuiApi.CloseCurrentPopup();
                _nodeCreateFilter = string.Empty;
            }

            ImGuiApi.EndPopup();
        }

        if (_renamePopup)
        {
            ImGuiApi.OpenPopup("重命名节点");
            _renamePopup = false;
        }

        if (ImGuiApi.BeginPopupModal("重命名节点", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGuiApi.SetNextItemWidth(360f);
            ImGuiApi.InputText("名称", ref _renameBuffer, 256);
            if (ImGuiApi.Button("确定") && RenameSelectedNode(_renameBuffer))
            {
                ImGuiApi.CloseCurrentPopup();
                _renameBuffer = string.Empty;
            }

            ImGuiApi.SameLine();
            if (ImGuiApi.Button("取消"))
            {
                ImGuiApi.CloseCurrentPopup();
                _renameBuffer = string.Empty;
            }

            ImGuiApi.EndPopup();
        }

        var menuH = ImGuiApi.GetFrameHeight();
        var windowW = io.DisplaySize.X;
        var windowH = io.DisplaySize.Y;
        const float leftWidth = 300f;
        const float rightWidth = 340f;

        // 中央视口：显示场景（离屏渲染纹理）
        if (_showViewport)
        {
            ImGuiApi.SetNextWindowPos(new Vector2(leftWidth, menuH), ImGuiCond.Always);
            ImGuiApi.SetNextWindowSize(new Vector2(windowW - leftWidth - rightWidth, windowH - menuH), ImGuiCond.Always);
            ImGuiApi.Begin("视口", ref _showViewport, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);
            var avail = ImGuiApi.GetContentRegionAvail();
            var sceneTexture = _vulkan.GetOffscreenTexture(_vulkan.CurrentFrameSlot);
            if (_session is not null && sceneTexture.IsReady)
            {
                var viewportTools = _session.PluginRegistry.GetEditorExtensions(PluginRegistrationKind.ViewportTool);
                var projectViewportTools = (_session as ProjectSceneSession)?.ProjectEditorRegistry.Extensions
                    .Where(extension => extension.Kind == ProjectEditorExtensionKind.ViewportTool)
                    .ToArray() ?? [];
                if (viewportTools.Count > 0 || projectViewportTools.Length > 0)
                {
                    ImGuiApi.TextUnformatted("视口工具");
                    ImGuiApi.SameLine();
                    foreach (var tool in viewportTools)
                    {
                        if (GetEditorExtension(tool) is IEditorViewportTool viewportTool)
                        {
                            viewportTool.Draw(new ImGuiEditorUiContext());
                        }

                        ImGuiApi.SameLine();
                    }

                    foreach (var tool in projectViewportTools)
                    {
                        if (GetProjectEditorExtension(tool) is IProjectEditorViewportTool viewportTool &&
                            _session is ProjectSceneSession projectSession)
                        {
                            viewportTool.Draw(projectSession.Tree.Root, new ImGuiProjectEditorUiContext());
                        }

                        ImGuiApi.SameLine();
                    }

                    ImGuiApi.NewLine();
                    ImGuiApi.Separator();
                }

                var texW = sceneTexture.Width;
                var texH = sceneTexture.Height;
                var fit = DisplaySettings.ComputeAspectFitRect((int)avail.X, (int)avail.Y, texW, texH);
                var cursor = ImGuiApi.GetCursorPos();
                ImGuiApi.SetCursorPos(cursor + new Vector2(fit.X, fit.Y));
                ImGuiApi.Image(
                    ImGuiRenderer.CreateExternalTextureRef(SceneTextureId),
                    new Vector2(fit.Width, fit.Height));
            }
            else
            {
                ImGuiApi.TextUnformatted("未打开场景");
            }

            ImGuiApi.End();
        }

        // 左侧：场景树
        if (_showSceneTree)
        {
            ImGuiApi.SetNextWindowPos(new Vector2(0, menuH), ImGuiCond.Always);
            ImGuiApi.SetNextWindowSize(new Vector2(leftWidth, windowH - menuH), ImGuiCond.Always);
            ImGuiApi.Begin("场景树", ref _showSceneTree, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);
            if (_session is null)
            {
                ImGuiApi.TextUnformatted("（空项目，无场景）");
            }
            else
            {
                if (ImGuiApi.Button("添加节点"))
                {
                    _nodeCreatePopup = true;
                }

                ImGuiApi.SameLine();
                if (ImGuiApi.Button("删除") && _selected?.Parent is not null)
                {
                    DeleteSelectedNode();
                }

                ImGuiApi.Separator();
                RenderTreeNode(_session.Tree.Root);
            }

            ImGuiApi.End();
        }

        // 右侧：属性
        if (_showInspector)
        {
            ImGuiApi.SetNextWindowPos(new Vector2(windowW - rightWidth, menuH), ImGuiCond.Always);
            ImGuiApi.SetNextWindowSize(new Vector2(rightWidth, windowH - menuH), ImGuiCond.Always);
            ImGuiApi.Begin("属性", ref _showInspector, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);
            if (_selected is null)
            {
                ImGuiApi.TextUnformatted("未选中节点");
            }
            else
            {
                ImGuiApi.TextUnformatted($"{_selected.GetType().Name}  {_selected.GetPath().Value}");
                var inspectorProvider = _session?.PluginRegistry.FindInspectorProvider(_selected.GetType());
                if (inspectorProvider is not null)
                {
                    ImGuiApi.TextUnformatted($"扩展 Inspector: {inspectorProvider.Title}");
                    if (GetEditorExtension(inspectorProvider) is IEditorInspectorProvider provider &&
                        provider.CanInspect(_selected))
                    {
                        provider.Draw(_selected, new ImGuiEditorUiContext());
                    }
                }

                var projectInspector = (_session as ProjectSceneSession)?.ProjectEditorRegistry
                    .FindInspector(_selected.GetType());
                if (projectInspector is not null)
                {
                    ImGuiApi.TextUnformatted($"项目 Inspector: {projectInspector.Title}");
                    if (GetProjectEditorExtension(projectInspector) is IProjectEditorInspectorProvider provider &&
                        provider.CanInspect(_selected))
                    {
                        provider.Draw(_selected, new ImGuiProjectEditorUiContext());
                    }
                }

                foreach (var group in GetInspectorPropertyGroupsWithProjectGroups(
                             _selected.GetType(),
                             projectInspector?.PropertyGroups))
                {
                    if (!ImGuiApi.CollapsingHeader(group.Name, ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        continue;
                    }

                    foreach (var property in group.Properties)
                    {
                        var export = property.GetCustomAttribute<ExportAttribute>();
                        var exported = export is not null;
                        var value = property.GetValue(_selected);
                        var label = GetInspectorPropertyDisplayName(property) +
                            (exported ? " [Export]" : "");
                        var referenceDiagnostic = _session is ProjectSceneSession && projectInspector is not null
                            ? GetProjectNodeReferenceDiagnostic(
                                _selected,
                                property,
                                projectInspector.PropertyGroups)
                            : null;
                        if (referenceDiagnostic is not null)
                        {
                            ImGuiApi.TextUnformatted($"节点路径诊断: {referenceDiagnostic}");
                        }

                        if (property.GetSetMethod(nonPublic: false) is null)
                        {
                            ImGuiApi.TextUnformatted($"{label} [只读]: {value?.ToString() ?? "null"}");
                            continue;
                        }

                        switch (GetInspectorPropertyEditorKind(property))
                        {
                            case "Float":
                            {
                                var floatValue = value is float currentValue ? currentValue : 0f;
                                if (ImGuiApi.InputFloat(label, ref floatValue))
                                {
                                    SetSelectedProperty(property, floatValue);
                                }

                                break;
                            }

                            case "Boolean":
                            {
                                var boolValue = value is bool currentValue && currentValue;
                                if (ImGuiApi.Checkbox(label, ref boolValue))
                                {
                                    SetSelectedProperty(property, boolValue);
                                }

                                break;
                            }

                            case "Vector3":
                            {
                                var vectorValue = value is Vector3 currentValue ? currentValue : Vector3.Zero;
                                if (ImGuiApi.InputFloat3(label, ref vectorValue))
                                {
                                    SetSelectedProperty(property, vectorValue);
                                }

                                break;
                            }

                            case "Int":
                            {
                                var intValue = value is uint u ? (int)u : (int)(value ?? 0);
                                if (ImGuiApi.InputInt(label, ref intValue))
                                {
                                    SetSelectedProperty(property, property.PropertyType == typeof(uint) ? (uint)Math.Max(0, intValue) : intValue);
                                }

                                break;
                            }

                            case "Enum":
                            {
                                var names = Enum.GetNames(property.PropertyType);
                                var current = Array.IndexOf(names, value?.ToString());
                                if (current < 0)
                                {
                                    current = 0;
                                }

                                if (ImGuiApi.Combo(label, ref current, names, names.Length))
                                {
                                    SetSelectedProperty(property, Enum.Parse(property.PropertyType, names[current]));
                                }

                                break;
                            }

                            case "String":
                            {
                                var s = value as string ?? "";
                                if (ImGuiApi.InputText(label, ref s, 256))
                                {
                                    SetSelectedProperty(property, s);
                                }

                                break;
                            }

                            default:
                            {
                                ImGuiApi.TextUnformatted(label + ": " + (value?.ToString() ?? "null"));
                                break;
                            }
                        }
                    }
                }
            }

            ImGuiApi.End();
        }

        // 引擎结构信息（可关）
        if (_showEngineInfo)
        {
            ImGuiApi.SetNextWindowPos(new Vector2(leftWidth, menuH + 40), ImGuiCond.FirstUseEver);
            ImGuiApi.SetNextWindowSize(new Vector2(360, 240), ImGuiCond.FirstUseEver);
            if (ImGuiApi.Begin("引擎结构", ref _showEngineInfo))
            {
                var tree = _session?.Tree;
                ImGuiApi.TextUnformatted($"SceneTree  {(tree is null ? "-" : tree.Root.Children.Count.ToString())} 节点  {tree?.SimulationTicksPerSecond}Hz  {(tree is not null && tree.Paused ? "暂停" : "运行")}");
                ImGuiApi.TextUnformatted($"RenderingServer  网格 {_session?.RenderingServer.MeshCount ?? 0}");
                ImGuiApi.TextUnformatted($"会话  {_session?.DisplayName ?? "-"}");
                ImGuiApi.Separator();
                var knownTypes = _session is ProjectSceneSession projectSession
                    ? projectSession.SceneRegistry.KnownNodeTypes
                    : Array.Empty<string>();
                ImGuiApi.TextUnformatted($"会话 registry 已注册节点 {knownTypes.Count}");
                foreach (var typeName in knownTypes)
                {
                    ImGuiApi.TextUnformatted("  " + typeName);
                }
            }

            ImGuiApi.End();
        }

        if (_showPluginExtensions)
        {
            ImGuiApi.SetNextWindowSize(new Vector2(420, 260), ImGuiCond.FirstUseEver);
            if (ImGuiApi.Begin("编辑器扩展", ref _showPluginExtensions))
            {
                var extensions = _session?.PluginRegistry.EditorExtensions ?? Array.Empty<PluginEditorExtensionRegistration>();
                if (extensions.Count == 0)
                {
                    ImGuiApi.TextUnformatted("当前项目没有注册编辑器扩展");
                }
                else
                {
                    ImGuiApi.TextUnformatted($"当前会话扩展 {extensions.Count}");
                    ImGuiApi.Separator();
                    foreach (var extension in extensions)
                    {
                        var target = string.IsNullOrWhiteSpace(extension.TargetTypeName)
                            ? string.Empty
                            : $" 目标: {extension.TargetTypeName}";
                        ImGuiApi.TextUnformatted(
                            $"{extension.Kind}  {extension.Title}  ({extension.PluginId}:{extension.Id}){target}");
                        if (extension.Kind == PluginRegistrationKind.EditorDock &&
                            GetEditorExtension(extension) is IEditorDock dock)
                        {
                            dock.Draw(new ImGuiEditorUiContext());
                        }
                    }
                }

                var projectSession = _session as ProjectSceneSession;
                var projectExtensions = projectSession?.ProjectEditorRegistry.Extensions ?? [];
                ImGuiApi.Separator();
                ImGuiApi.TextUnformatted("项目扩展");
                if (projectExtensions.Count == 0)
                {
                    ImGuiApi.TextUnformatted("当前项目没有注册项目编辑器扩展");
                }
                else if (projectSession is not null)
                {
                    foreach (var extension in projectExtensions)
                    {
                        ImGuiApi.TextUnformatted($"{extension.Kind}  {extension.Title}  ({extension.Id})");
                        if (extension.Kind == ProjectEditorExtensionKind.Dock &&
                            GetProjectEditorExtension(extension) is IProjectEditorDock dock)
                        {
                            dock.Draw(projectSession.Tree.Root, new ImGuiProjectEditorUiContext());
                        }
                    }
                }
            }

            ImGuiApi.End();
        }
    }

    public sealed record InspectorPropertyGroup(string Name, IReadOnlyList<PropertyInfo> Properties);

    public sealed record NodeTypeEntry(
        string TypeName,
        string Group,
        string Owner,
        Type Type,
        string? DisplayName = null);

    public static string GetInspectorPropertyDisplayName(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        var export = property.GetCustomAttribute<ExportAttribute>();
        return export?.Name is { Length: > 0 } displayName ? displayName : property.Name;
    }

    /// <summary>返回 Inspector 对属性使用的编辑器类型；未知类型保持只读展示。</summary>
    public static string GetInspectorPropertyEditorKind(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.GetSetMethod(nonPublic: false) is null)
        {
            return "ReadOnly";
        }

        var type = property.PropertyType;
        if (type == typeof(float))
        {
            return "Float";
        }

        if (type == typeof(bool))
        {
            return "Boolean";
        }

        if (type == typeof(Vector3))
        {
            return "Vector3";
        }

        if (type == typeof(uint) || type == typeof(int))
        {
            return "Int";
        }

        if (type.IsEnum)
        {
            return "Enum";
        }

        if (type == typeof(string))
        {
            return "String";
        }

        return "ReadOnly";
    }

    /// <summary>
    /// 校验项目 Inspector 明确标记为“节点引用”的字符串属性。
    /// 资源路径和可选空路径不在此处解释，避免把项目资产路径误判为节点路径。
    /// </summary>
    public static string? GetProjectNodeReferenceDiagnostic(
        Node node,
        PropertyInfo property,
        IReadOnlyList<ProjectEditorPropertyGroup> projectGroups)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(projectGroups);
        if (property.PropertyType != typeof(string) ||
            !projectGroups.Any(group =>
                group.Name.Equals("节点引用", StringComparison.Ordinal) &&
                group.PropertyNames.Contains(property.Name, StringComparer.Ordinal)))
        {
            return null;
        }

        var value = property.GetValue(node) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return node.GetNode(new NodePath(value)) is null
                ? $"{property.Name} 指向不存在的节点: {value}"
                : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return $"{property.Name} 路径无效: {exception.Message}";
        }
    }

    /// <summary>从当前会话 registry 生成节点创建器条目，并标注插件/程序集归属。</summary>
    public static IReadOnlyList<NodeTypeEntry> GetNodeTypeEntries(
        SceneRegistry registry,
        PluginRegistry? pluginRegistry = null) =>
        GetNodeTypeEntriesForProject(registry, pluginRegistry, null);

    /// <summary>生成节点创建器条目并叠加项目节点显示元数据。</summary>
    public static IReadOnlyList<NodeTypeEntry> GetNodeTypeEntriesForProject(
        SceneRegistry registry,
        PluginRegistry? pluginRegistry,
        ProjectEditorRegistry? projectEditorRegistry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var registrations = pluginRegistry?.Registrations
            .Where(registration => registration.Kind == PluginRegistrationKind.Node)
            .ToArray() ?? [];
        return registry.KnownNodeTypes
            .Select(typeName =>
            {
                var type = registry.ResolveNodeType(typeName)!;
                var registration = registrations.FirstOrDefault(candidate =>
                    candidate.Type == type && candidate.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                var owner = registration?.PluginId ?? type.Assembly.GetName().Name ?? "Cubit.Core";
                var projectPresentation = projectEditorRegistry?.FindNodePresentation(type);
                var group = projectPresentation?.Group ?? (registration is not null
                    ? $"插件 / {registration.PluginId}"
                    : type.Namespace?.Split('.')[0] ?? "Cubit.Core");
                return new NodeTypeEntry(
                    typeName,
                    group,
                    owner,
                    type,
                    projectPresentation?.Title);
            })
            .OrderBy(entry => entry.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.TypeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetNodeCreateHistory() => _nodeCreateHistory.ToArray();

    public static IReadOnlyList<InspectorPropertyGroup> GetInspectorPropertyGroups(Type type) =>
        GetInspectorPropertyGroupsWithProjectGroups(type, null);

    /// <summary>项目可为自身节点指定语义分组；未归类属性仍按导出/通用保留。</summary>
    private static IReadOnlyList<InspectorPropertyGroup> GetInspectorPropertyGroupsWithProjectGroups(
        Type type,
        IReadOnlyList<ProjectEditorPropertyGroup>? projectGroups)
    {
        ArgumentNullException.ThrowIfNull(type);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead &&
                (property.GetCustomAttribute<ExportAttribute>() is not null || IsEditableType(property.PropertyType)))
            .ToArray();
        var groups = new List<InspectorPropertyGroup>();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projectGroup in projectGroups ?? [])
        {
            var groupedProperties = projectGroup.PropertyNames
                .Select(name => properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.Ordinal)))
                .Where(property => property is not null)
                .Cast<PropertyInfo>()
                .Where(property => assigned.Add(property.Name))
                .ToArray();
            if (groupedProperties.Length > 0)
            {
                groups.Add(new InspectorPropertyGroup(projectGroup.Name, groupedProperties));
            }
        }

        var remaining = properties.Where(property => !assigned.Contains(property.Name)).ToArray();
        var exported = remaining.Where(property => property.GetCustomAttribute<ExportAttribute>() is not null).ToArray();
        var common = remaining.Where(property => property.GetCustomAttribute<ExportAttribute>() is null).ToArray();
        if (exported.Length > 0)
        {
            groups.Add(new InspectorPropertyGroup("导出属性", exported));
        }

        if (common.Length > 0)
        {
            groups.Add(new InspectorPropertyGroup("通用属性", common));
        }

        return groups;
    }

    public Node? CreateNode(string typeName)
    {
        if (_session is null || string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var parent = _selected is not null && _selected.Tree == _session.Tree
            ? _selected
            : _session.Tree.Root;
        var command = EditorSceneOperations.CreateNodeCommand(
            _session.Tree,
            _session.SceneRegistry,
            parent,
            typeName,
            created =>
            {
                if (_session is ProjectSceneSession projectSession)
                {
                    projectSession.InjectEditorServices(created);
                }
            });
        try
        {
            _commandHistory.Execute(command);
        }
        catch (Exception exception)
        {
            _statusText = $"添加节点失败: {exception.Message}";
            return null;
        }

        var node = command.CreatedNode;
        if (node is null)
        {
            _statusText = $"未知节点类型: {typeName}";
            return null;
        }

        _selected = node;
        _nodeCreateHistory.RemoveAll(existing => existing.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase));
        _nodeCreateHistory.Insert(0, typeName.Trim());
        if (_nodeCreateHistory.Count > 12)
        {
            _nodeCreateHistory.RemoveRange(12, _nodeCreateHistory.Count - 12);
        }
        _statusText = $"已添加节点: {node.Name}";
        return node;
    }

    public bool RenameSelectedNode(string newName)
    {
        if (_selected is null || _selected.Parent is null || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        try
        {
            _commandHistory.Execute(EditorSceneOperations.RenameNodeCommand(_selected, newName));
        }
        catch (Exception)
        {
            _statusText = $"重命名失败：名称为空或同级已有同名节点";
            return false;
        }

        _statusText = $"已重命名节点: {_selected.Name}";
        return true;
    }

    public bool DeleteSelectedNode()
    {
        if (_selected is null || _selected.Parent is null)
        {
            return false;
        }

        var deletedName = _selected.Name;
        try
        {
            _commandHistory.Execute(EditorSceneOperations.DeleteNodeCommand(_selected));
        }
        catch (Exception)
        {
            return false;
        }

        _selected = null;
        _statusText = $"已删除节点: {deletedName}";
        return true;
    }

    /// <summary>撤销最近一次场景编辑命令。</summary>
    public bool Undo()
    {
        try
        {
            var changed = _commandHistory.Undo();
            if (changed && (_selected is null || _selected.Tree != _session?.Tree))
            {
                _selected = null;
            }

            return changed;
        }
        catch (Exception exception)
        {
            _statusText = $"撤销失败: {exception.Message}";
            return false;
        }
    }

    /// <summary>重做最近撤销的场景编辑命令。</summary>
    public bool Redo()
    {
        try
        {
            return _commandHistory.Redo();
        }
        catch (Exception exception)
        {
            _statusText = $"重做失败: {exception.Message}";
            return false;
        }
    }

    /// <summary>将当前选中节点的 Inspector 属性修改记录为可撤销命令。</summary>
    public bool SetSelectedProperty(string propertyName, object? value)
    {
        if (_selected is null || string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var property = _selected.GetType().GetProperty(propertyName.Trim(), BindingFlags.Public | BindingFlags.Instance);
        return property is not null && SetSelectedProperty(property, value);
    }

    private bool SetSelectedProperty(PropertyInfo property, object? value)
    {
        if (_selected is null || property.GetSetMethod(nonPublic: false) is null || Equals(property.GetValue(_selected), value))
        {
            return false;
        }

        try
        {
            _commandHistory.Execute(EditorSceneOperations.SetPropertyCommand(_selected, property, value));
            return true;
        }
        catch (Exception exception)
        {
            _statusText = $"属性修改失败: {exception.Message}";
            return false;
        }
    }


    private static bool IsEditableType(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
        type == typeof(Vector3) || type == typeof(Guid) || type == typeof(DateTime);

    private object? GetEditorExtension(PluginEditorExtensionRegistration registration)
    {
        if (_session is null)
        {
            return null;
        }

        var key = $"{registration.Kind}\0{registration.Id}";
        if (_editorExtensionInstances.TryGetValue(key, out var existing))
        {
            return existing;
        }

        try
        {
            var created = _session.PluginRegistry.CreateEditorExtension(registration.Kind, registration.Id);
            _editorExtensionInstances[key] = created;
            return created;
        }
        catch (Exception exception)
        {
            _statusText = $"编辑器扩展加载失败: {registration.Id} ({exception.Message})";
            return null;
        }
    }

    private object? GetProjectEditorExtension(ProjectEditorExtensionRegistration registration)
    {
        if (_session is not ProjectSceneSession projectSession)
        {
            return null;
        }

        var key = $"{registration.Kind}\0{registration.Id}";
        if (_projectEditorExtensionInstances.TryGetValue(key, out var existing))
        {
            return existing;
        }

        try
        {
            var created = projectSession.ProjectEditorRegistry.CreateExtension(registration.Kind, registration.Id);
            _projectEditorExtensionInstances[key] = created;
            return created;
        }
        catch (Exception exception)
        {
            _statusText = $"项目编辑器扩展加载失败: {registration.Id} ({exception.Message})";
            return null;
        }
    }

    private sealed class ImGuiEditorUiContext : IEditorUiContext
    {
        public void Text(string text) => ImGuiApi.TextUnformatted(text ?? string.Empty);

        public bool Button(string label) => ImGuiApi.Button(label ?? string.Empty);

        public void Separator() => ImGuiApi.Separator();
    }

    private sealed class ImGuiProjectEditorUiContext : IProjectEditorUiContext
    {
        public void Text(string text) => ImGuiApi.TextUnformatted(text ?? string.Empty);

        public bool Button(string label) => ImGuiApi.Button(label ?? string.Empty);

        public bool InputText(string label, ref string value, int maxLength) =>
            ImGuiApi.InputText(label ?? string.Empty, ref value, (nuint)Math.Max(1, maxLength));

        public void Separator() => ImGuiApi.Separator();
    }

    private void RenderTreeNode(Node node)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (node == _selected)
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        if (node.Children.Count == 0)
        {
            flags |= ImGuiTreeNodeFlags.Leaf;
        }

        var open = ImGuiApi.TreeNodeEx($"{node.Name}##{node.GetPath().Value}", flags);
        if (ImGuiApi.IsItemClicked() && !ImGuiApi.IsItemToggledOpen())
        {
            _selected = node;
        }

        if (open)
        {
            foreach (var child in node.Children)
            {
                RenderTreeNode(child);
            }

            ImGuiApi.TreePop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vulkan.Overlay -= _drawOverlay;
        McpHost.Dispose();
        _session?.Dispose();
        _session = null;
        _imgui.Dispose();
    }
}
