using Cubit.Audio;
using Silk.NET.SDL;

namespace Cubit.Audio.Android;

/// <summary>SDL 队列设备的固定 PCM 格式描述。</summary>
public readonly record struct SdlAudioDeviceFormat(int SampleRate, byte Channels, ushort SampleFormat)
{
    private const ushort AudioFloat32System = 0x8120;

    /// <summary>Cubit Audio 1.0 唯一支持的设备格式。</summary>
    public static SdlAudioDeviceFormat Fixed { get; } = new(
        AudioFormat.SampleRate,
        AudioFormat.Channels,
        AudioFloat32System);
}

/// <summary>
/// Android SDL 音频设备的小型可替换入口。实现必须在 <see cref="QueueAudio"/> 返回前消费输入样本。
/// </summary>
public interface ISdlAudioAdapter : IDisposable
{
    uint OpenAudioDevice(SdlAudioDeviceFormat requestedFormat, out SdlAudioDeviceFormat obtainedFormat);

    int GetQueuedAudioSize(uint deviceId);

    bool QueueAudio(uint deviceId, ReadOnlySpan<float> samples);

    void SetAudioDevicePaused(uint deviceId, bool paused);

    void ClearQueuedAudio(uint deviceId);

    void CloseAudioDevice(uint deviceId);

    string GetError();
}

public sealed record SdlAudioDiagnostic(string Code, string Message);

/// <summary>Android 后端的诊断收集器。宿主可在生命周期边界读取快照。</summary>
public sealed class SdlAudioDiagnostics
{
    private readonly object _sync = new();
    private readonly List<SdlAudioDiagnostic> _items = [];

    public void Add(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_sync)
        {
            _items.Add(new SdlAudioDiagnostic(code, message));
        }
    }

    public SdlAudioDiagnostic[] Snapshot()
    {
        lock (_sync)
        {
            return _items.ToArray();
        }
    }
}

/// <summary>平台音频设备初始化失败时返回的稳定异常。</summary>
public sealed class AudioBackendException : InvalidOperationException
{
    public AudioBackendException(string diagnosticCode, string message)
        : base(message)
    {
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

/// <summary>
/// Android SDL2 队列音频输出后端。每个实例只持有一个设备，不创建额外工作线程或全局服务。
/// </summary>
public sealed class SdlAudioOutputBackend : IAudioOutputBackend
{
    private const int DefaultMaximumQueuedBytes = AudioFormat.SampleRate * AudioFormat.Channels * sizeof(float) / 2;

    private readonly object _sync = new();
    private readonly ISdlAudioAdapter _adapter;
    private readonly SdlAudioDiagnostics _diagnostics;
    private readonly int _maximumQueuedBytes;
    private readonly uint _deviceId;
    private readonly SdlAudioDeviceFormat _deviceFormat;
    private long _submittedBytes;
    private bool _closed;
    private bool _closedDiagnosticReported;
    private bool _queueFullDiagnosticReported;

    /// <summary>稳定的后端标识。</summary>
    public const string BackendId = "cubit.audio.android.sdl2";

    public SdlAudioOutputBackend(SdlAudioDiagnostics diagnostics, int maximumQueuedBytes = DefaultMaximumQueuedBytes)
        : this(new SilkSdlAudioAdapter(), diagnostics, maximumQueuedBytes)
    {
    }

    /// <summary>用可替换 SDL 入口创建后端，供宿主集成与无需真机的契约验证使用。</summary>
    public SdlAudioOutputBackend(
        ISdlAudioAdapter adapter,
        SdlAudioDiagnostics diagnostics,
        int maximumQueuedBytes = DefaultMaximumQueuedBytes)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        if (maximumQueuedBytes < sizeof(float) * AudioFormat.Channels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumQueuedBytes),
                "音频队列上限至少必须容纳一个立体声 float 样本帧");
        }

        _maximumQueuedBytes = maximumQueuedBytes;
        try
        {
            _deviceId = _adapter.OpenAudioDevice(SdlAudioDeviceFormat.Fixed, out var obtainedFormat);
            if (_deviceId == 0)
            {
                ThrowOpenFailure("无法打开 Android SDL 音频设备");
            }

            if (obtainedFormat != SdlAudioDeviceFormat.Fixed)
            {
                TryCloseDuringConstruction();
                Report(
                    "audio.backend.format_mismatch",
                    $"SDL 音频设备格式不匹配: {obtainedFormat.SampleRate}Hz/{obtainedFormat.Channels}ch/0x{obtainedFormat.SampleFormat:X4}");
                throw new AudioBackendException(
                    "audio.backend.format_mismatch",
                    "Android SDL 音频设备未协商到 Cubit Audio 1.0 固定格式");
            }

            _deviceFormat = obtainedFormat;
            _adapter.SetAudioDevicePaused(_deviceId, paused: true);
            _adapter.ClearQueuedAudio(_deviceId);
            _adapter.SetAudioDevicePaused(_deviceId, paused: false);
        }
        catch (AudioBackendException)
        {
            _adapter.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            TryCloseDuringConstruction();
            Report(
                "audio.backend.open_failed",
                $"Android SDL 音频设备初始化失败: {exception.GetType().Name}");
            _adapter.Dispose();
            throw new AudioBackendException("audio.backend.open_failed", "Android SDL 音频设备初始化失败");
        }
    }

    public string Id => BackendId;

    /// <summary>设备队列允许保留的最大字节数。</summary>
    public int MaximumQueuedBytes => _maximumQueuedBytes;

    /// <summary>SDL 打开的设备标识，供 Android 宿主记录初始化证据；后端关闭后仍保留原始值。</summary>
    public uint DeviceId => _deviceId;

    /// <summary>SDL 实际协商的设备格式。</summary>
    public SdlAudioDeviceFormat DeviceFormat => _deviceFormat;

    /// <summary>当前 SDL 队列字节数；关闭后返回零，避免生命周期日志触碰已释放的 SDL 句柄。</summary>
    public int QueuedBytes
    {
        get
        {
            lock (_sync)
            {
                return _closed ? 0 : Math.Max(0, _adapter.GetQueuedAudioSize(_deviceId));
            }
        }
    }

    /// <summary>当前实例成功交给 SDL 的 PCM 累计字节数；不把丢弃或失败提交计入其中。</summary>
    public long SubmittedBytes
    {
        get
        {
            lock (_sync)
            {
                return _submittedBytes;
            }
        }
    }

    /// <summary>向 SDL 队列提交已经混音的完整立体声 float PCM 数据。</summary>
    public void Submit(ReadOnlySpan<float> samples)
    {
        if ((samples.Length & 1) != 0)
        {
            throw new ArgumentException("音频输出必须是完整的立体声样本帧", nameof(samples));
        }

        var byteCount = checked(samples.Length * sizeof(float));
        lock (_sync)
        {
            if (_closed)
            {
                ReportClosedOnce();
                return;
            }

            var queuedBytes = _adapter.GetQueuedAudioSize(_deviceId);
            if (queuedBytes < 0 || byteCount > _maximumQueuedBytes - Math.Min(queuedBytes, _maximumQueuedBytes))
            {
                ReportQueueFullOnce();
                return;
            }

            if (_adapter.QueueAudio(_deviceId, samples))
            {
                _submittedBytes = checked(_submittedBytes + byteCount);
            }
            else
            {
                Report(
                    "audio.backend.queue_failed",
                    $"Android SDL 音频队列提交失败: {_adapter.GetError()}");
            }
        }
    }

    /// <summary>暂停、清空、关闭设备并释放 SDL 绑定。允许重复调用。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try
            {
                _adapter.SetAudioDevicePaused(_deviceId, paused: true);
                _adapter.ClearQueuedAudio(_deviceId);
                _adapter.CloseAudioDevice(_deviceId);
            }
            finally
            {
                _adapter.Dispose();
            }
        }
    }

    private void ThrowOpenFailure(string fallbackMessage)
    {
        var detail = _adapter.GetError();
        Report(
            "audio.backend.open_failed",
            string.IsNullOrWhiteSpace(detail) ? fallbackMessage : $"{fallbackMessage}: {detail}");
        throw new AudioBackendException("audio.backend.open_failed", fallbackMessage);
    }

    private void TryCloseDuringConstruction()
    {
        if (_deviceId == 0)
        {
            return;
        }

        try
        {
            _adapter.SetAudioDevicePaused(_deviceId, paused: true);
            _adapter.ClearQueuedAudio(_deviceId);
            _adapter.CloseAudioDevice(_deviceId);
        }
        catch
        {
            // 构造失败路径只保留最初的稳定诊断，不以清理异常替换它。
        }
    }

    private void ReportClosedOnce()
    {
        if (_closedDiagnosticReported)
        {
            return;
        }

        _closedDiagnosticReported = true;
        Report("audio.backend.closed", "Android SDL 音频后端已经关闭，忽略提交");
    }

    private void ReportQueueFullOnce()
    {
        if (_queueFullDiagnosticReported)
        {
            return;
        }

        _queueFullDiagnosticReported = true;
        Report("audio.backend.queue_full", "Android SDL 音频队列已达到固定上限，丢弃当前 PCM 块");
    }

    private void Report(string code, string message) =>
        _diagnostics.Add(code, message);
}

internal sealed unsafe class SilkSdlAudioAdapter : ISdlAudioAdapter
{
    private readonly Sdl _sdl = Sdl.GetApi();
    private bool _audioInitialized;
    private bool _disposed;

    public uint OpenAudioDevice(SdlAudioDeviceFormat requestedFormat, out SdlAudioDeviceFormat obtainedFormat)
    {
        ThrowIfDisposed();
        if (_sdl.InitSubSystem(Sdl.InitAudio) != 0)
        {
            obtainedFormat = default;
            return 0;
        }

        _audioInitialized = true;
        var requested = new AudioSpec
        {
            Freq = requestedFormat.SampleRate,
            Format = requestedFormat.SampleFormat,
            Channels = requestedFormat.Channels,
            Samples = 1024,
        };
        AudioSpec obtained = default;
        var deviceId = _sdl.OpenAudioDevice((byte*)null, 0, &requested, &obtained, 0);
        obtainedFormat = new SdlAudioDeviceFormat(obtained.Freq, obtained.Channels, (ushort)obtained.Format);
        return deviceId;
    }

    public int GetQueuedAudioSize(uint deviceId)
    {
        ThrowIfDisposed();
        return checked((int)_sdl.GetQueuedAudioSize(deviceId));
    }

    public bool QueueAudio(uint deviceId, ReadOnlySpan<float> samples)
    {
        ThrowIfDisposed();
        return _sdl.QueueAudio(deviceId, samples, checked((uint)(samples.Length * sizeof(float)))) == 0;
    }

    public void SetAudioDevicePaused(uint deviceId, bool paused)
    {
        ThrowIfDisposed();
        _sdl.PauseAudioDevice(deviceId, paused ? 1 : 0);
    }

    public void ClearQueuedAudio(uint deviceId)
    {
        ThrowIfDisposed();
        _sdl.ClearQueuedAudio(deviceId);
    }

    public void CloseAudioDevice(uint deviceId)
    {
        ThrowIfDisposed();
        _sdl.CloseAudioDevice(deviceId);
    }

    public string GetError()
    {
        ThrowIfDisposed();
        return _sdl.GetErrorS();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_audioInitialized)
        {
            _sdl.QuitSubSystem(Sdl.InitAudio);
        }

        _sdl.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
