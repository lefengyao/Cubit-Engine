using System.Threading;
using System.Numerics;
using Cubit.Core.Diagnostics;

namespace Cubit.Audio;

/// <summary>
/// 平台无关的托管混音服务。播放命令只能在创建线程提交，混音回调只读取不可变语音快照。
/// </summary>
public sealed class AudioServer
{
    private readonly IAudioOutputBackend? _backend;
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<int, Voice> _voices = [];
    private readonly Dictionary<string, AudioBus> _buses = new(StringComparer.Ordinal);
    private VoiceSnapshot[] _snapshot = [];
    private int _nextVoiceId;
    private int _mixInProgress;
    private int _backendFailureReported;
    private readonly int _ownerThreadId;
    private Vector3 _listenerPosition;
    private Vector3 _listenerForward = -Vector3.UnitZ;
    private Vector3 _listenerUp = Vector3.UnitY;
    private bool _closed;

    public AudioServer(IAudioOutputBackend? backend, DiagnosticBag diagnostics)
    {
        _backend = backend;
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _buses.Add("Master", new AudioBus("Master", 1f));
        if (_backend is null)
        {
            _diagnostics.Add(new EngineDiagnostic(
                "audio.backend.missing",
                DiagnosticSeverity.Error,
                "未提供 Audio 输出后端；游戏不会创建隐式设备线程"));
        }
    }

    public bool IsClosed
    {
        get
        {
            EnsureOwnerThread();
            return _closed;
        }
    }

    public AudioServerStatus Status
    {
        get
        {
            EnsureOwnerThread();
            if (PruneFinishedVoices())
            {
                PublishSnapshot();
            }

            return new AudioServerStatus(_backend is not null && !_closed, _voices.Count, _diagnostics.Snapshot());
        }
    }

    /// <summary>已注册音频总线的名称序稳定快照。</summary>
    public IReadOnlyList<AudioBusStatus> Buses
    {
        get
        {
            EnsureOwnerThread();
            return _buses.Values
                .OrderBy(bus => bus.Name, StringComparer.Ordinal)
                .Select(bus => new AudioBusStatus(bus.Name, bus.Volume, bus.Muted))
                .ToArray();
        }
    }

    /// <summary>
    /// 在主线程的明确帧边界回收混音回调已完成的语音。游戏每帧应调用一次。
    /// </summary>
    public bool Maintain()
    {
        EnsureOwnerThread();
        if (_closed)
        {
            return false;
        }

        if (!PruneFinishedVoices())
        {
            return false;
        }

        PublishSnapshot();
        return true;
    }

    public Vector3 ListenerPosition
    {
        get
        {
            EnsureOwnerThread();
            return _listenerPosition;
        }
    }

    public void SetListenerPosition(Vector3 position)
    {
        EnsureOwnerThread();
        if (!IsFinite(position))
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        _listenerPosition = position;
    }

    public Vector3 ListenerForward
    {
        get
        {
            EnsureOwnerThread();
            return _listenerForward;
        }
    }

    public Vector3 ListenerUp
    {
        get
        {
            EnsureOwnerThread();
            return _listenerUp;
        }
    }

    public Vector3 ListenerRight
    {
        get
        {
            EnsureOwnerThread();
            return Vector3.Normalize(Vector3.Cross(_listenerForward, _listenerUp));
        }
    }

    public void SetListenerOrientation(Vector3 forward, Vector3 up)
    {
        EnsureOwnerThread();
        if (!IsFinite(forward) || !IsFinite(up) ||
            forward.LengthSquared() <= float.Epsilon || up.LengthSquared() <= float.Epsilon)
        {
            throw new ArgumentOutOfRangeException(nameof(forward));
        }

        forward = Vector3.Normalize(forward);
        var right = Vector3.Cross(forward, up);
        if (!IsFinite(right) || right.LengthSquared() <= float.Epsilon)
        {
            throw new ArgumentException("Listener forward and up vectors cannot be collinear", nameof(up));
        }

        right = Vector3.Normalize(right);
        _listenerForward = forward;
        _listenerUp = Vector3.Normalize(Vector3.Cross(right, forward));
    }

    public AudioBus CreateBus(string name, float volume = 1f)
    {
        EnsureOwnerThread();
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(name) || !float.IsFinite(volume) || volume < 0f)
        {
            throw new ArgumentException("音频总线名称不能为空，音量必须是有限非负数", nameof(name));
        }

        if (PruneFinishedVoices())
        {
            PublishSnapshot();
        }

        var bus = new AudioBus(name.Trim(), volume);
        if (!_buses.TryAdd(bus.Name, bus))
        {
            throw new InvalidOperationException($"音频总线名称重复: {bus.Name}");
        }

        return bus;
    }

    public void SetBusVolume(string name, float volume)
    {
        EnsureOwnerThread();
        EnsureOpen();
        var bus = GetBus(name);
        if (!float.IsFinite(volume) || volume < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        if (!bus.Muted)
        {
            EnsureFiniteBusGain(bus, volume);
        }

        _ = PruneFinishedVoices();
        bus.Volume = volume;
        PublishSnapshot();
    }

    public void SetBusMuted(string name, bool muted)
    {
        EnsureOwnerThread();
        EnsureOpen();
        var bus = GetBus(name);
        if (!muted)
        {
            EnsureFiniteBusGain(bus, bus.Volume);
        }

        _ = PruneFinishedVoices();
        bus.Muted = muted;
        PublishSnapshot();
    }

    public AudioPlaybackHandle Play(
        AudioClip clip,
        float volume = 1f,
        float pan = 0f,
        bool loop = false,
        string busName = "Master")
    {
        EnsureOwnerThread();
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(clip);
        if (!float.IsFinite(volume) || volume < 0f || !float.IsFinite(pan) || pan < -1f || pan > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "音量和平衡必须是有限的有效范围");
        }

        var bus = GetBus(busName);
        EnsureFiniteVoiceGain(volume, pan, bus.Muted ? 0f : bus.Volume);
        _ = PruneFinishedVoices();
        var id = checked(++_nextVoiceId);
        _voices.Add(id, new Voice(id, clip, volume, pan, loop, bus));
        PublishSnapshot();
        return new AudioPlaybackHandle(id);
    }

    public void Stop(AudioPlaybackHandle handle)
    {
        EnsureOwnerThread();
        if (_closed)
        {
            return;
        }

        var changed = PruneFinishedVoices();
        if (handle.IsValid && _voices.Remove(handle.Id))
        {
            changed = true;
        }

        if (changed)
        {
            PublishSnapshot();
        }
    }

    public bool IsPlaying(AudioPlaybackHandle handle)
    {
        EnsureOwnerThread();
        if (_closed)
        {
            return false;
        }

        return handle.IsValid && _voices.TryGetValue(handle.Id, out var voice) && !Volatile.Read(ref voice.Finished);
    }

    /// <summary>主线程更新一个活动语音的表现参数，并发布新的只读混音快照。</summary>
    public bool UpdateVoice(AudioPlaybackHandle handle, float volume, float pan)
    {
        EnsureOwnerThread();
        if (_closed)
        {
            return false;
        }

        if (!float.IsFinite(volume) || volume < 0f || !float.IsFinite(pan) || pan < -1f || pan > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        if (!handle.IsValid || !_voices.TryGetValue(handle.Id, out var voice) || Volatile.Read(ref voice.Finished))
        {
            return false;
        }

        EnsureFiniteVoiceGain(volume, pan, voice.Bus.Muted ? 0f : voice.Bus.Volume);
        voice.Volume = volume;
        voice.Pan = pan;
        PublishSnapshot();
        return true;
    }

    /// <summary>设备回调入口；调用方拥有输出缓冲，混音过程不分配托管内存。</summary>
    /// <summary>
    /// 设备回调入口。单个 AudioServer 只允许一个设备回调串行调用，避免并发回调竞争语音游标。
    /// </summary>
    public void Mix(Span<float> output)
    {
        if (output.Length % AudioFormat.Channels != 0)
        {
            throw new ArgumentException("输出缓冲必须包含完整的双声道采样帧", nameof(output));
        }

        if (_closed)
        {
            output.Clear();
            return;
        }

        if (Interlocked.Exchange(ref _mixInProgress, 1) != 0)
        {
            throw new InvalidOperationException("AudioServer.Mix 只允许一个单设备回调串行调用");
        }

        try
        {
            MixUnsafe(output);
        }
        finally
        {
            Volatile.Write(ref _mixInProgress, 0);
        }
    }

    private void MixUnsafe(Span<float> output)
    {
        if (output.Length % AudioFormat.Channels != 0)
        {
            throw new ArgumentException("输出缓冲必须包含完整的双声道采样帧", nameof(output));
        }

        output.Clear();
        var snapshot = Volatile.Read(ref _snapshot);
        for (var voiceIndex = 0; voiceIndex < snapshot.Length; voiceIndex++)
        {
            var voiceSnapshot = snapshot[voiceIndex];
            var voice = voiceSnapshot.Voice;
            if (Volatile.Read(ref voice.Finished))
            {
                continue;
            }

            var clip = voice.Clip;
            if (clip is null)
            {
                continue;
            }

            var samples = clip.SampleSpan;
            var leftGain = voiceSnapshot.LeftGain;
            var rightGain = voiceSnapshot.RightGain;
            var position = voice.Position;
            for (var outputIndex = 0; outputIndex < output.Length; outputIndex += AudioFormat.Channels)
            {
                if (position >= clip.FrameCount)
                {
                    if (!voice.Loop)
                    {
                        CompleteVoice(voice);
                        break;
                    }

                    position = 0;
                }

                var sourceIndex = position * AudioFormat.Channels;
                output[outputIndex] += samples[sourceIndex] * leftGain;
                output[outputIndex + 1] += samples[sourceIndex + 1] * rightGain;
                position++;
            }

            voice.Position = position;
            if (!voice.Loop && position >= clip.FrameCount)
            {
                CompleteVoice(voice);
            }
        }

        for (var index = 0; index < output.Length; index++)
        {
            var sample = output[index];
            output[index] = float.IsNaN(sample) ? 0f : Math.Clamp(sample, -1f, 1f);
        }
    }

    /// <summary>混音并立即提交到显式输出后端；后端不得保留传入 Span。</summary>
    public void Render(Span<float> output)
    {
        EnsureOwnerThread();
        if (_closed)
        {
            output.Clear();
            return;
        }

        Mix(output);
        if (_backend is null || _closed || Volatile.Read(ref _backendFailureReported) != 0)
        {
            return;
        }

        try
        {
            _backend.Submit(output);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _backendFailureReported, 1) == 0)
            {
                _diagnostics.Add(new EngineDiagnostic(
                    "audio.backend.submit-failed",
                    DiagnosticSeverity.Error,
                    $"Audio output backend submit failed: {exception.GetType().Name}"));
            }
        }
    }

    public void Dispose()
    {
        EnsureOwnerThread();
        if (_closed)
        {
            return;
        }

        _closed = true;
        _voices.Clear();
        Volatile.Write(ref _snapshot, []);
        if (_backend is null)
        {
            return;
        }

        try
        {
            _backend.Dispose();
        }
        catch (Exception exception)
        {
            _diagnostics.Add(new EngineDiagnostic(
                "audio.backend.dispose-failed",
                DiagnosticSeverity.Error,
                $"Audio output backend dispose failed: {exception.GetType().Name}"));
        }
    }

    private void PublishSnapshot()
    {
        var voices = _voices.Values.ToArray();
        Array.Sort(voices, static (left, right) => left.Id.CompareTo(right.Id));
        var snapshot = new VoiceSnapshot[voices.Length];
        for (var index = 0; index < voices.Length; index++)
        {
            var voice = voices[index];
            var busGain = voice.Bus.Muted ? 0f : voice.Bus.Volume;
            var leftPan = voice.Pan <= 0f ? 1f : 1f - voice.Pan;
            var rightPan = voice.Pan >= 0f ? 1f : 1f + voice.Pan;
            var leftGain = voice.Volume * busGain * leftPan;
            var rightGain = voice.Volume * busGain * rightPan;
            if (!float.IsFinite(leftGain) || !float.IsFinite(rightGain))
            {
                throw new InvalidOperationException("Audio 语音增益超出有限 PCM 范围");
            }

            snapshot[index] = new VoiceSnapshot(
                voice,
                leftGain,
                rightGain);
        }

        Volatile.Write(ref _snapshot, snapshot);
    }

    /// <summary>
    /// 回调线程只标记已完成的语音。主线程在命令边界将其移出字典并重新发布快照，避免回调修改结构。
    /// </summary>
    private bool PruneFinishedVoices()
    {
        List<int>? finishedIds = null;
        foreach (var pair in _voices)
        {
            if (Volatile.Read(ref pair.Value.Finished))
            {
                (finishedIds ??= []).Add(pair.Key);
            }
        }

        if (finishedIds is null)
        {
            return false;
        }

        foreach (var id in finishedIds)
        {
            _voices.Remove(id);
        }

        return true;
    }

    private static void CompleteVoice(Voice voice)
    {
        Volatile.Write(ref voice.Finished, true);
        voice.ReleaseClip();
    }

    private void EnsureFiniteBusGain(AudioBus bus, float volume)
    {
        foreach (var voice in _voices.Values)
        {
            if (ReferenceEquals(voice.Bus, bus) && !Volatile.Read(ref voice.Finished))
            {
                EnsureFiniteVoiceGain(voice.Volume, voice.Pan, volume);
            }
        }
    }

    private static void EnsureFiniteVoiceGain(float volume, float pan, float busGain)
    {
        var leftPan = pan <= 0f ? 1f : 1f - pan;
        var rightPan = pan >= 0f ? 1f : 1f + pan;
        if (!float.IsFinite(volume * busGain * leftPan) || !float.IsFinite(volume * busGain * rightPan))
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Audio 语音增益必须保持在有限 PCM 范围");
        }
    }

    private AudioBus GetBus(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_buses.TryGetValue(name.Trim(), out var bus))
        {
            throw new InvalidOperationException($"找不到音频总线: {name}");
        }

        return bus;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("AudioServer 播放命令只能在创建线程提交");
        }
    }

    private void EnsureOpen()
    {
        if (_closed)
        {
            throw new InvalidOperationException("AudioServer is closed");
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class Voice(int id, AudioClip clip, float volume, float pan, bool loop, AudioBus bus)
    {
        public int Id { get; } = id;
        private AudioClip? _clip = clip;
        public AudioClip? Clip => Volatile.Read(ref _clip);
        public float Volume { get; set; } = volume;
        public float Pan { get; set; } = pan;
        public bool Loop { get; } = loop;
        public AudioBus Bus { get; } = bus;
        public int Position;
        public bool Finished;

        public void ReleaseClip() => Interlocked.Exchange(ref _clip, null);
    }

    private readonly struct VoiceSnapshot(Voice voice, float leftGain, float rightGain)
    {
        public Voice Voice { get; } = voice;
        public float LeftGain { get; } = leftGain;
        public float RightGain { get; } = rightGain;
    }
}
