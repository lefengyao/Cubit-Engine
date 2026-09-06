using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json.Serialization;
using Cubit.Core.Scene;
using NVorbis;

namespace Cubit.Audio;

/// <summary>完整载入的 48 kHz 双声道 float PCM 资源。</summary>
public sealed class AudioClip : Resource
{
    private float[] _samples = [];
    private IReadOnlyList<float> _readOnlySamples = Array.Empty<float>();

    [JsonConstructor]
    private AudioClip()
    {
        _samples = [];
        _readOnlySamples = Array.AsReadOnly(_samples);
    }

    public AudioClip(float[] samples, int sampleRate = AudioFormat.SampleRate, int channels = AudioFormat.Channels)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate != AudioFormat.SampleRate || channels != AudioFormat.Channels)
        {
            throw new ArgumentException("AudioClip 只接受 48 kHz 双声道 PCM");
        }

        if (samples.Length == 0 || samples.Length % AudioFormat.Channels != 0)
        {
            throw new ArgumentException("AudioClip 必须包含完整的双声道采样帧", nameof(samples));
        }

        for (var index = 0; index < samples.Length; index++)
        {
            if (!float.IsFinite(samples[index]))
            {
                throw new ArgumentException("AudioClip 采样值必须是有限数", nameof(samples));
            }
        }

        AssignSamples(samples);
    }

    public int SampleRate => AudioFormat.SampleRate;

    public int Channels => AudioFormat.Channels;

    public int FrameCount => _samples.Length / AudioFormat.Channels;

    [JsonIgnore]
    public IReadOnlyList<float> Samples => _readOnlySamples;

    /// <summary>资源 JSON 使用的完整交错 PCM 样本；赋值时仍经过运行时格式校验并复制。</summary>
    [JsonInclude]
    public float[] SerializedSamples
    {
        get => _samples.ToArray();
        private set => AssignSamples(value);
    }

    internal ReadOnlySpan<float> SampleSpan => _samples;

    private void AssignSamples(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0 || samples.Length % AudioFormat.Channels != 0)
        {
            throw new ArgumentException("AudioClip 必须包含完整的双声道采样帧", nameof(samples));
        }

        for (var index = 0; index < samples.Length; index++)
        {
            if (!float.IsFinite(samples[index]))
            {
                throw new ArgumentException("AudioClip 采样值必须是有限数", nameof(samples));
            }
        }

        _samples = samples.ToArray();
        _readOnlySamples = Array.AsReadOnly(_samples);
    }

    /// <summary>
    /// 从完整载入的 WAV 数据创建音频片段。1.0 只接受与内部格式一致的
    /// 48 kHz、双声道、16 位 PCM，避免导入阶段隐式重采样或声道混合。
    /// </summary>
    public static AudioClip FromWav(byte[] wavData)
    {
        ArgumentNullException.ThrowIfNull(wavData);
        var source = wavData.AsSpan();
        if (source.Length < 12 || !source[..4].SequenceEqual("RIFF"u8) || !source.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new ArgumentException("WAV 文件必须是有效的 RIFF/WAVE 数据", nameof(wavData));
        }

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, sizeof(uint)));
        var riffEnd = checked((long)declaredSize + 8L);
        if (declaredSize < 4 || riffEnd > source.Length)
        {
            throw new ArgumentException("WAV RIFF 长度无效", nameof(wavData));
        }

        var formatFound = false;
        var dataFound = false;
        ushort formatCode = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        uint byteRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        ReadOnlySpan<byte> pcmData = default;
        var offset = 12;
        while (offset < riffEnd)
        {
            if (offset + 8L > riffEnd)
            {
                throw new ArgumentException("WAV 区块头不完整", nameof(wavData));
            }

            var chunkId = source.Slice(offset, 4);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset + 4, sizeof(uint)));
            var chunkDataOffset = offset + 8;
            var chunkEnd = checked((long)chunkDataOffset + chunkLength);
            if (chunkEnd > riffEnd)
            {
                throw new ArgumentException("WAV 区块长度超出 RIFF 边界", nameof(wavData));
            }

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (formatFound || chunkLength < 16)
                {
                    throw new ArgumentException("WAV fmt 区块无效", nameof(wavData));
                }

                formatFound = true;
                var format = source.Slice(chunkDataOffset, checked((int)chunkLength));
                formatCode = BinaryPrimitives.ReadUInt16LittleEndian(format);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format.Slice(4));
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format.Slice(8));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(12));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(14));
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                if (dataFound)
                {
                    throw new ArgumentException("WAV 不支持多个 data 区块", nameof(wavData));
                }

                dataFound = true;
                pcmData = source.Slice(chunkDataOffset, checked((int)chunkLength));
            }

            offset = checked((int)chunkEnd);
            if ((chunkLength & 1) != 0)
            {
                if (offset >= riffEnd)
                {
                    throw new ArgumentException("WAV 区块缺少对齐字节", nameof(wavData));
                }

                offset++;
            }
        }

        if (!formatFound || !dataFound || formatCode != 1 || channels != AudioFormat.Channels ||
            sampleRate != AudioFormat.SampleRate || bitsPerSample != 16 || blockAlign != 4 || byteRate != 192_000 ||
            pcmData.Length == 0 || pcmData.Length % blockAlign != 0)
        {
            throw new ArgumentException("WAV 必须是 48 kHz 双声道 16 位 PCM", nameof(wavData));
        }

        var samples = new float[pcmData.Length / sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(pcmData.Slice(index * sizeof(short))) / 32768f;
        }

        return new AudioClip(samples);
    }

    /// <summary>
    /// 从完整载入的 OGG/Vorbis 数据创建音频片段。解码与重采样仅在资源加载阶段执行，
    /// 播放回调始终只接收 48 kHz 双声道 float PCM。
    /// </summary>
    public static AudioClip FromOggVorbis(byte[] oggData)
    {
        ArgumentNullException.ThrowIfNull(oggData);
        if (oggData.Length == 0)
        {
            throw new ArgumentException("OGG/Vorbis 数据不能为空", nameof(oggData));
        }

        try
        {
            using var stream = new MemoryStream(oggData, writable: false);
            using var reader = new VorbisReader(stream, closeOnDispose: false);
            var sourceChannels = reader.Channels;
            var sourceSampleRate = reader.SampleRate;
            if (sourceChannels != AudioFormat.Channels)
            {
                throw new ArgumentException("OGG/Vorbis 必须是双声道", nameof(oggData));
            }

            if (sourceSampleRate <= 0)
            {
                throw new ArgumentException("OGG/Vorbis 采样率无效", nameof(oggData));
            }

            var decodedSamples = new ArrayBufferWriter<float>();
            var readBuffer = new float[8_192];
            while (true)
            {
                var read = reader.ReadSamples(readBuffer, 0, readBuffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (read < 0 || read % AudioFormat.Channels != 0 || reader.Channels != sourceChannels ||
                    reader.SampleRate != sourceSampleRate)
                {
                    throw new ArgumentException("OGG/Vorbis 包含不支持的格式变更", nameof(oggData));
                }

                readBuffer.AsSpan(0, read).CopyTo(decodedSamples.GetSpan(read));
                decodedSamples.Advance(read);
            }

            if (decodedSamples.WrittenCount == 0)
            {
                throw new ArgumentException("OGG/Vorbis 不包含音频采样", nameof(oggData));
            }

            return new AudioClip(ResampleStereo(decodedSamples.WrittenSpan, sourceSampleRate));
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ArgumentException("OGG/Vorbis 数据无效或已损坏", nameof(oggData), exception);
        }
    }

    private static float[] ResampleStereo(ReadOnlySpan<float> sourceSamples, int sourceSampleRate)
    {
        if (sourceSamples.Length == 0 || sourceSamples.Length % AudioFormat.Channels != 0)
        {
            throw new ArgumentException("源 PCM 必须包含完整的双声道采样帧", nameof(sourceSamples));
        }

        if (sourceSampleRate == AudioFormat.SampleRate)
        {
            return sourceSamples.ToArray();
        }

        var sourceFrameCount = sourceSamples.Length / AudioFormat.Channels;
        var targetFrameCount = checked((int)Math.Ceiling(
            sourceFrameCount * (double)AudioFormat.SampleRate / sourceSampleRate));
        var targetSamples = new float[checked(targetFrameCount * AudioFormat.Channels)];
        var sourceFramesPerTargetFrame = sourceSampleRate / (double)AudioFormat.SampleRate;
        for (var targetFrame = 0; targetFrame < targetFrameCount; targetFrame++)
        {
            var sourcePosition = targetFrame * sourceFramesPerTargetFrame;
            var sourceFrame = Math.Min((int)sourcePosition, sourceFrameCount - 1);
            var nextSourceFrame = Math.Min(sourceFrame + 1, sourceFrameCount - 1);
            var interpolation = (float)(sourcePosition - sourceFrame);
            for (var channel = 0; channel < AudioFormat.Channels; channel++)
            {
                var first = sourceSamples[sourceFrame * AudioFormat.Channels + channel];
                var second = sourceSamples[nextSourceFrame * AudioFormat.Channels + channel];
                targetSamples[targetFrame * AudioFormat.Channels + channel] = first + ((second - first) * interpolation);
            }
        }

        return targetSamples;
    }

}
