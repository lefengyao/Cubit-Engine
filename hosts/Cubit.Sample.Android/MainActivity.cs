using System.Collections.Concurrent;
using System.Threading;
using Silk.NET.Windowing;
using Cubit.Core.Engine;
using Cubit.Core.Input;
using Cubit.Audio.Android;
using Cubit.Core.Project;
using Cubit.Sample.Android.Platform;

namespace Cubit.Sample.Android;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    LaunchMode = global::Android.Content.PM.LaunchMode.SingleTask,
    ScreenOrientation = global::Android.Content.PM.ScreenOrientation.Landscape,
    ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation
        | global::Android.Content.PM.ConfigChanges.ScreenSize
        | global::Android.Content.PM.ConfigChanges.ScreenLayout
        | global::Android.Content.PM.ConfigChanges.SmallestScreenSize
        | global::Android.Content.PM.ConfigChanges.KeyboardHidden
        | global::Android.Content.PM.ConfigChanges.UiMode)]
public class MainActivity : Silk.NET.Windowing.Sdl.Android.SilkActivity
{
    private Engine? _engine;
    private GameScene? _scene;
    private SdlAudioOutputBackend? _audioBackend;
    private global::Android.Media.AudioManager? _audioManager;
    private readonly CubitAudioFocusListener _audioFocusListener = new();
    private global::Android.Media.AudioFocusRequestClass? _audioFocusRequest;
    private bool _audioFocusRequested;
    private bool _audioSmokeEnabled;
    private bool _audioSmokeTelemetryLogged;
    private bool _touchInputTelemetryLogged;
    private bool _touchDispatchTelemetryLogged;
    private bool _moveGestureTelemetryLogged;
    private bool _lookGestureTelemetryLogged;
    private bool _moveSnapshotTelemetryLogged;
    private bool _lookSnapshotTelemetryLogged;
    private bool _shutdown;
    private int _shutdownRequested;
    private readonly InputServer _inputServer = new();
    private readonly TouchInputEventQueue _touchInput = new(1280f, 720f, lookSensitivity: 0.75f);
    private readonly ConcurrentQueue<MobileTouchEvent> _pendingMobileTouchEvents = new();
    private readonly Dictionary<int, MobileTouchAction> _buttonPointers = [];
    private MobileTouchOverlay? _mobileTouchOverlay;
    private int _touchViewportWidth = 1280;
    private int _touchViewportHeight = 720;
    private int _appliedTouchViewportWidth;
    private int _appliedTouchViewportHeight;

    protected override void OnRun()
    {
        var view = Silk.NET.Windowing.Window.GetView(ViewOptions.DefaultVulkan);
        var window = new SdlGameWindow(view);
        var engine = new Engine(window);
        engine.RenderError += exception =>
            global::Android.Util.Log.Error("Cubit", $"渲染异常: {exception}");
        var project = ProjectIO.Load(PrepareProjectRoot());
        _engine = engine;

        view.Load += () =>
        {
            global::Android.Util.Log.Info("Cubit", "Vulkan 视图已创建");
        };

        // 项目运行时只从 project.cubit.json 的 mainScene 构造场景树。
        engine.Load += () =>
        {
            if (engine.Vulkan is { } vulkan)
            {
                vulkan.Diagnostic += message => global::Android.Util.Log.Info("Cubit", message);
            }

            if (AcquireAudioFocus())
            {
                try
                {
                    _audioBackend = new SdlAudioOutputBackend(new SdlAudioDiagnostics());
                }
                catch (AudioBackendException exception)
                {
                    global::Android.Util.Log.Warn("Cubit", $"音频设备不可用: {exception.DiagnosticCode}");
                }
            }

            _scene = new GameScene(
                engine.Vulkan!,
                project,
                _inputServer,
                _audioBackend);
            _audioSmokeEnabled = Intent?.GetBooleanExtra(AudioSmokeSignal.AndroidIntentExtra, false) == true;
            if (_audioSmokeEnabled && _audioBackend is not null)
            {
                var audioServer = _scene.Runtime.Context.GetRequiredService<Cubit.Audio.AudioServer>();
                _ = audioServer.Play(AudioSmokeSignal.CreateClip(), volume: 0.65f, loop: true);
                global::Android.Util.Log.Info(
                    "Cubit",
                    $"音频 smoke 已启用: id={_audioBackend.DeviceId}, format={_audioBackend.DeviceFormat}, queued={_audioBackend.QueuedBytes}");
            }
            engine.ConfigureDisplay(1280, 720, keepAspect: false); // 真机全屏铺满（不做 letterbox）
            global::Android.Util.Log.Info("Cubit", $"项目场景已创建: {_scene.Tree.Root.Children.Count} 节点");
        };

        // 每帧驱动场景（固定 20Hz 模拟 tick + 渲染插值），并消费本帧触摸快照。
        engine.Update += delta =>
        {
            if (Volatile.Read(ref _shutdownRequested) != 0)
            {
                Shutdown();
                return;
            }

            if (_scene is null)
            {
                return;
            }

            ApplyPublishedTouchViewport();
            DrainMobileTouchEvents();
            var touch = _touchInput.BuildSnapshot();
            if (!_touchInputTelemetryLogged
                && (touch.MoveX != 0f || touch.MoveY != 0f || touch.LookDeltaX != 0f || touch.LookDeltaY != 0f))
            {
                _touchInputTelemetryLogged = true;
                global::Android.Util.Log.Info(
                    "Cubit",
                    $"触摸快照已进入游戏循环: move=({touch.MoveX:0.000},{touch.MoveY:0.000}), look=({touch.LookDeltaX:0.000},{touch.LookDeltaY:0.000})");
            }
            if (!_moveSnapshotTelemetryLogged && (touch.MoveX != 0f || touch.MoveY != 0f))
            {
                _moveSnapshotTelemetryLogged = true;
                LogMobileControlTrace($"移动输入快照已进入游戏循环: move=({touch.MoveX:0.000},{touch.MoveY:0.000})");
            }
            if (!_lookSnapshotTelemetryLogged && (touch.LookDeltaX != 0f || touch.LookDeltaY != 0f))
            {
                _lookSnapshotTelemetryLogged = true;
                LogMobileControlTrace($"视角输入快照已进入游戏循环: look=({touch.LookDeltaX:0.000},{touch.LookDeltaY:0.000})");
            }

            _inputServer.SetMoveAxis(touch.MoveX, touch.MoveY);
            _inputServer.AddLookDelta(touch.LookDeltaX, touch.LookDeltaY);

            _scene.ProcessFrame(delta);
            if (_audioSmokeEnabled && !_audioSmokeTelemetryLogged && _audioBackend is not null)
            {
                _audioSmokeTelemetryLogged = true;
                global::Android.Util.Log.Info(
                    "Cubit",
                    $"音频 smoke 已提交 PCM: id={_audioBackend.DeviceId}, format={_audioBackend.DeviceFormat}, submitted={_audioBackend.SubmittedBytes}, queued={_audioBackend.QueuedBytes}");
            }
        };

        // Closing 可能由 Android UI 线程触发；这里只发布请求，不能跨线程释放场景树。
        view.Closing += () => Volatile.Write(ref _shutdownRequested, 1);

        engine.Run();
    }

    /// <summary>Android Asset 不能直接作为普通文件加载，启动时将项目清单和主场景复制到应用缓存。</summary>
    private string PrepareProjectRoot()
    {
        var cacheDirectory = CacheDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android 缓存目录不可用");
        var projectRoot = Path.Combine(cacheDirectory, "cubit-project");
        CopyProjectAsset("project.cubit.json", projectRoot);
        CopyProjectAsset("scenes/main.cscene", projectRoot);
        return projectRoot;
    }

    private void CopyProjectAsset(string assetPath, string projectRoot)
    {
        var targetPath = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"项目资源目录无效: {assetPath}");
        Directory.CreateDirectory(targetDirectory);
        using var source = Assets?.Open(assetPath)
            ?? throw new FileNotFoundException($"找不到 Android 项目资源: {assetPath}");
        using var target = File.Create(targetPath);
        source.CopyTo(target);
    }

    /// <summary>SDL 在创建 SurfaceView 时调用；Surface 只负责呈现，触摸由上层覆盖 View 独占。</summary>
    protected override global::Org.Libsdl.App.SDLSurface CreateSDLSurface(global::Android.Content.Context? context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new CubitTouchSurface(context, InstallMobileTouchOverlay);
    }

    /// <summary>
    /// 在 Android 向 Overlay 或 SDL Surface 分发前记录触摸。这样渲染视图的层级变化不会改变
    /// Cubit 的输入链路，随后仍交给基类完成正常的 Android 视图分发。
    /// </summary>
    public override bool DispatchTouchEvent(global::Android.Views.MotionEvent? motionEvent)
    {
        if (motionEvent is not null)
        {
            QueueMobileTouch(motionEvent);
        }

        return base.DispatchTouchEvent(motionEvent);
    }

    /// <summary>在 SDL 根布局之上安装可见的移动操控层；触摸仍由 Activity 在分发前统一路由。</summary>
    private void InstallMobileTouchOverlay(global::Android.Views.ViewGroup layout)
    {
        RunOnUiThread(() =>
        {
            if (_mobileTouchOverlay is not null)
            {
                return;
            }

            var overlay = new MobileTouchOverlay(this, PublishTouchViewport);
            layout.AddView(
                overlay,
                new global::Android.Widget.RelativeLayout.LayoutParams(
                    global::Android.Views.ViewGroup.LayoutParams.MatchParent,
                    global::Android.Views.ViewGroup.LayoutParams.MatchParent));
            _mobileTouchOverlay = overlay;
            global::Android.Util.Log.Info("Cubit", "移动触摸覆盖层已安装");
        });
    }

    /// <summary>Activity UI 线程发布实际覆盖层尺寸；游戏线程只在帧边界更新映射器。</summary>
    private void PublishTouchViewport(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Volatile.Write(ref _touchViewportWidth, width);
        Volatile.Write(ref _touchViewportHeight, height);
    }

    /// <summary>由游戏线程应用最新视口，尺寸改变时取消旧手势以避免坐标系混用。</summary>
    private void ApplyPublishedTouchViewport()
    {
        var width = Volatile.Read(ref _touchViewportWidth);
        var height = Volatile.Read(ref _touchViewportHeight);
        if (width <= 0 || height <= 0 || (width == _appliedTouchViewportWidth && height == _appliedTouchViewportHeight))
        {
            return;
        }

        _touchInput.Resize(width, height);
        _appliedTouchViewportWidth = width;
        _appliedTouchViewportHeight = height;
    }

    /// <summary>UI 线程只复制 MotionEvent 值，不能把 Android 的可复用事件对象带入游戏线程。</summary>
    private void QueueMobileTouch(global::Android.Views.MotionEvent motionEvent)
    {
        switch (motionEvent.ActionMasked)
        {
            case global::Android.Views.MotionEventActions.Down:
            case global::Android.Views.MotionEventActions.PointerDown:
                EnqueuePointerEvent(MobileTouchEventKind.Down, motionEvent, motionEvent.ActionIndex);
                break;
            case global::Android.Views.MotionEventActions.Move:
                for (var index = 0; index < motionEvent.PointerCount; index++)
                {
                    EnqueuePointerEvent(MobileTouchEventKind.Move, motionEvent, index);
                }

                break;
            case global::Android.Views.MotionEventActions.Up:
            case global::Android.Views.MotionEventActions.PointerUp:
                EnqueuePointerEvent(MobileTouchEventKind.Up, motionEvent, motionEvent.ActionIndex);
                break;
            case global::Android.Views.MotionEventActions.Cancel:
                _pendingMobileTouchEvents.Enqueue(new MobileTouchEvent(MobileTouchEventKind.Cancel, 0, 0f, 0f));
                break;
        }
    }

    /// <summary>游戏线程在帧开始按平台到达顺序路由触点，所有玩法状态仅在此线程修改。</summary>
    private void DrainMobileTouchEvents()
    {
        while (_pendingMobileTouchEvents.TryDequeue(out var touchEvent))
        {
            switch (touchEvent.Kind)
            {
                case MobileTouchEventKind.Down:
                    RoutePointerDown(touchEvent.PointerId, touchEvent.X, touchEvent.Y);
                    break;
                case MobileTouchEventKind.Move:
                    RoutePointerMove(touchEvent.PointerId, touchEvent.X, touchEvent.Y);
                    break;
                case MobileTouchEventKind.Up:
                    RoutePointerUp(touchEvent.PointerId);
                    break;
                case MobileTouchEventKind.Cancel:
                    _buttonPointers.Clear();
                    _touchInput.EnqueueCancel();
                    UpdateMobileTouchOverlay(overlay => overlay.CancelMoveGesture());
                    ApplyProjectButtonState();
                    break;
                default:
                    throw new InvalidOperationException($"未知移动端触点事件: {touchEvent.Kind}");
            }
        }
    }

    private void EnqueuePointerEvent(
        MobileTouchEventKind kind,
        global::Android.Views.MotionEvent motionEvent,
        int index) =>
        _pendingMobileTouchEvents.Enqueue(new MobileTouchEvent(
            kind,
            motionEvent.GetPointerId(index),
            motionEvent.GetX(index),
            motionEvent.GetY(index)));

    private void RoutePointerDown(int pointerId, float x, float y)
    {
        var action = MobileTouchLayout.HitTestAction(
            x,
            y,
            Volatile.Read(ref _touchViewportWidth),
            Volatile.Read(ref _touchViewportHeight),
            Resources?.DisplayMetrics?.Density ?? 1f);
        if (action is { } buttonAction)
        {
            _buttonPointers[pointerId] = buttonAction;
            ApplyProjectButtonState();
            return;
        }

        _touchInput.EnqueuePointerDown(pointerId, x, y);
        UpdateMobileTouchOverlay(overlay => overlay.BeginMoveGesture(pointerId, x, y));
        var moveZoneBoundary = Volatile.Read(ref _touchViewportWidth) * 0.5f;
        if (x <= moveZoneBoundary && !_moveGestureTelemetryLogged)
        {
            _moveGestureTelemetryLogged = true;
            LogMobileControlTrace($"移动手势已路由: id={pointerId}, x={x:0.0}, y={y:0.0}, boundary={moveZoneBoundary:0.0}");
        }
        else if (x > moveZoneBoundary && !_lookGestureTelemetryLogged)
        {
            _lookGestureTelemetryLogged = true;
            LogMobileControlTrace($"视角手势已路由: id={pointerId}, x={x:0.0}, y={y:0.0}, boundary={moveZoneBoundary:0.0}");
        }
        if (!_touchDispatchTelemetryLogged)
        {
            _touchDispatchTelemetryLogged = true;
            global::Android.Util.Log.Info("Cubit", $"Android Activity 触摸路由已接收 Down: id={pointerId}, x={x:0.0}, y={y:0.0}");
        }
    }

    private void RoutePointerMove(int pointerId, float x, float y)
    {
        if (_buttonPointers.ContainsKey(pointerId))
        {
            return;
        }

        _touchInput.EnqueuePointerMove(pointerId, x, y);
        UpdateMobileTouchOverlay(overlay => overlay.UpdateMoveGesture(pointerId, x, y));
    }

    private void RoutePointerUp(int pointerId)
    {
        if (_buttonPointers.Remove(pointerId))
        {
            ApplyProjectButtonState();
            return;
        }

        UpdateMobileTouchOverlay(overlay => overlay.EndMoveGesture(pointerId));
        _touchInput.EnqueuePointerUp(pointerId);
    }

    private void ApplyProjectButtonState()
    {
        var primaryPressed = _buttonPointers.Values.Any(action => action == MobileTouchAction.Primary);
        var secondaryPressed = _buttonPointers.Values.Any(action => action == MobileTouchAction.Secondary);
        _scene?.RouteProjectInput(new ProjectInputEvent(ProjectInputControl.PrimaryPointer, primaryPressed));
        _scene?.RouteProjectInput(new ProjectInputEvent(ProjectInputControl.SecondaryPointer, secondaryPressed));
        UpdateMobileTouchOverlay(overlay => overlay.SetActionState(primaryPressed, secondaryPressed));
    }

    /// <summary>覆盖层始终由 Android UI 线程刷新；游戏线程只发布显示状态。</summary>
    private void UpdateMobileTouchOverlay(Action<MobileTouchOverlay> update)
    {
        var overlay = _mobileTouchOverlay;
        if (overlay is null)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (ReferenceEquals(_mobileTouchOverlay, overlay))
            {
                update(overlay);
            }
        });
    }

    private readonly record struct MobileTouchEvent(MobileTouchEventKind Kind, int PointerId, float X, float Y);

    private enum MobileTouchEventKind
    {
        Down,
        Move,
        Up,
        Cancel,
    }

    private static void LogMobileControlTrace(string message) => global::Android.Util.Log.Info("Cubit", message);

    /// <summary>恢复前台：Android 会重建 SurfaceView，强制引擎下一帧重建表面与交换链，避免黑屏。</summary>
    protected override void OnResume()
    {
        base.OnResume();
        _engine?.Vulkan?.InvalidateSurface();
    }

    /// <summary>Silk.NET/SDL 不支持同进程重建 Activity（单例限制）；被系统销毁时直接结束进程，避免 “Only one SilkActivity” 崩溃与黑屏。</summary>
    protected override void OnDestroy()
    {
        RequestShutdown();
        base.OnDestroy();
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    /// <summary>退到后台即退出进程：Silk.NET/SDL 不支持同进程重建 Activity，且本机 Adreno 驱动在恢复时重建交换链会段错误。</summary>
    /// <summary>回到前台 = 全新启动，稳定无崩溃（代价：冷启动约 2 秒）。</summary>
    protected override void OnStop()
    {
        RequestShutdown();
        base.OnStop();
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    /// <summary>Android 生命周期只发布关闭请求；SceneTree 与插件由游戏线程消费请求后释放。</summary>
    private void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return;
        }

        try
        {
            _engine?.RequestClose();
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Error("Cubit", $"请求 Android 引擎关闭失败: {exception}");
        }
    }

    /// <summary>平台窗口关闭或 Activity 退出前按场景、引擎顺序释放资源；允许多条 Android 生命周期路径重复触发。</summary>
    private void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _touchInput.Reset();
        var scene = _scene;
        _scene = null;
        scene?.Dispose();
        _audioBackend = null;
        ReleaseAudioFocus();
        var engine = _engine;
        _engine = null;
        engine?.Dispose();
    }

    /// <summary>请求媒体焦点，避免 Android 将前台游戏的 SDL 输出当作无焦点后台播放处理。</summary>
    private bool AcquireAudioFocus()
    {
        if (_audioFocusRequested)
        {
            return true;
        }

        _audioManager = GetSystemService(global::Android.Content.Context.AudioService)
            as global::Android.Media.AudioManager;
        if (_audioManager is null)
        {
            global::Android.Util.Log.Warn("Cubit", "无法取得 Android AudioManager，SDL 音频可能被系统静音");
            return false;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return AcquireModernAudioFocus(_audioManager);
        }

        return AcquireLegacyAudioFocus(_audioManager);
    }

    /// <summary>Android 8.0 及以上使用请求对象取得焦点，并在关闭时归还同一对象。</summary>
    [global::System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private bool AcquireModernAudioFocus(global::Android.Media.AudioManager audioManager)
    {
        var audioFocusRequest = new global::Android.Media.AudioFocusRequestClass.Builder(
                global::Android.Media.AudioFocus.Gain)
            .SetOnAudioFocusChangeListener(_audioFocusListener)
            .Build()
            ?? throw new InvalidOperationException("Android AudioFocusRequest 构建失败");
        var result = audioManager.RequestAudioFocus(audioFocusRequest);
        if (result != global::Android.Media.AudioFocusRequest.Granted)
        {
            global::Android.Util.Log.Warn("Cubit", $"Android 媒体焦点请求失败: {result}，SDL 音频设备不会启动");
            _audioManager = null;
            return false;
        }

        _audioFocusRequest = audioFocusRequest;
        _audioFocusRequested = true;
        return true;
    }

    /// <summary>Android 7.x 保持旧焦点重载；该分支只会在 API 26 以下运行。</summary>
    private bool AcquireLegacyAudioFocus(global::Android.Media.AudioManager audioManager)
    {
#pragma warning disable CA1422 // API 26+ 已弃用；运行时分支只允许 API 24-25 进入。
        var result = audioManager.RequestAudioFocus(
            _audioFocusListener,
            global::Android.Media.Stream.Music,
            global::Android.Media.AudioFocus.Gain);
#pragma warning restore CA1422
        if (result != global::Android.Media.AudioFocusRequest.Granted)
        {
            global::Android.Util.Log.Warn("Cubit", $"Android 旧版媒体焦点请求失败: {result}，SDL 音频设备不会启动");
            _audioManager = null;
            return false;
        }

        _audioFocusRequested = true;
        return true;
    }

    /// <summary>在 SDL 设备关闭后归还媒体焦点，允许其他应用恢复播放。</summary>
    private void ReleaseAudioFocus()
    {
        if (_audioManager is null)
        {
            return;
        }

        if (_audioFocusRequested && OperatingSystem.IsAndroidVersionAtLeast(26) && _audioFocusRequest is { } audioFocusRequest)
        {
            ReleaseModernAudioFocus(_audioManager, audioFocusRequest);
        }
        else if (_audioFocusRequested)
        {
            ReleaseLegacyAudioFocus(_audioManager);
        }

        _audioFocusRequested = false;
        _audioFocusRequest = null;
        _audioManager = null;
    }

    /// <summary>Android 8.0 及以上使用与请求阶段相同的对象归还媒体焦点。</summary>
    [global::System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    private static void ReleaseModernAudioFocus(
        global::Android.Media.AudioManager audioManager,
        global::Android.Media.AudioFocusRequestClass audioFocusRequest) =>
        _ = audioManager.AbandonAudioFocusRequest(audioFocusRequest);

    /// <summary>Android 7.x 使用与请求阶段配对的旧焦点重载。</summary>
    private void ReleaseLegacyAudioFocus(global::Android.Media.AudioManager audioManager)
    {
#pragma warning disable CA1422 // API 26+ 已弃用；运行时分支只允许 API 24-25 进入。
        _ = audioManager.AbandonAudioFocus(_audioFocusListener);
#pragma warning restore CA1422
    }

    private sealed class CubitAudioFocusListener : global::Java.Lang.Object, global::Android.Media.AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(global::Android.Media.AudioFocus focusChange)
        {
            global::Android.Util.Log.Info("Cubit", $"Android 音频焦点变化: {focusChange}");
        }
    }
}
