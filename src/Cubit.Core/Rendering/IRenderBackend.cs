using System.Numerics;

namespace Cubit.Core.Rendering;

/// <summary>平台渲染后端契约；RenderingServer 通过 RID 提交通用网格和相机状态。</summary>
public interface IRenderBackend
{
    (int Width, int Height) FramebufferSize { get; }

    void UpdateCamera(in Matrix4x4 view, in Matrix4x4 projection);

    void SetTexture(RID rid, TextureData texture);

    void RemoveTexture(RID rid);

    void SetMaterial(RID rid, MaterialData material);

    void RemoveMaterial(RID rid);

    void SetMesh(RID rid, MeshData mesh);

    void UpdateMeshTransform(RID rid, in Matrix4x4 transform);

    void UpdateMeshModulation(RID rid, in Vector4 modulation);

    /// <summary>设置通用场景环境调制；不携带昼夜、天气或项目语义。</summary>
    void SetEnvironmentModulation(in Vector4 modulation);

    void RemoveMesh(RID rid);
}
