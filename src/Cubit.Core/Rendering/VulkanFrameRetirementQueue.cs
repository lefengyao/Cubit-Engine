namespace Cubit.Core.Rendering;

/// <summary>按在途帧数延迟释放 Vulkan 资源，避免 GPU 仍引用已销毁的句柄。</summary>
internal sealed class VulkanFrameRetirementQueue<T>
{
    private readonly int _completedFrameDelay;
    private readonly List<Entry> _entries = [];

    private readonly record struct Entry(T Resource, long RetiredAtFrame);

    public VulkanFrameRetirementQueue(int completedFrameDelay)
    {
        if (completedFrameDelay < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(completedFrameDelay));
        }

        _completedFrameDelay = completedFrameDelay;
    }

    public int Count => _entries.Count;

    public void Retire(T resource, long frame)
    {
        if (frame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        _entries.Add(new Entry(resource, frame));
    }

    public void DrainCompleted(long completedFrame, Action<T> destroy)
    {
        ArgumentNullException.ThrowIfNull(destroy);
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            if (completedFrame - _entries[index].RetiredAtFrame < _completedFrameDelay)
            {
                continue;
            }

            destroy(_entries[index].Resource);
            _entries.RemoveAt(index);
        }
    }

    public void DrainAll(Action<T> destroy)
    {
        ArgumentNullException.ThrowIfNull(destroy);
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            destroy(_entries[index].Resource);
        }

        _entries.Clear();
    }
}
