using Cubit.Core.Diagnostics;

namespace Cubit.Audio;

/// <summary>Audio 1.0 固定的内部采样格式。</summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
}

/// <summary>平台输出后端；设备线程只负责提交已混合的 PCM 缓冲。</summary>
public interface IAudioOutputBackend : IDisposable
{
    string Id { get; }

    /// <summary>Submit a complete stereo float PCM block; consume or copy the span before returning.</summary>
    void Submit(ReadOnlySpan<float> samples);
}

/// <summary>主线程创建的播放句柄。</summary>
public readonly record struct AudioPlaybackHandle(int Id)
{
    public bool IsValid => Id > 0;
}

/// <summary>由 AudioServer 主线程拥有的混音总线。</summary>
public sealed class AudioBus
{
    internal AudioBus(string name, float volume)
    {
        Name = name;
        Volume = volume;
    }

    public string Name { get; }

    public float Volume { get; internal set; }

    public bool Muted { get; internal set; }
}

/// <summary>Audio 服务的最小状态快照，供宿主记录诊断而不依赖全局服务。</summary>
public sealed record AudioServerStatus(
    bool HasOutputBackend,
    int ActiveVoiceCount,
    IReadOnlyList<EngineDiagnostic> Diagnostics);

/// <summary>音频总线的不可变状态快照。</summary>
public sealed record AudioBusStatus(string Name, float Volume, bool Muted);
