using System.Collections.ObjectModel;
using Cubit.Core.Scene;

namespace Cubit.Scripting;

/// <summary>用户项目脚本基类。实例由 ScriptRuntime 创建，只在主线程收到生命周期回调。</summary>
public abstract class CubitScript
{
    /// <summary>当前脚本绑定的场景节点。</summary>
    public Node? TargetNode { get; private set; }

    protected virtual void OnEnterTree() { }

    protected virtual void OnReady() { }

    protected virtual void OnPhysicsProcess(double delta) { }

    protected virtual void OnProcess(double delta) { }

    protected virtual void OnExitTree() { }

    internal void Attach(Node target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (TargetNode is not null)
        {
            throw new InvalidOperationException("CubitScript 不能重复绑定节点");
        }

        TargetNode = target;
    }

    internal void InvokeEnterTree() => OnEnterTree();

    internal void InvokeReady() => OnReady();

    internal void InvokePhysicsProcess(double delta) => OnPhysicsProcess(delta);

    internal void InvokeProcess(double delta) => OnProcess(delta);

    internal void InvokeExitTree() => OnExitTree();
}

/// <summary>场景中的脚本附件声明；程序集加载只由 ScriptRuntime 显式触发。</summary>
public sealed class ScriptAttachment : Resource
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyOverrides =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));
    private NodePath _targetPath;
    private string _typeName = "";
    private IReadOnlyDictionary<string, object?> _propertyOverrides = EmptyOverrides;
    private int _ownerThreadId;

    public ScriptAttachment()
    {
        _targetPath = new NodePath("");
        _typeName = "";
    }

    public ScriptAttachment(
        NodePath targetPath,
        string typeName,
        IReadOnlyDictionary<string, object?>? propertyOverrides = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath.Value))
        {
            throw new ArgumentException("脚本目标路径不能为空", nameof(targetPath));
        }

        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException("脚本类型名不能为空", nameof(typeName));
        }

        _targetPath = targetPath;
        _typeName = typeName.Trim();
        PropertyOverrides = propertyOverrides ?? EmptyOverrides;
    }

    [Export]
    public NodePath TargetPath
    {
        get
        {
            EnsureOwnerThread();
            return _targetPath;
        }
        set
        {
            EnsureOwnerThread();
            _targetPath = value;
        }
    }

    [Export]
    public string TypeName
    {
        get
        {
            EnsureOwnerThread();
            return _typeName;
        }
        set
        {
            EnsureOwnerThread();
            _typeName = value ?? "";
        }
    }

    /// <summary>
    /// 导出的脚本属性覆盖。写入时复制字典，避免场景反序列化后的可变字典被作者场景与运行时共享。
    /// </summary>
    [Export]
    public IReadOnlyDictionary<string, object?> PropertyOverrides
    {
        get
        {
            EnsureOwnerThread();
            return _propertyOverrides;
        }
        set
        {
            EnsureOwnerThread();
            _propertyOverrides = CreateOverridesSnapshot(value);
        }
    }

    /// <summary>把资源绑定到场景节点的所属线程；未入树资源仍可由作者线程构造和反序列化。</summary>
    internal void BindOwnerThread(int ownerThreadId)
    {
        if (ownerThreadId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerThreadId));
        }

        if (_ownerThreadId != 0 && _ownerThreadId != ownerThreadId)
        {
            throw new InvalidOperationException("ScriptAttachment 不能跨线程复用");
        }

        _ownerThreadId = ownerThreadId;
    }

    /// <summary>为运行时复制独立附件，避免作者场景对象在预览期间被别名修改。</summary>
    internal ScriptAttachment Snapshot()
    {
        EnsureOwnerThread();
        var snapshot = new ScriptAttachment
        {
            _targetPath = _targetPath,
            _typeName = _typeName,
            _propertyOverrides = CreateOverridesSnapshot(_propertyOverrides),
        };
        return snapshot;
    }

    private void EnsureOwnerThread()
    {
        if (_ownerThreadId != 0 && _ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("ScriptAttachment 只能在所属线程访问");
        }
    }

    private static IReadOnlyDictionary<string, object?> CreateOverridesSnapshot(
        IReadOnlyDictionary<string, object?>? propertyOverrides) =>
        propertyOverrides is null || propertyOverrides.Count == 0
            ? EmptyOverrides
            : new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(propertyOverrides, StringComparer.Ordinal));
}

/// <summary>脚本加载或生命周期失败时携带稳定上下文的异常。</summary>
public sealed class ScriptRuntimeException : InvalidOperationException
{
    public ScriptRuntimeException(
        string message,
        string scriptType,
        string nodePath,
        string phase,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ScriptType = scriptType;
        NodePath = nodePath;
        Phase = phase;
    }

    public string ScriptType { get; }

    public string NodePath { get; }

    public string Phase { get; }
}
