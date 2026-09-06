using System.Numerics;
using Cubit.Core.Camera;
using Cubit.Core.Scene;

namespace Cubit.Core.Scene;

/// <summary>相机节点（借鉴 Godot Camera3D）：持有飞行相机，向渲染服务提供视图矩阵。</summary>
public sealed class Camera3D : Node
{
    public Camera3D() => Name = nameof(Camera3D);

    public FlyCamera Camera { get; } = new(new Vector3(24f, 34f, 24f), MathF.PI * 0.25f, -0.35f);

    public Matrix4x4 ViewMatrix => Camera.ViewMatrix;

    /// <summary>按插值系数取视图矩阵（模拟 tick 与渲染帧之间平滑）。</summary>
    public Matrix4x4 GetViewMatrix(float interpolationAlpha) => Camera.InterpolatedViewMatrix(interpolationAlpha);

    public Matrix4x4 ProjectionMatrix(float aspect) => Camera.ProjectionMatrix(aspect);
}
