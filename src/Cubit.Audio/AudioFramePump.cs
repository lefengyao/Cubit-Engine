namespace Cubit.Audio;

/// <summary>宿主每帧调用的 PCM 提交器；按真实 delta 累积完整采样帧，不创建设备线程。</summary>
public sealed class AudioFramePump
{
    private const int DefaultFramesPerBlock = 1024;
    private const double MaximumDeltaSeconds = 1d;

    private readonly AudioServer _server;
    private readonly float[] _buffer;
    private readonly int _ownerThreadId;
    private double _fractionalFrames;

    /// <summary>创建固定大小的主线程 PCM 输出缓冲。</summary>
    public AudioFramePump(AudioServer server, int framesPerBlock = DefaultFramesPerBlock)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        if (framesPerBlock <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerBlock));
        }

        _buffer = new float[checked(framesPerBlock * AudioFormat.Channels)];
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>固定 PCM 块的最大采样帧数。</summary>
    public int FramesPerBlock => _buffer.Length / AudioFormat.Channels;

    /// <summary>
    /// 将游戏帧时长转换为 48 kHz 完整 PCM 采样帧并提交。小于一个采样帧的余数会留给下一帧。
    /// 宿主应在场景更新后调用，使当帧播放命令进入同一次输出。
    /// </summary>
    public void RenderFrame(double delta)
    {
        EnsureOwnerThread();
        if (!double.IsFinite(delta) || delta < 0d || delta > MaximumDeltaSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "音频帧时长必须是 0 到 1 秒的有限值");
        }

        var accumulatedFrames = _fractionalFrames + delta * AudioFormat.SampleRate;
        var pendingFrames = (int)Math.Floor(accumulatedFrames);
        _fractionalFrames = accumulatedFrames - pendingFrames;

        while (pendingFrames > 0)
        {
            var frameCount = Math.Min(pendingFrames, FramesPerBlock);
            _server.Render(_buffer.AsSpan(0, frameCount * AudioFormat.Channels));
            pendingFrames -= frameCount;
        }
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("AudioFramePump 只能在创建线程提交 PCM");
        }
    }
}
