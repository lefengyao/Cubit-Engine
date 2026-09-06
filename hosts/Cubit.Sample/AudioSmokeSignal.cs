using Cubit.Audio;

namespace Cubit.Sample;

/// <summary>仅供平台设备验收使用的短促测试音；默认不会加入运行场景。</summary>
public static class AudioSmokeSignal
{
    /// <summary>Android Intent 中显式启用测试音的键，未传入时保持静音。</summary>
    public const string AndroidIntentExtra = "cubit.audio.smoke";

    private const int DurationFrames = AudioFormat.SampleRate / 4;
    private const float FrequencyHz = 440f;
    private const float Amplitude = 0.16f;

    /// <summary>创建 250 ms、48 kHz 双声道、低幅度的单次测试音。</summary>
    public static AudioClip CreateClip()
    {
        var samples = new float[DurationFrames * AudioFormat.Channels];
        for (var frame = 0; frame < DurationFrames; frame++)
        {
            var sample = Amplitude * MathF.Sin(2f * MathF.PI * FrequencyHz * frame / AudioFormat.SampleRate);
            var index = frame * AudioFormat.Channels;
            samples[index] = sample;
            samples[index + 1] = sample;
        }

        return new AudioClip(samples);
    }
}
