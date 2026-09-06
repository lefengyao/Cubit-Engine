using System.Numerics;

namespace Cubit.Core.Rendering;

/// <summary>
/// 渲染服务器（借鉴 Godot RenderingServer）：节点是前端，服务器是后端。
/// 服务器用 RID 句柄管理 GPU 资源；本类包装通用 IRenderBackend 后端，
/// 并实现"2 帧安全期延迟销毁"与句柄生命周期管理。
/// </summary>
public sealed class RenderingServer : IDisposable
{
    private readonly IRenderBackend _backend;
    private readonly Dictionary<ulong, TextureData> _textures = [];
    private readonly Dictionary<ulong, MaterialData> _materials = [];
    private readonly Dictionary<ulong, MeshData> _meshes = [];
    private readonly Queue<RetireEntry> _retireQueue = new();
    private readonly HashSet<ulong> _pendingTextureRetirements = [];
    private readonly HashSet<ulong> _pendingMaterialRetirements = [];
    private readonly HashSet<ulong> _pendingRetirements = [];
    private long _nextRid;
    private int _frame;

    private enum ResourceKind { Texture, Material, Mesh }

    private readonly record struct RetireEntry(ResourceKind Kind, ulong Rid, int RetireFrame);

    public RenderingServer(IRenderBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    /// <summary>当前存活（未到期销毁）的网格句柄数。</summary>
    public int MeshCount => _meshes.Count;

    public int TextureCount => _textures.Count;

    public int MaterialCount => _materials.Count;

    /// <summary>创建通用纹理并立即提交后端，返回服务器分配的 RID。</summary>
    public RID CreateTexture(TextureData texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var rid = AllocateRid();
        _textures.Add(rid.Value, texture);
        try
        {
            _backend.SetTexture(rid, texture);
            return rid;
        }
        catch
        {
            _textures.Remove(rid.Value);
            throw;
        }
    }

    /// <summary>请求销毁纹理；引用它的材质存在时拒绝操作。</summary>
    public void DestroyTexture(RID rid)
    {
        if (!rid.IsValid || !_textures.ContainsKey(rid.Value))
        {
            return;
        }

        if (_materials.Any(pair =>
                pair.Value.AlbedoTexture == rid &&
                !_pendingMaterialRetirements.Contains(pair.Key)))
        {
            throw new InvalidOperationException($"纹理仍被材质引用，不能销毁: {rid}");
        }

        if (_pendingTextureRetirements.Add(rid.Value))
        {
            _retireQueue.Enqueue(new RetireEntry(ResourceKind.Texture, rid.Value, _frame + 2));
        }
    }

    /// <summary>创建引用已存在纹理的通用材质。</summary>
    public RID CreateMaterial(MaterialData material)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!_textures.ContainsKey(material.AlbedoTexture.Value) ||
            _pendingTextureRetirements.Contains(material.AlbedoTexture.Value))
        {
            throw new InvalidOperationException($"材质引用了未知或待销毁纹理: {material.AlbedoTexture}");
        }

        var rid = AllocateRid();
        _materials.Add(rid.Value, material);
        try
        {
            _backend.SetMaterial(rid, material);
            return rid;
        }
        catch
        {
            _materials.Remove(rid.Value);
            throw;
        }
    }

    /// <summary>请求销毁材质；引用它的网格存在时拒绝操作。</summary>
    public void DestroyMaterial(RID rid)
    {
        if (!rid.IsValid || !_materials.ContainsKey(rid.Value))
        {
            return;
        }

        if (_meshes.Any(pair =>
                pair.Value.Material == rid &&
                !_pendingRetirements.Contains(pair.Key)))
        {
            throw new InvalidOperationException($"材质仍被网格引用，不能销毁: {rid}");
        }

        if (_pendingMaterialRetirements.Add(rid.Value))
        {
            _retireQueue.Enqueue(new RetireEntry(ResourceKind.Material, rid.Value, _frame + 2));
        }
    }

    /// <summary>创建通用网格并立即提交后端，返回服务器分配的 RID。</summary>
    public RID CreateMesh(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ValidateMeshMaterial(mesh);

        var rid = AllocateRid();
        _meshes.Add(rid.Value, mesh);
        try
        {
            _backend.SetMesh(rid, mesh);
            return rid;
        }
        catch
        {
            _meshes.Remove(rid.Value);
            throw;
        }
    }

    /// <summary>原位替换已有网格的通用几何数据，RID 保持不变。</summary>
    public void ReplaceMesh(RID rid, MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!rid.IsValid || !_meshes.ContainsKey(rid.Value) || _pendingRetirements.Contains(rid.Value))
        {
            throw new InvalidOperationException($"不能替换未知或待销毁网格: {rid}");
        }

        ValidateMeshMaterial(mesh);
        _meshes[rid.Value] = mesh;
        _backend.SetMesh(rid, mesh);
    }

    /// <summary>更新网格实例的通用模型矩阵；几何数据和 RID 保持不变。</summary>
    public void UpdateMeshTransform(RID rid, in Matrix4x4 transform)
    {
        if (!rid.IsValid || !_meshes.ContainsKey(rid.Value))
        {
            return;
        }

        if (!IsFinite(transform))
        {
            throw new ArgumentException("网格模型矩阵必须为有限值", nameof(transform));
        }

        _backend.UpdateMeshTransform(rid, in transform);
    }

    /// <summary>更新已有网格的通用颜色调制，不重新上传顶点或索引缓冲。</summary>
    public void UpdateMeshModulation(RID rid, in Vector4 modulation)
    {
        if (!rid.IsValid || !_meshes.TryGetValue(rid.Value, out var mesh) || _pendingRetirements.Contains(rid.Value))
        {
            return;
        }

        MeshData.ValidateModulation(modulation);
        _meshes[rid.Value] = mesh.WithModulation(modulation);
        _backend.UpdateMeshModulation(rid, in modulation);
    }

    public void SetEnvironmentModulation(in Vector4 modulation)
    {
        MeshData.ValidateModulation(modulation);
        _backend.SetEnvironmentModulation(in modulation);
    }

    /// <summary>请求销毁句柄：2 帧安全期后真正释放（避免 GPU 还在使用）。</summary>
    public void DestroyMesh(RID rid)
    {
        if (rid.IsValid && _meshes.ContainsKey(rid.Value) && _pendingRetirements.Add(rid.Value))
        {
            _retireQueue.Enqueue(new RetireEntry(ResourceKind.Mesh, rid.Value, _frame + 2));
        }
    }

    /// <summary>每渲染帧调用一次：推进帧号，销毁到期句柄。</summary>
    public void BeginFrame()
    {
        _frame++;
        while (_retireQueue.TryPeek(out var entry) && entry.RetireFrame <= _frame)
        {
            _retireQueue.Dequeue();
            var rid = new RID(entry.Rid);
            switch (entry.Kind)
            {
                case ResourceKind.Texture when _textures.Remove(entry.Rid):
                    _pendingTextureRetirements.Remove(entry.Rid);
                    _backend.RemoveTexture(rid);
                    break;
                case ResourceKind.Material when _materials.Remove(entry.Rid):
                    _pendingMaterialRetirements.Remove(entry.Rid);
                    _backend.RemoveMaterial(rid);
                    break;
                case ResourceKind.Mesh when _meshes.Remove(entry.Rid):
                    _pendingRetirements.Remove(entry.Rid);
                    _backend.RemoveMesh(rid);
                    break;
            }
        }
    }

    public (int Width, int Height) FramebufferSize => _backend.FramebufferSize;

    public void UpdateCamera(in Matrix4x4 view, in Matrix4x4 projection) => _backend.UpdateCamera(view, projection);

    public void Dispose()
    {
        if (_meshes.Count == 0 && _materials.Count == 0 && _textures.Count == 0)
        {
            _retireQueue.Clear();
            _textures.Clear();
            _materials.Clear();
            _meshes.Clear();
            _pendingTextureRetirements.Clear();
            _pendingMaterialRetirements.Clear();
            _pendingRetirements.Clear();
            return;
        }

        foreach (var id in _meshes.Keys)
        {
            _backend.RemoveMesh(new RID(id));
        }

        foreach (var id in _materials.Keys)
        {
            _backend.RemoveMaterial(new RID(id));
        }

        foreach (var id in _textures.Keys)
        {
            _backend.RemoveTexture(new RID(id));
        }

        _meshes.Clear();
        _materials.Clear();
        _textures.Clear();
        _retireQueue.Clear();
        _pendingTextureRetirements.Clear();
        _pendingMaterialRetirements.Clear();
        _pendingRetirements.Clear();
    }

    private RID AllocateRid() => new((ulong)Interlocked.Increment(ref _nextRid));

    private void ValidateMeshMaterial(MeshData mesh)
    {
        if (mesh.Material.IsValid &&
            (!_materials.ContainsKey(mesh.Material.Value) || _pendingMaterialRetirements.Contains(mesh.Material.Value)))
        {
            throw new InvalidOperationException($"网格引用了未知或待销毁材质: {mesh.Material}");
        }
    }

    private static bool IsFinite(in Matrix4x4 value)
    {
        return float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
            float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
            float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
            float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
    }
}
