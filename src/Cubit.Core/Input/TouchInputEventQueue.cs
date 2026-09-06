using System.Collections.Concurrent;

namespace Cubit.Core.Input;

/// <summary>
/// 平台 UI 线程到游戏线程的触摸事件队列。
/// 生产者只写入值类型事件；唯一游戏线程在帧开始时排空并驱动触摸映射器。
/// </summary>
public sealed class TouchInputEventQueue
{
    private readonly TouchInputMapper _mapper;
    private readonly ConcurrentQueue<TouchInputEvent> _pendingEvents = new();

    public TouchInputEventQueue(
        float viewportWidth,
        float viewportHeight,
        float moveZoneRatio = 0.5f,
        float joystickRadiusRatio = 0.22f,
        float lookSensitivity = 1f)
    {
        _mapper = new TouchInputMapper(
            viewportWidth,
            viewportHeight,
            moveZoneRatio,
            joystickRadiusRatio,
            lookSensitivity);
    }

    /// <summary>由平台 UI 线程排入一个触点开始事件。</summary>
    public void EnqueuePointerDown(int pointerId, float x, float y) =>
        _pendingEvents.Enqueue(new TouchInputEvent(TouchInputEventKind.Down, pointerId, x, y));

    /// <summary>由平台 UI 线程排入一个触点移动事件。</summary>
    public void EnqueuePointerMove(int pointerId, float x, float y) =>
        _pendingEvents.Enqueue(new TouchInputEvent(TouchInputEventKind.Move, pointerId, x, y));

    /// <summary>由平台 UI 线程排入一个触点结束事件。</summary>
    public void EnqueuePointerUp(int pointerId) =>
        _pendingEvents.Enqueue(new TouchInputEvent(TouchInputEventKind.Up, pointerId, 0f, 0f));

    /// <summary>由平台 UI 线程排入取消事件，防止焦点变化后保留卡住的手势。</summary>
    public void EnqueueCancel() =>
        _pendingEvents.Enqueue(new TouchInputEvent(TouchInputEventKind.Cancel, 0, 0f, 0f));

    /// <summary>仅由游戏线程在表面尺寸变化时调用；会取消旧视口上的活动手势。</summary>
    public void Resize(float viewportWidth, float viewportHeight) => _mapper.Resize(viewportWidth, viewportHeight);

    /// <summary>仅由游戏线程调用，按事件到达顺序排空并生成本帧快照。</summary>
    public TouchInputSnapshot BuildSnapshot()
    {
        while (_pendingEvents.TryDequeue(out var touchEvent))
        {
            switch (touchEvent.Kind)
            {
                case TouchInputEventKind.Down:
                    _mapper.PointerDown(touchEvent.PointerId, touchEvent.X, touchEvent.Y);
                    break;
                case TouchInputEventKind.Move:
                    _mapper.PointerMove(touchEvent.PointerId, touchEvent.X, touchEvent.Y);
                    break;
                case TouchInputEventKind.Up:
                    _mapper.PointerUp(touchEvent.PointerId);
                    break;
                case TouchInputEventKind.Cancel:
                    _mapper.Reset();
                    break;
                default:
                    throw new InvalidOperationException($"未知触摸事件类型: {touchEvent.Kind}");
            }
        }

        return _mapper.BuildSnapshot();
    }

    /// <summary>仅由游戏线程取消当前手势，并丢弃尚未消费的平台事件。</summary>
    public void Reset()
    {
        while (_pendingEvents.TryDequeue(out _))
        {
        }

        _mapper.Reset();
    }

    private readonly record struct TouchInputEvent(TouchInputEventKind Kind, int PointerId, float X, float Y);

    private enum TouchInputEventKind
    {
        Down,
        Move,
        Up,
        Cancel,
    }
}
