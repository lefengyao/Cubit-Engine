using System.Numerics;
using Cubit.Core.Scene;

namespace Cubit.Audio;

/// <summary>场景树中的普通声音播放器；服务器必须由宿主显式注入。</summary>
public class AudioPlayer : Node
{
    private AudioClip? _clip;
    private string _busName = "Master";
    private float _volume = 1f;
    private float _pan;
    private bool _loop;
    private bool _autoplay;
    private AudioServer? _server;
    private AudioPlaybackHandle _handle;
    private int _ownerThreadId;
    private bool _hasAppliedParameters;
    private bool _autoplayPending;
    private float _appliedVolume;
    private float _appliedPan;

    [Export("音频片段")]
    public AudioClip? Clip
    {
        get
        {
            EnsureMainThread();
            return _clip;
        }
        set
        {
            EnsureMainThread();
            _clip = value;
        }
    }

    public string BusName
    {
        get
        {
            EnsureMainThread();
            return _busName;
        }
        set
        {
            EnsureMainThread();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("音频总线名称不能为空", nameof(value));
            }

            _busName = value.Trim();
        }
    }

    public float Volume
    {
        get
        {
            EnsureMainThread();
            return _volume;
        }
        set
        {
            EnsureMainThread();
            ValidateVolume(value, nameof(value));
            _volume = value;
        }
    }

    public float Pan
    {
        get
        {
            EnsureMainThread();
            return _pan;
        }
        set
        {
            EnsureMainThread();
            ValidatePan(value, nameof(value));
            _pan = value;
        }
    }

    public bool Loop
    {
        get
        {
            EnsureMainThread();
            return _loop;
        }
        set
        {
            EnsureMainThread();
            _loop = value;
        }
    }

    public bool Autoplay
    {
        get
        {
            EnsureMainThread();
            return _autoplay;
        }
        set
        {
            EnsureMainThread();
            _autoplay = value;
        }
    }

    public bool IsPlaying
    {
        get
        {
            EnsureMainThread();
            return _handle.IsValid && _server?.IsPlaying(_handle) == true;
        }
    }

    public void AttachServer(AudioServer server)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(server);
        if (!ReferenceEquals(_server, server))
        {
            if (_handle.IsValid)
            {
                _server?.Stop(_handle);
            }

            _handle = default;
            _hasAppliedParameters = false;
            _server = server;
        }

        StartPendingAutoplay();
    }

    public virtual void Play()
    {
        EnsureMainThread();
        PlayWithParameters(Volume, Pan);
    }

    internal void PlayWithParameters(float volume, float pan)
    {
        EnsureMainThread();
        var server = _server ?? throw new InvalidOperationException("AudioPlayer 尚未附加 AudioServer");
        var clip = Clip ?? throw new InvalidOperationException("AudioPlayer 没有 AudioClip");
        if (_handle.IsValid)
        {
            server.Stop(_handle);
            _handle = default;
        }

        _handle = server.Play(clip, volume, pan, Loop, BusName);
        _appliedVolume = volume;
        _appliedPan = pan;
        _hasAppliedParameters = true;
        _autoplayPending = false;
    }

    internal bool UpdateParameters(float volume, float pan)
    {
        EnsureMainThread();
        if (!_handle.IsValid || _server is null)
        {
            return false;
        }

        if (_hasAppliedParameters && volume == _appliedVolume && pan == _appliedPan)
        {
            return true;
        }

        if (!_server.UpdateVoice(_handle, volume, pan))
        {
            _hasAppliedParameters = false;
            return false;
        }

        _appliedVolume = volume;
        _appliedPan = pan;
        _hasAppliedParameters = true;
        return true;
    }

    public void Stop()
    {
        EnsureMainThread();
        if (_handle.IsValid)
        {
            _server?.Stop(_handle);
            _handle = default;
        }

        _hasAppliedParameters = false;
        _autoplayPending = false;
    }

    protected override void EnterTree()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    protected override void Ready()
    {
        if (Autoplay)
        {
            if (_server is null)
            {
                _autoplayPending = true;
            }
            else
            {
                Play();
            }
        }
    }

    protected override void Process(double delta)
    {
        EnsureMainThread();
        if (!_handle.IsValid || _server is null)
        {
            return;
        }

        if (!_server.IsPlaying(_handle))
        {
            Stop();
            return;
        }

        var (volume, pan) = GetPlaybackParameters(_server);
        _ = UpdateParameters(volume, pan);
    }

    protected override void ExitTree() => Stop();

    protected virtual (float Volume, float Pan) GetPlaybackParameters(AudioServer server)
    {
        if (!float.IsFinite(_volume) || _volume < 0f || !float.IsFinite(_pan) || _pan < -1f || _pan > 1f)
        {
            throw new InvalidOperationException("AudioPlayer 音量或声像无效");
        }

        return (_volume, _pan);
    }

    private static void ValidateVolume(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName, "音量必须是有限非负数");
        }
    }

    private static void ValidatePan(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < -1f || value > 1f)
        {
            throw new ArgumentOutOfRangeException(parameterName, "声像必须是 [-1, 1] 范围内的有限数");
        }
    }

    private void StartPendingAutoplay()
    {
        if (!_autoplayPending || _server is null || Tree is null)
        {
            return;
        }

        Play();
    }

    private void EnsureMainThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == 0)
        {
            _ownerThreadId = current;
        }
        else if (_ownerThreadId != current)
        {
            throw new InvalidOperationException("AudioPlayer 只能在拥有它的主线程访问");
        }
    }
}

/// <summary>基于服务器监听位置的轻量空间声音播放器。</summary>
public sealed class AudioPlayer3D : Node3D
{
    private AudioClip? _clip;
    private string _busName = "Master";
    private float _volume = 1f;
    private bool _loop;
    private bool _autoplay;
    private float _minDistance = 1f;
    private float _maxDistance = 50f;
    private readonly AudioPlayer _playback = new();
    private AudioServer? _server;
    private int _ownerThreadId;
    private bool _autoplayPending;

    [Export("音频片段")]
    public AudioClip? Clip
    {
        get
        {
            EnsureMainThread();
            return _clip;
        }
        set
        {
            EnsureMainThread();
            _clip = value;
        }
    }

    public string BusName
    {
        get
        {
            EnsureMainThread();
            return _busName;
        }
        set
        {
            EnsureMainThread();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("音频总线名称不能为空", nameof(value));
            }

            _busName = value.Trim();
        }
    }

    public float Volume
    {
        get
        {
            EnsureMainThread();
            return _volume;
        }
        set
        {
            EnsureMainThread();
            ValidateVolume(value, nameof(value));
            _volume = value;
        }
    }

    public bool Loop
    {
        get
        {
            EnsureMainThread();
            return _loop;
        }
        set
        {
            EnsureMainThread();
            _loop = value;
        }
    }

    public bool Autoplay
    {
        get
        {
            EnsureMainThread();
            return _autoplay;
        }
        set
        {
            EnsureMainThread();
            _autoplay = value;
        }
    }

    public bool IsPlaying
    {
        get
        {
            EnsureMainThread();
            return _playback.IsPlaying;
        }
    }

    public float MinDistance
    {
        get
        {
            EnsureMainThread();
            return _minDistance;
        }
        set
        {
            EnsureMainThread();
            ValidateMinDistance(value);
            _minDistance = value;
        }
    }

    public float MaxDistance
    {
        get
        {
            EnsureMainThread();
            return _maxDistance;
        }
        set
        {
            EnsureMainThread();
            ValidateMaxDistance(value);
            _maxDistance = value;
        }
    }

    public void AttachServer(AudioServer server)
    {
        EnsureMainThread();
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _playback.AttachServer(server);
        StartPendingAutoplay();
    }

    public void Play()
    {
        EnsureMainThread();
        var (volume, pan) = GetPlaybackParameters();
        _playback.Clip = Clip;
        _playback.BusName = BusName;
        _playback.Loop = Loop;
        _playback.PlayWithParameters(volume, pan);
        _autoplayPending = false;
    }

    public void Stop()
    {
        EnsureMainThread();
        _playback.Stop();
        _autoplayPending = false;
    }

    protected override void EnterTree()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    protected override void Ready()
    {
        if (Autoplay)
        {
            if (_server is null)
            {
                _autoplayPending = true;
            }
            else
            {
                Play();
            }
        }
    }

    protected override void Process(double delta)
    {
        EnsureMainThread();
        if (_server is null)
        {
            return;
        }

        if (!IsPlaying)
        {
            _playback.Stop();
            return;
        }

        var (volume, pan) = GetPlaybackParameters();
        _ = _playback.UpdateParameters(volume, pan);
    }

    protected override void ExitTree() => Stop();

    private (float Volume, float Pan) GetPlaybackParameters()
    {
        if (_server is null)
        {
            throw new InvalidOperationException("AudioPlayer3D 尚未附加 AudioServer");
        }

        if (!float.IsFinite(_volume) || _volume < 0f)
        {
            throw new InvalidOperationException("AudioPlayer3D 音量无效");
        }

        if (!float.IsFinite(_minDistance) || !float.IsFinite(_maxDistance) ||
            _minDistance < 0f || _maxDistance <= _minDistance)
        {
            throw new InvalidOperationException("AudioPlayer3D 距离范围无效");
        }

        var offset = GlobalPosition - _server.ListenerPosition;
        var distance = offset.Length();
        if (!float.IsFinite(distance))
        {
            throw new InvalidOperationException("AudioPlayer3D 位置必须是有限值");
        }

        var attenuation = distance <= _minDistance
            ? 1f
            : distance >= _maxDistance
                ? 0f
                : 1f - (distance - _minDistance) / (_maxDistance - _minDistance);
        var pan = distance <= float.Epsilon
            ? 0f
            : Math.Clamp(Vector3.Dot(offset / distance, _server.ListenerRight), -1f, 1f);
        return (_volume * attenuation, pan);
    }

    private static void ValidateVolume(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
        {
            throw new ArgumentOutOfRangeException(parameterName, "音量必须是有限非负数");
        }
    }

    private void StartPendingAutoplay()
    {
        if (!_autoplayPending || _server is null || Tree is null)
        {
            return;
        }

        Play();
    }

    private void ValidateMinDistance(float value)
    {
        if (!float.IsFinite(value) || value < 0f || value >= _maxDistance)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "最小距离必须是有限非负数且小于最大距离");
        }
    }

    private void ValidateMaxDistance(float value)
    {
        if (!float.IsFinite(value) || value <= _minDistance)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "最大距离必须是有限数且大于最小距离");
        }
    }

    private void EnsureMainThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == 0)
        {
            _ownerThreadId = current;
        }
        else if (_ownerThreadId != current)
        {
            throw new InvalidOperationException("AudioPlayer3D 只能在拥有它的主线程访问");
        }
    }
}
