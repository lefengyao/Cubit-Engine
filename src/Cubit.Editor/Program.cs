using Cubit.Core.Engine;
using Cubit.Editor.Platform;
using Cubit.Mcp;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Cubit.Editor;

/// <summary>
/// 编辑器启动器：启动时只打开"引擎 + 空项目"（像 Godot 打开编辑器本身）。
/// demo 也是普通项目，用户通过 文件 → 打开项目 显式加载后才会出现在编辑窗口。
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        // 日志写入文件（AutoFlush），便于诊断编辑器启动/运行问题
        var logPath = Path.Combine(AppContext.BaseDirectory, "cubit-editor.log");
        var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);
        Console.WriteLine($"[CubitEditor] 启动，日志: {logPath}");
        var launchOptions = EditorLaunchOptions.Parse(args);
        if (launchOptions.Error is not null)
        {
            Console.Error.WriteLine($"[CubitEditor] {launchOptions.Error}");
        }

        var options = WindowOptions.DefaultVulkan with
        {
            Title = "Cubit 编辑器",
            Size = new Vector2D<int>(EditorWindowPolicy.InitialWidth, EditorWindowPolicy.InitialHeight),
        };

        var view = Window.Create(options);
        var window = new GlfwGameWindow(view);
        var engine = new Engine(window);
        EditorNode? editor = null;
        IInputContext? input = null;
        McpHttpServer? mcpHttpServer = null;

        engine.Load += () =>
        {
            // 编辑器模式：场景渲染到离屏纹理（中央视口），UI 渲染到交换链
            engine.Vulkan!.RenderSceneOffscreen = true;

            input = view.CreateInput();

            foreach (var keyboard in input.Keyboards)
            {
                keyboard.KeyDown += (_, key, _) => editor?.OnKey(key, true);
                keyboard.KeyUp += (_, key, _) => editor?.OnKey(key, false);
            }

            foreach (var mouse in input.Mice)
            {
                mouse.MouseMove += (_, position) => editor?.OnMouseMove(position.X, position.Y);
                mouse.MouseDown += (_, button) => editor?.OnMouseButton(button, true);
                mouse.MouseUp += (_, button) => editor?.OnMouseButton(button, false);
                mouse.Scroll += (_, wheel) => editor?.OnMouseWheel(wheel.Y);
            }

            editor = new EditorNode(engine.Vulkan!);
            if (launchOptions.ProjectPath is not null)
            {
                editor.OpenProject(launchOptions.ProjectPath);
            }

            if (launchOptions.McpStdio)
            {
                var mcpHost = editor.McpHost;
                var mcpThread = new Thread(() =>
                {
                    using var server = new McpServer(mcpHost, Console.OpenStandardInput(), Console.OpenStandardOutput());
                    server.Run();
                })
                {
                    IsBackground = true,
                    Name = "Cubit-Editor-Mcp",
                };
                mcpThread.Start();
                Console.WriteLine("[CubitEditor] MCP stdio 服务已启动");
            }
            else if (launchOptions.McpHttpPort is { } port)
            {
                mcpHttpServer = new McpHttpServer(editor.McpHost, port);
                Console.WriteLine($"[CubitEditor] MCP HTTP 服务已启动: http://127.0.0.1:{mcpHttpServer.Port}/mcp");
            }
            Console.WriteLine("[CubitEditor] 编辑器已启动（默认空项目，可通过 文件 → 打开项目 显式加载）");
        };

        engine.Update += delta =>
        {
            editor?.Update(delta);
        };

        view.Closing += () =>
        {
            mcpHttpServer?.Dispose();
            input?.Dispose();
            editor?.Dispose();
            engine.Dispose();
            logWriter.Dispose();
        };
        engine.Run();
        mcpHttpServer?.Dispose();
    }
}
