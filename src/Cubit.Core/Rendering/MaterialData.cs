namespace Cubit.Core.Rendering;

/// <summary>通用材质数据；纹理通过 RID 引用，由 RenderingServer 校验生命周期。</summary>
public sealed record MaterialData
{
    public RID AlbedoTexture { get; }

    public MaterialBlendMode BlendMode { get; }

    public MaterialData(RID albedoTexture)
        : this(albedoTexture, MaterialBlendMode.Opaque)
    {
    }

    public MaterialData(RID albedoTexture, MaterialBlendMode blendMode)
    {
        if (!albedoTexture.IsValid)
        {
            throw new ArgumentException("材质必须引用有效纹理 RID", nameof(albedoTexture));
        }

        if (!Enum.IsDefined(blendMode))
        {
            throw new ArgumentOutOfRangeException(nameof(blendMode));
        }

        AlbedoTexture = albedoTexture;
        BlendMode = blendMode;
    }
}
