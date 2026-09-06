using Android.Content;
using Android.Graphics;
using Android.Views;

namespace Cubit.Sample.Android.Platform;

/// <summary>
/// 移动端操控层的固定布局。事件只由 Activity 分发层路由，避免 SDL 子视图间的触摸抢占。
/// </summary>
internal static class MobileTouchLayout
{
    public static RectF PrimaryBounds(int width, int height, float density) => new(
        width - Dp(116f, density),
        height - Dp(242f, density),
        width - Dp(28f, density),
        height - Dp(154f, density));

    public static RectF SecondaryBounds(int width, int height, float density) => new(
        width - Dp(116f, density),
        height - Dp(138f, density),
        width - Dp(28f, density),
        height - Dp(50f, density));

    public static MobileTouchAction? HitTestAction(float x, float y, int width, int height, float density)
    {
        if (PrimaryBounds(width, height, density).Contains(x, y))
        {
            return MobileTouchAction.Primary;
        }

        return SecondaryBounds(width, height, density).Contains(x, y) ? MobileTouchAction.Secondary : null;
    }

    private static float Dp(float value, float density) => value * density;
}

internal enum MobileTouchAction
{
    Primary,
    Secondary,
}

/// <summary>覆盖 SDL Surface 的移动端操控绘制层；触摸由 Activity 在子视图分发前统一处理。</summary>
internal sealed class MobileTouchOverlay : View
{
    private const int NoPointer = -1;

    private readonly Action<int, int> _onViewportChanged;
    private readonly Paint _strokePaint = new(PaintFlags.AntiAlias) { StrokeWidth = 3f };
    private readonly Paint _fillPaint = new(PaintFlags.AntiAlias);
    private readonly float _density;
    private int _movePointerId = NoPointer;
    private float _moveAnchorX;
    private float _moveAnchorY;
    private float _moveKnobX;
    private float _moveKnobY;
    private bool _primaryPressed;
    private bool _secondaryPressed;

    public MobileTouchOverlay(Context context, Action<int, int> onViewportChanged)
        : base(context)
    {
        _onViewportChanged = onViewportChanged;
        _density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        SetWillNotDraw(false);
        Clickable = false;
        Focusable = false;
    }

    /// <summary>将实际覆盖层尺寸发布给游戏线程，在其帧边界更新通用触摸映射器。</summary>
    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (w > 0 && h > 0)
        {
            _onViewportChanged(w, h);
        }
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        DrawJoystick(canvas);
        DrawActionButton(canvas, MobileTouchLayout.PrimaryBounds(Width, Height, _density), MobileTouchAction.Primary, _primaryPressed);
        DrawActionButton(canvas, MobileTouchLayout.SecondaryBounds(Width, Height, _density), MobileTouchAction.Secondary, _secondaryPressed);
    }

    /// <summary>开始左侧移动手势；覆盖层复用 Core 的半屏规则，但只保存视觉状态。</summary>
    internal void BeginMoveGesture(int pointerId, float x, float y)
    {
        if (pointerId < 0 || x > Width * 0.5f || _movePointerId != NoPointer)
        {
            return;
        }

        _movePointerId = pointerId;
        _moveAnchorX = x;
        _moveAnchorY = y;
        _moveKnobX = x;
        _moveKnobY = y;
        PostInvalidateOnAnimation();
    }

    /// <summary>更新当前摇杆帽的位置，视觉半径与 Core 触摸映射器保持一致。</summary>
    internal void UpdateMoveGesture(int pointerId, float x, float y)
    {
        if (pointerId != _movePointerId)
        {
            return;
        }

        var radius = JoystickRadius;
        var dx = x - _moveAnchorX;
        var dy = y - _moveAnchorY;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length > radius && length > 0f)
        {
            var scale = radius / length;
            dx *= scale;
            dy *= scale;
        }

        _moveKnobX = _moveAnchorX + dx;
        _moveKnobY = _moveAnchorY + dy;
        PostInvalidateOnAnimation();
    }

    /// <summary>手势结束时将摇杆恢复到默认中心。</summary>
    internal void EndMoveGesture(int pointerId)
    {
        if (pointerId != _movePointerId)
        {
            return;
        }

        ResetMoveGesture();
        PostInvalidateOnAnimation();
    }

    /// <summary>Activity 收到取消事件后清除摇杆视觉状态。</summary>
    internal void CancelMoveGesture()
    {
        ResetMoveGesture();
        PostInvalidateOnAnimation();
    }

    /// <summary>由 Activity 的按钮指针集合推送按住态，绘制层不读取或消费玩法输入。</summary>
    internal void SetActionState(bool primaryPressed, bool secondaryPressed)
    {
        _primaryPressed = primaryPressed;
        _secondaryPressed = secondaryPressed;
        PostInvalidateOnAnimation();
    }

    private float JoystickRadius => MathF.Min(Width, Height) * 0.22f;

    private void DrawJoystick(Canvas canvas)
    {
        var active = _movePointerId != NoPointer;
        var centerX = active ? _moveAnchorX : Dp(112f);
        var centerY = active ? _moveAnchorY : Height - Dp(126f);
        var knobX = active ? _moveKnobX : centerX;
        var knobY = active ? _moveKnobY : centerY;
        var radius = JoystickRadius;

        _fillPaint.Color = Color.Argb(active ? 86 : 54, 13, 20, 31);
        canvas.DrawCircle(centerX, centerY, radius, _fillPaint);
        _strokePaint.Color = Color.Argb(active ? 238 : 170, 205, 222, 235);
        _strokePaint.SetStyle(Paint.Style.Stroke);
        canvas.DrawCircle(centerX, centerY, radius, _strokePaint);

        _fillPaint.Color = Color.Argb(active ? 188 : 92, 89, 184, 146);
        canvas.DrawCircle(knobX, knobY, radius * 0.38f, _fillPaint);
        _strokePaint.Color = Color.Argb(active ? 255 : 190, 226, 255, 235);
        canvas.DrawCircle(knobX, knobY, radius * 0.38f, _strokePaint);
    }

    private void DrawActionButton(Canvas canvas, RectF bounds, MobileTouchAction action, bool pressed)
    {
        _fillPaint.Color = pressed ? Color.Argb(192, 103, 188, 123) : Color.Argb(118, 16, 27, 39);
        canvas.DrawRoundRect(bounds, Dp(18f), Dp(18f), _fillPaint);

        _strokePaint.Color = pressed ? Color.Argb(255, 226, 255, 235) : Color.Argb(190, 205, 222, 235);
        _strokePaint.SetStyle(Paint.Style.Stroke);
        canvas.DrawRoundRect(bounds, Dp(18f), Dp(18f), _strokePaint);

        var centerX = bounds.CenterX();
        var centerY = bounds.CenterY();
        var iconSize = Dp(18f);
        canvas.DrawLine(centerX - iconSize, centerY, centerX + iconSize, centerY, _strokePaint);
        if (action == MobileTouchAction.Secondary)
        {
            canvas.DrawLine(centerX, centerY - iconSize, centerX, centerY + iconSize, _strokePaint);
        }
        else
        {
            canvas.DrawLine(centerX - iconSize, centerY - iconSize, centerX + iconSize, centerY + iconSize, _strokePaint);
            canvas.DrawLine(centerX - iconSize, centerY + iconSize, centerX + iconSize, centerY - iconSize, _strokePaint);
        }
    }

    private void ResetMoveGesture()
    {
        _movePointerId = NoPointer;
        _moveAnchorX = 0f;
        _moveAnchorY = 0f;
        _moveKnobX = 0f;
        _moveKnobY = 0f;
    }

    private float Dp(float value) => value * _density;
}
