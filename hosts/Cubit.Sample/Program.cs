using System.Numerics;
using System.Globalization;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Cubit.Core.Engine;
using Cubit.Core.Input;
using Cubit.Core.Project;
using Cubit.Audio;
using Cubit.Audio.Windows;
using Cubit.ImGui.Rendering;
using Cubit.Sample;
using Cubit.Sample.Platform;
using Cubit.Mcp;

namespace Cubit.Sample;

internal static class Program
{
    private static void Main(string[] args)
    {
        // 日志写入文件（AutoFlush），便于无窗口会话下诊断
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "directsound");
        var logPath = Path.Combine(AppContext.BaseDirectory, "cubit.log");
        var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);
        Console.WriteLine($"[CubitSample] 启动，日志: {logPath}");

        AudioSmokeRequest? audioSmokeRequest;
        try
        {
            audioSmokeRequest = ParseAudioSmokeRequest(args);
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException)
        {
            Console.Error.WriteLine($"[CubitSample] 桌面 OGG 冒烟测试参数无效: {exception.Message}");
            return;
        }

        var options = WindowOptions.DefaultVulkan with
        {
            Title = "Cubit 示例 — 仿我的世界",
            Size = new Vector2D<int>(1280, 720),
        };

        var view = Window.Create(options);
        var window = new GlfwGameWindow(view);
        var engine = new Engine(window);
        var closeRequest = new FrameCloseRequest();
        var shutdownRequested = 0;
        var shutdownCompleted = 0;

        var project = ProjectIO.Load(FindProjectRoot());
        var inputServer = new InputServer();
        var lastMouse = Vector2.Zero;
        var mouseReady = false;
        var menuPointerActive = false;
        Action<CursorMode>? setCursorMode = null;
        GameScene? scene = null;
        ImGuiProjectPresentationHost? projectPresentation = null;
        GameSceneMcpHost? mcpHost = null;
        IAudioOutputBackend? audioBackend = null;
        McpHttpServer? mcpHttpServer = null;
        AudioServer? audioSmokeServer = null;
        var audioSmokePlayback = default(AudioPlaybackHandle);
        var audioSmokeElapsedSeconds = 0d;
        var audioSmokeFinished = false;

        void DispatchPresentationInput(Action<ImGuiProjectPresentationHost> dispatch)
        {
            ArgumentNullException.ThrowIfNull(dispatch);
            if (Volatile.Read(ref shutdownRequested) != 0 || projectPresentation is not { } presentation)
            {
                return;
            }

            dispatch(presentation);
        }

        static InputAction MapKey(Key key) => key switch
        {
            Key.W => InputAction.Forward,
            Key.S => InputAction.Back,
            Key.A => InputAction.Left,
            Key.D => InputAction.Right,
            Key.Space => InputAction.Up,
            Key.ShiftLeft or Key.ShiftRight => InputAction.Down,
            Key.Escape => InputAction.Cancel,
            _ => (InputAction)(-1),
        };

        static ProjectInputControl? MapProjectControl(Key key) => key switch
        {
            Key.Escape => ProjectInputControl.Cancel,
            Key.Number1 => ProjectInputControl.Digit1,
            Key.Number2 => ProjectInputControl.Digit2,
            Key.Number3 => ProjectInputControl.Digit3,
            Key.Number4 => ProjectInputControl.Digit4,
            Key.Number5 => ProjectInputControl.Digit5,
            Key.Number6 => ProjectInputControl.Digit6,
            Key.Number7 => ProjectInputControl.Digit7,
            Key.Number8 => ProjectInputControl.Digit8,
            Key.Number9 => ProjectInputControl.Digit9,
            Key.F3 => ProjectInputControl.ToggleDebugOverlay,
            Key.F5 => ProjectInputControl.ToggleView,
            Key.T => ProjectInputControl.OpenChat,
            Key.E => ProjectInputControl.ToggleInventory,
            _ => null,
        };

        engine.Load += () =>
        {
            // 视图已初始化，此时才能创建输入上下文
            var input = view.CreateInput();
            setCursorMode = mode =>
            {
                foreach (var mouse in input.Mice)
                {
                    mouse.Cursor.CursorMode = mode;
                }
            };

            foreach (var keyboard in input.Keyboards)
            {
                // 文本输入必须走字符事件，不能根据物理键猜测字符；否则键盘布局下的
                // 斜杠、大小写和数字键盘字符会在聊天/项目文本框中丢失。
                keyboard.BeginInput();
                keyboard.KeyDown += (_, key, _) =>
                {
                    DispatchPresentationInput(presentation => presentation.OnKey((int)key, down: true));
                    var action = MapKey(key);
                    if (action != (InputAction)(-1) && projectPresentation is not { UsesPointer: true })
                    {
                        inputServer.SetAction(action, true);
                    }
                    if (MapProjectControl(key) is { } control) scene?.RouteProjectInput(new ProjectInputEvent(control, true));
                };
                keyboard.KeyChar += (_, character) =>
                {
                    if (!char.IsControl(character))
                    {
                        DispatchPresentationInput(presentation => presentation.OnTextInput(character));
                    }
                };
                keyboard.KeyUp += (_, key, _) =>
                {
                    DispatchPresentationInput(presentation => presentation.OnKey((int)key, down: false));
                    var action = MapKey(key);
                    if (action != (InputAction)(-1)) inputServer.SetAction(action, false);
                    if (MapProjectControl(key) is { } control) scene?.RouteProjectInput(new ProjectInputEvent(control, false));
                };
            }

            foreach (var mouse in input.Mice)
            {
                mouse.MouseMove += (_, position) =>
                {
                    if (Volatile.Read(ref shutdownRequested) != 0)
                    {
                        return;
                    }

                    if (!mouseReady)
                    {
                        lastMouse = position;
                        mouseReady = true;
                    }

                    if (projectPresentation is { UsesPointer: true })
                    {
                        var inputSize = view.Size;
                        var framebufferSize = engine.Vulkan?.SwapchainSize ?? (inputSize.X, inputSize.Y);
                        var scaleX = inputSize.X > 0 ? framebufferSize.Width / (float)inputSize.X : 1f;
                        var scaleY = inputSize.Y > 0 ? framebufferSize.Height / (float)inputSize.Y : 1f;
                        DispatchPresentationInput(presentation => presentation.OnMouseMove(position.X * scaleX, position.Y * scaleY));
                    }
                    else
                    {
                        inputServer.AddMouseDelta(position.X - lastMouse.X, position.Y - lastMouse.Y);
                    }

                    lastMouse = position;
                };
                mouse.MouseDown += (_, button) =>
                {
                    if (Volatile.Read(ref shutdownRequested) != 0)
                    {
                        return;
                    }

                    if (projectPresentation is { UsesPointer: true })
                    {
                        var pointerPosition = mouse.Position;
                        var clickInputSize = view.Size;
                        var clickFramebufferSize = engine.Vulkan?.SwapchainSize ?? (clickInputSize.X, clickInputSize.Y);
                        var clickScaleX = clickInputSize.X > 0
                            ? clickFramebufferSize.Width / (float)clickInputSize.X
                            : 1f;
                        var clickScaleY = clickInputSize.Y > 0
                            ? clickFramebufferSize.Height / (float)clickInputSize.Y
                            : 1f;
                        DispatchPresentationInput(presentation => presentation.OnMouseMove(
                            pointerPosition.X * clickScaleX,
                            pointerPosition.Y * clickScaleY));
                        lastMouse = pointerPosition;
                        mouseReady = true;
                    }
                    DispatchPresentationInput(presentation => presentation.OnMouseButton((int)button, down: true));
                    if (button == MouseButton.Left) scene?.RouteProjectInput(new ProjectInputEvent(ProjectInputControl.PrimaryPointer, true));
                    if (button == MouseButton.Right) scene?.RouteProjectInput(new ProjectInputEvent(ProjectInputControl.SecondaryPointer, true));
                };
                mouse.MouseUp += (_, button) =>
                {
                    if (Volatile.Read(ref shutdownRequested) != 0)
                    {
                        return;
                    }

                    DispatchPresentationInput(presentation => presentation.OnMouseButton((int)button, down: false));
                    if (button == MouseButton.Left) scene?.RouteProjectInput(new ProjectInputEvent(ProjectInputControl.PrimaryPointer, false));
                    if (button == MouseButton.Right) scene?.RouteProjectInput(new ProjectInputEvent(ProjectInputControl.SecondaryPointer, false));
                };
                mouse.Scroll += (_, wheel) =>
                {
                    if (Volatile.Read(ref shutdownRequested) == 0)
                    {
                        DispatchPresentationInput(presentation => presentation.OnMouseWheel(wheel.Y));
                    }
                };
            }

            if (project.Manifest.Plugins.Any(plugin =>
                plugin.Id.Equals("cubit.audio", StringComparison.OrdinalIgnoreCase)))
            {
                // Windows WASAPI 回调与 GLFW 窗口销毁存在驱动级冲突；桌面固定 DirectSound 后端。
                var diagnostics = new SdlAudioDiagnostics();
                try
                {
                    audioBackend = new SdlAudioOutputBackend(diagnostics);
                }
                catch (AudioBackendException exception)
                {
                    Console.Error.WriteLine($"[CubitSample] 音频设备不可用: {exception.DiagnosticCode}");
                }
            }

            scene = new GameScene(
                engine.Vulkan!,
                project,
                inputServer,
                audioBackend,
                projectPresentationVulkan: engine.Vulkan);
            projectPresentation = ImGuiProjectPresentationHost.Create(engine.Vulkan!, scene.Runtime, closeRequest.Request);
            menuPointerActive = projectPresentation.UsesPointer;
            setCursorMode(menuPointerActive ? CursorMode.Normal : CursorMode.Raw);
            engine.ConfigureDisplay(1280, 720, keepAspect: true);
            Console.WriteLine("[CubitSample] 平台输入已连接；具体控制由当前项目 runtime 定义");

            if (audioSmokeRequest is not null)
            {
                if (audioBackend is not SdlAudioOutputBackend sdlBackend)
                {
                    Console.Error.WriteLine("[CubitSample] 桌面 OGG 冒烟测试失败：SDL 音频输出设备未成功打开");
                    closeRequest.Request();
                    return;
                }

                try
                {
                    var server = scene.Runtime.Context.GetRequiredService<AudioServer>();
                    var clip = AudioClip.FromOggVorbis(File.ReadAllBytes(audioSmokeRequest.FilePath));
                    audioSmokePlayback = server.Play(clip, volume: 0.32f);
                    audioSmokeServer = server;
                    Console.WriteLine(
                        $"[CubitSample] 桌面 OGG 冒烟测试开始: path={audioSmokeRequest.FilePath} " +
                        $"frames={clip.FrameCount} output={sdlBackend.DeviceFormat.SampleRate}Hz/" +
                        $"{sdlBackend.DeviceFormat.Channels}ch device={sdlBackend.DeviceId} " +
                        $"duration={audioSmokeRequest.DurationSeconds:F1}s");
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"[CubitSample] 桌面 OGG 冒烟测试失败: {exception.Message}");
                    closeRequest.Request();
                    return;
                }
            }

            // 内置 MCP：--mcp 启动 stdio JSON-RPC 服务（AI/编辑器可直接驱动引擎）
            if (args.Length > 0 && args[0] == "--mcp")
            {
                mcpHost = new GameSceneMcpHost(scene, engine.Vulkan, inputServer);
                var combinedMcpHost = new McpEngineHostWithExtensions(
                    mcpHost,
                    [ComposeProjectMcpExtensions(scene)]);
                var mcpThread = new Thread(() =>
                {
                    using var server = new McpServer(combinedMcpHost, Console.OpenStandardInput(), Console.OpenStandardOutput());
                    server.Run();
                })
                {
                    IsBackground = true,
                    Name = "Cubit-Mcp",
                };
                mcpThread.Start();
                Console.WriteLine("[CubitSample] MCP stdio 服务已启动（--mcp）");
            }
            else if (args.Length > 0 && args[0] == "--mcp-http")
            {
                // 内置 MCP HTTP：编辑器/外部工具经 HTTP 驱动引擎（--mcp-http [端口]，默认 8765）
                var port = args.Length > 1 && int.TryParse(args[1], out var parsedPort) ? parsedPort : 8765;
                mcpHost = new GameSceneMcpHost(scene, engine.Vulkan, inputServer);
                var combinedMcpHost = new McpEngineHostWithExtensions(
                    mcpHost,
                    [ComposeProjectMcpExtensions(scene)]);
                mcpHttpServer = new McpHttpServer(combinedMcpHost, port);
                Console.WriteLine($"[CubitSample] MCP HTTP 服务已启动: http://127.0.0.1:{mcpHttpServer.Port}/mcp");
            }
        };

        engine.Update += delta =>
        {
            if (Volatile.Read(ref shutdownRequested) != 0)
            {
                return;
            }

            // MCP 传输线程只排队；场景、输入、GPU 与音频服务由游戏主线程处理。
            mcpHost?.PumpMcpRequests();
            scene?.ProcessFrame(delta);
            if (Volatile.Read(ref shutdownRequested) != 0)
            {
                return;
            }

            projectPresentation?.Update(delta);
            if (closeRequest.TryConsume())
            {
                Interlocked.Exchange(ref shutdownRequested, 1);
                engine.RequestClose();
                return;
            }

            var nextMenuPointerActive = projectPresentation?.UsesPointer == true;
            if (nextMenuPointerActive != menuPointerActive)
            {
                menuPointerActive = nextMenuPointerActive;
                if (menuPointerActive)
                {
                    inputServer.ClearActions();
                }
                setCursorMode?.Invoke(menuPointerActive ? CursorMode.Normal : CursorMode.Raw);
                if (menuPointerActive && projectPresentation is not null)
                {
                    // boot 首帧切到标题后可能没有新的 MouseMove 事件；立即同步已有坐标，避免首击只完成焦点/指针状态。
                    var pointerInputSize = view.Size;
                    var pointerFramebufferSize = engine.Vulkan?.SwapchainSize ?? (pointerInputSize.X, pointerInputSize.Y);
                    var pointerScaleX = pointerInputSize.X > 0
                        ? pointerFramebufferSize.Width / (float)pointerInputSize.X
                        : 1f;
                    var pointerScaleY = pointerInputSize.Y > 0
                        ? pointerFramebufferSize.Height / (float)pointerInputSize.Y
                        : 1f;
                    projectPresentation.OnMouseMove(
                        lastMouse.X * pointerScaleX,
                        lastMouse.Y * pointerScaleY);
                    mouseReady = true;
                }
                else
                {
                    mouseReady = false;
                }
            }

            if (audioSmokeRequest is not null && audioSmokeServer is not null && !audioSmokeFinished)
            {
                audioSmokeElapsedSeconds += delta;
                var stillPlaying = audioSmokeServer.IsPlaying(audioSmokePlayback);
                if (!stillPlaying || audioSmokeElapsedSeconds >= audioSmokeRequest.DurationSeconds)
                {
                    if (stillPlaying)
                    {
                        audioSmokeServer.Stop(audioSmokePlayback);
                    }

                    audioSmokeFinished = true;
                    Console.WriteLine(
                        $"[CubitSample] 桌面 OGG 冒烟测试结束: " +
                        $"reason={(stillPlaying ? "duration" : "clip-finished")} " +
                        $"elapsed={audioSmokeElapsedSeconds:F2}s");
                    closeRequest.Request();
                }
            }

        };

        view.Closing += () =>
        {
            // GLFW 的 Closing 仍可能处于当前回调/提交路径；此处只能停止新帧。
            // 所有 ImGui、场景和 Vulkan 相关释放必须等窗口主循环退出后进行。
            Interlocked.Exchange(ref shutdownRequested, 1);
        };
        try
        {
            engine.Run();
        }
        finally
        {
            if (Interlocked.Exchange(ref shutdownCompleted, 1) == 0)
            {
                projectPresentation?.Dispose();
                mcpHost?.Dispose();
                scene?.Dispose();
                engine.Dispose();
            }

            mcpHttpServer?.Dispose();
        }
    }

    private static McpProjectExtensionComposition ComposeProjectMcpExtensions(GameScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var modules = scene.Bootstrap is IProjectMcpExtensionSource source
            ? source.CreateMcpExtensionModules()
            : [];
        return McpProjectExtensionComposer.Compose(
            new McpProjectExtensionContext
            {
                Project = scene.Runtime.Project,
                Tree = scene.Tree,
                EngineContext = scene.Runtime.Context,
                ResolveRuntimeService = type => scene.Runtime.Context.TryGetService(type, out var service)
                    ? service
                    : null,
            },
            modules);
    }

    private static string FindProjectRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CUBIT_PROJECT_PATH");
        if (!string.IsNullOrWhiteSpace(configured) &&
            File.Exists(Path.Combine(configured, ProjectIO.ManifestFileName)))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, ProjectIO.ManifestFileName)))
                {
                    return current.FullName;
                }
            }
        }

        throw new FileNotFoundException(
            $"找不到 {ProjectIO.ManifestFileName}；请在项目目录启动或设置 CUBIT_PROJECT_PATH");
    }

    private static AudioSmokeRequest? ParseAudioSmokeRequest(string[] args)
    {
        const string option = "--audio-smoke-ogg";
        const string durationOption = "--audio-smoke-seconds";
        const double defaultDurationSeconds = 8d;
        const double maximumDurationSeconds = 30d;

        var optionIndex = Array.IndexOf(args, option);
        if (optionIndex < 0)
        {
            return null;
        }

        if (Array.LastIndexOf(args, option) != optionIndex)
        {
            throw new ArgumentException($"{option} 只能提供一次");
        }

        if (optionIndex >= args.Length - 1 || string.IsNullOrWhiteSpace(args[optionIndex + 1]))
        {
            throw new ArgumentException($"{option} 需要一个 OGG 文件路径");
        }

        var filePath = Path.GetFullPath(args[optionIndex + 1]);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到 OGG 文件", filePath);
        }

        var durationIndex = Array.IndexOf(args, durationOption);
        var durationSeconds = defaultDurationSeconds;
        if (durationIndex >= 0)
        {
            if (Array.LastIndexOf(args, durationOption) != durationIndex || durationIndex >= args.Length - 1 ||
                !double.TryParse(args[durationIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds) ||
                !double.IsFinite(durationSeconds) || durationSeconds <= 0d || durationSeconds > maximumDurationSeconds)
            {
                throw new ArgumentException($"{durationOption} 必须是 0 到 {maximumDurationSeconds:0} 秒之间的有限数");
            }
        }

        return new AudioSmokeRequest(filePath, durationSeconds);
    }

    private sealed record AudioSmokeRequest(string FilePath, double DurationSeconds);
}





