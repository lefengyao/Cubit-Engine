using System.Numerics;
using Cubit.Core.Input;

namespace Cubit.Core.Camera;

/// <summary>
/// 飞行相机：俯仰/偏航 + 视角矩阵（右手系，适配 Vulkan 裁剪空间）。
/// 支持渲染插值：BeginPhysicsStep 在每次模拟 tick 前调用，随后可用 alpha 插值视图矩阵。
/// </summary>
public sealed class FlyCamera
{
    public Vector3 Position;
    public float Yaw;
    public float Pitch;

    public Vector3 PreviousPosition;
    public float PreviousYaw;
    public float PreviousPitch;

    public float Speed = 14f;
    public float Sensitivity = 0.0022f;
    public const float FovY = 70f * MathF.PI / 180f;
    public const float Near = 0.1f;
    public const float Far = 600f;

    private bool _hasPrevious;

    public FlyCamera(Vector3 position, float yaw, float pitch)
    {
        Position = position;
        Yaw = yaw;
        Pitch = pitch;
    }

    public Vector3 Forward => new(
        MathF.Cos(Pitch) * MathF.Sin(Yaw),
        MathF.Sin(Pitch),
        MathF.Cos(Pitch) * MathF.Cos(Yaw));

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public Matrix4x4 ProjectionMatrix(float aspect)
    {
        var f = 1f / MathF.Tan(FovY * 0.5f);
        // 行主序存储；上传后 GLSL 按列主序解释即为 Vulkan 正确投影矩阵。
        // 注意：-1 在列 2 的 w 分量（即行主序的 m34），near*far 项在列 3 的 z 分量（m43）。
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, Far / (Near - Far), -1,
            0, 0, Near * Far / (Near - Far), 0);
    }

    // ---- 渲染插值（模拟 tick 20Hz 独立于渲染帧，位置/朝向按 alpha 插值避免卡顿）----

    /// <summary>模拟 tick 开始时调用：把当前变换保存为上一帧，供渲染插值。</summary>
    public void BeginPhysicsStep()
    {
        PreviousPosition = Position;
        PreviousYaw = Yaw;
        PreviousPitch = Pitch;
        _hasPrevious = true;
    }

    public Vector3 InterpolatedPosition(float alpha) =>
        _hasPrevious ? Vector3.Lerp(PreviousPosition, Position, alpha) : Position;

    public float InterpolatedYaw(float alpha) =>
        _hasPrevious ? PreviousYaw + ShortestAngleDelta(Yaw - PreviousYaw) * alpha : Yaw;

    public float InterpolatedPitch(float alpha) =>
        _hasPrevious ? PreviousPitch + (Pitch - PreviousPitch) * alpha : Pitch;

    public Vector3 InterpolatedForward(float alpha)
    {
        var yaw = InterpolatedYaw(alpha);
        var pitch = InterpolatedPitch(alpha);
        return new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw));
    }

    public Matrix4x4 InterpolatedViewMatrix(float alpha) =>
        Matrix4x4.CreateLookAt(
            InterpolatedPosition(alpha),
            InterpolatedPosition(alpha) + InterpolatedForward(alpha),
            Vector3.UnitY);

    /// <summary>应用鼠标视角（每帧一次，渲染驱动）。</summary>
    public void ApplyLook(in GameInput input)
    {
        Yaw -= input.MouseDeltaX * Sensitivity;
        Pitch -= input.MouseDeltaY * Sensitivity;
        Pitch = Math.Clamp(Pitch, -1.55f, 1.55f);
    }

    /// <summary>应用按键移动（固定模拟步驱动）。</summary>
    public void ApplyMovement(in GameInput input, double dt)
    {
        var forwardFlat = new Vector3(MathF.Sin(Yaw), 0, MathF.Cos(Yaw));
        // CreateLookAt 的屏幕右侧是 Forward × Up；反向会让 A/D 的实际位移颠倒。
        var right = new Vector3(-MathF.Cos(Yaw), 0, MathF.Sin(Yaw));

        var move = Vector3.Zero;
        if (input.Forward) move += forwardFlat;
        if (input.Back) move -= forwardFlat;
        if (input.Right) move += right;
        if (input.Left) move -= right;
        if (input.Up) move += Vector3.UnitY;
        if (input.Down) move -= Vector3.UnitY;

        if (move.LengthSquared() > 0)
        {
            move = Vector3.Normalize(move);
        }

        Position += move * Speed * (float)dt;
    }

    public void ApplyInput(in GameInput input, double dt)
    {
        ApplyLook(input);
        ApplyMovement(input, dt);
    }

    private static float ShortestAngleDelta(float delta)
    {
        while (delta > MathF.PI)
        {
            delta -= 2 * MathF.PI;
        }

        while (delta < -MathF.PI)
        {
            delta += 2 * MathF.PI;
        }

        return delta;
    }
}
