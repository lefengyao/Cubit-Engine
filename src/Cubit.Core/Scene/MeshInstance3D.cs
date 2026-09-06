using System.Numerics;
using Cubit.Core.Rendering;

namespace Cubit.Core.Scene;

/// <summary>通用网格实例节点：只负责 MeshData 与 RenderingServer RID 生命周期。</summary>
public sealed class MeshInstance3D : Node3D, IRenderingServerConsumer
{
    private MeshData? _mesh;
    private RID _meshRid;
    private Matrix4x4 _lastSubmittedTransform;
    private bool _hasSubmittedTransform;

    [Export("网格")]
    public MeshData? Mesh
    {
        get => _mesh;
        set
        {
            EnsureMainThread();
            if (ReferenceEquals(_mesh, value))
            {
                return;
            }

            _mesh = value;
            if (Tree is not null)
            {
                UploadMesh();
            }
        }
    }

    public RenderingServer? Server { get; set; }

    protected override void Ready()
    {
        EnsureMainThread();
        if (Server is null)
        {
            throw new InvalidOperationException("MeshInstance3D 未注入渲染服务器");
        }

        UploadMesh();
        UpdateMeshTransform();
    }

    protected override void Process(double delta)
    {
        UpdateMeshTransform();
    }

    protected override void ExitTree()
    {
        EnsureMainThread();
        DestroyMesh();
    }

    private void UploadMesh()
    {
        if (Server is null)
        {
            throw new InvalidOperationException("MeshInstance3D 未注入渲染服务器");
        }

        if (_mesh is null)
        {
            DestroyMesh();
            return;
        }

        var nextRid = Server.CreateMesh(_mesh);
        var previousRid = _meshRid;
        _meshRid = nextRid;
        if (previousRid.IsValid)
        {
            Server.DestroyMesh(previousRid);
        }

        _hasSubmittedTransform = false;
    }

    private void UpdateMeshTransform()
    {
        if (Server is null || !_meshRid.IsValid || Tree is null)
        {
            return;
        }

        var transform = GlobalTransform.Matrix;
        if (_hasSubmittedTransform && transform == _lastSubmittedTransform)
        {
            return;
        }

        Server.UpdateMeshTransform(_meshRid, in transform);
        _lastSubmittedTransform = transform;
        _hasSubmittedTransform = true;
    }

    private void DestroyMesh()
    {
        if (_meshRid.IsValid)
        {
            Server?.DestroyMesh(_meshRid);
            _meshRid = RID.None;
        }
    }

    private void EnsureMainThread()
    {
        if (Tree is { IsMainThread: false })
        {
            throw new InvalidOperationException("MeshInstance3D 只能由拥有 SceneTree 的主线程访问");
        }
    }
}
