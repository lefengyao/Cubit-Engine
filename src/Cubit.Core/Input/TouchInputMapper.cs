namespace Cubit.Core.Input;

/// <summary>触摸输入快照：左侧虚拟摇杆移动轴与右侧滑动视角增量。</summary>
public readonly record struct TouchInputSnapshot(
    float MoveX,
    float MoveY,
    float LookDeltaX,
    float LookDeltaY);

/// <summary>
/// 平台无关的双区触摸映射。
/// 左侧首次触点控制移动摇杆，右侧首次触点控制视角；多余触点不会抢占已有控制权。
/// </summary>
public sealed class TouchInputMapper
{
    private const int NoPointer = -1;

    private readonly float _moveZoneRatio;
    private readonly float _joystickRadiusRatio;
    private readonly float _lookSensitivity;
    private float _viewportWidth;
    private float _viewportHeight;
    private int _movePointerId = NoPointer;
    private int _lookPointerId = NoPointer;
    private float _moveAnchorX;
    private float _moveAnchorY;
    private float _moveX;
    private float _moveY;
    private float _lookLastX;
    private float _lookLastY;
    private float _lookDeltaX;
    private float _lookDeltaY;

    public TouchInputMapper(
        float viewportWidth,
        float viewportHeight,
        float moveZoneRatio = 0.5f,
        float joystickRadiusRatio = 0.22f,
        float lookSensitivity = 1f)
    {
        ValidateViewport(viewportWidth, viewportHeight);
        if (!float.IsFinite(moveZoneRatio) || moveZoneRatio <= 0f || moveZoneRatio > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(moveZoneRatio), "移动触摸区域比例必须在 (0, 1] 内。");
        }

        if (!float.IsFinite(joystickRadiusRatio) || joystickRadiusRatio <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(joystickRadiusRatio), "摇杆半径比例必须为有限正数。");
        }

        if (!float.IsFinite(lookSensitivity) || lookSensitivity < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(lookSensitivity), "视角灵敏度必须为有限非负数。");
        }

        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;
        _moveZoneRatio = moveZoneRatio;
        _joystickRadiusRatio = joystickRadiusRatio;
        _lookSensitivity = lookSensitivity;
    }

    /// <summary>更新触摸坐标对应的视口尺寸；尺寸变化会取消当前手势，避免旧坐标误驱动新表面。</summary>
    public void Resize(float viewportWidth, float viewportHeight)
    {
        ValidateViewport(viewportWidth, viewportHeight);
        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;
        Reset();
    }

    /// <summary>开始一个触点；由触点落点决定其控制移动或视角。</summary>
    public void PointerDown(int pointerId, float x, float y)
    {
        if (!AreFinite(x, y) || pointerId < 0 || pointerId == _movePointerId || pointerId == _lookPointerId)
        {
            return;
        }

        if (x <= _viewportWidth * _moveZoneRatio)
        {
            if (_movePointerId != NoPointer)
            {
                return;
            }

            _movePointerId = pointerId;
            _moveAnchorX = x;
            _moveAnchorY = y;
            _moveX = 0f;
            _moveY = 0f;
            return;
        }

        if (_lookPointerId != NoPointer)
        {
            return;
        }

        _lookPointerId = pointerId;
        _lookLastX = x;
        _lookLastY = y;
    }

    /// <summary>更新一个已接管触点的位置。</summary>
    public void PointerMove(int pointerId, float x, float y)
    {
        if (!AreFinite(x, y))
        {
            return;
        }

        if (pointerId == _movePointerId)
        {
            UpdateMoveAxis(x - _moveAnchorX, y - _moveAnchorY);
            return;
        }

        if (pointerId == _lookPointerId)
        {
            _lookDeltaX += (x - _lookLastX) * _lookSensitivity;
            _lookDeltaY += (y - _lookLastY) * _lookSensitivity;
            _lookLastX = x;
            _lookLastY = y;
        }
    }

    /// <summary>结束或取消一个触点。</summary>
    public void PointerUp(int pointerId)
    {
        if (pointerId == _movePointerId)
        {
            _movePointerId = NoPointer;
            _moveX = 0f;
            _moveY = 0f;
        }

        if (pointerId == _lookPointerId)
        {
            _lookPointerId = NoPointer;
        }
    }

    /// <summary>构建输入快照；视角增量为单帧量，读取后清零，移动轴则持续到摇杆触点抬起。</summary>
    public TouchInputSnapshot BuildSnapshot()
    {
        var snapshot = new TouchInputSnapshot(_moveX, _moveY, _lookDeltaX, _lookDeltaY);
        _lookDeltaX = 0f;
        _lookDeltaY = 0f;
        return snapshot;
    }

    /// <summary>取消所有活动手势与累计增量，供失焦、暂停和视口重建使用。</summary>
    public void Reset()
    {
        _movePointerId = NoPointer;
        _lookPointerId = NoPointer;
        _moveX = 0f;
        _moveY = 0f;
        _lookDeltaX = 0f;
        _lookDeltaY = 0f;
    }

    private void UpdateMoveAxis(float dx, float dy)
    {
        var radius = MathF.Min(_viewportWidth, _viewportHeight) * _joystickRadiusRatio;
        if (radius <= 0f)
        {
            _moveX = 0f;
            _moveY = 0f;
            return;
        }

        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length > radius)
        {
            var scale = radius / length;
            dx *= scale;
            dy *= scale;
        }

        _moveX = Math.Clamp(dx / radius, -1f, 1f);
        _moveY = Math.Clamp(dy / radius, -1f, 1f);
    }

    private static void ValidateViewport(float width, float height)
    {
        if (!AreFinite(width, height) || width <= 0f || height <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "触摸视口宽高必须为有限正数。");
        }
    }

    private static bool AreFinite(float x, float y) => float.IsFinite(x) && float.IsFinite(y);
}
