namespace Cubit.Core.Scene;

/// <summary>
/// 场景节点（借鉴 Godot Node）：万物皆节点，组成树。
/// 生命周期钩子：EnterTree（自上而下）→ Ready（自下而上）→ Process/PhysicsProcess（每帧/固定步）→ ExitTree。
/// 信号直接用 C# event（原生类型安全）。
/// </summary>
public class Node : IDisposable
{
    public string Name { get; set; } = "Node";

    public Node? Parent { get; private set; }

    public SceneTree? Tree { get; internal set; }

    private readonly List<Node> _children = [];
    private readonly HashSet<string> _groups = [];
    private bool _lifecycleActive;

    public IReadOnlyList<Node> Children => _children;

    /// <summary>把子节点加入树；若已在树中则触发其 EnterTree/Ready。</summary>
    public void AddChild(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Parent is not null)
        {
            throw new InvalidOperationException($"节点 {child.Name} 已有父节点 {child.Parent.Name}");
        }

        if (Tree?.TryQueueChildAddition(this, child) == true)
        {
            return;
        }

        AddChildImmediately(child);
    }

    /// <summary>把子节点插入指定顺序位置；供场景编辑器和可复现的场景重组使用。</summary>
    public void AddChildAt(Node child, int index)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Parent is not null)
        {
            throw new InvalidOperationException($"节点 {child.Name} 已有父节点 {child.Parent.Name}");
        }

        if (index < 0 || index > _children.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "子节点插入位置超出范围");
        }

        if (Tree?.TryQueueChildAddition(this, child, index) == true)
        {
            return;
        }

        AddChildImmediately(child, index);
    }

    internal void AddChildImmediately(Node child)
        => AddChildImmediately(child, _children.Count);

    internal void AddChildImmediately(Node child, int index)
    {
        if (index < 0 || index > _children.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "子节点插入位置超出范围");
        }

        child.Parent = this;
        _children.Insert(index, child);
        if (Tree is not null)
        {
            child.Tree = Tree;
            if (Tree.LifecycleEnabled)
            {
                EnterSubtree(child);
                ReadySubtree(child);
            }
            else
            {
                AttachSubtree(child, Tree);
            }
        }
    }

    /// <summary>从树上移除子节点，触发其 ExitTree。</summary>
    public void RemoveChild(Node child)
    {
        if (!_children.Contains(child))
        {
            Tree?.ThrowIfChildStructuralChangePending(child);
            return;
        }

        if (Tree?.TryQueueChildRemoval(this, child) == true)
        {
            return;
        }

        RemoveChildImmediately(child);
    }

    internal void RemoveChildImmediately(Node child)
    {
        if (!_children.Remove(child))
        {
            return;
        }

        // 反序列化后的场景根是脱离树的容器；从中取出子节点并挂入运行树时，
        // 只是重组，不应伪造 ExitTree 生命周期。
        if (child.Tree is not null)
        {
            child.ExitSubtree();
        }

        child.Parent = null;
        child.Tree = null;
    }

    /// <summary>按路径取节点，如 "/root/World/Camera" 或相对名 "Camera"。</summary>
    public Node? GetNode(NodePath path)
    {
        Tree?.EnsureMainThread();
        return Tree?.ResolvePath(this, path);
    }

    public T? GetNode<T>(NodePath path) where T : Node => GetNode(path) as T;

    public NodePath GetPath()
    {
        var parts = new List<string>();
        Node? n = this;
        while (n is not null)
        {
            parts.Add(n.Name);
            n = n.Parent;
        }

        parts.Reverse();
        return new NodePath("/" + string.Join("/", parts));
    }

    public void AddToGroup(string group)
    {
        if (_groups.Add(group))
        {
            Tree?.RegisterGroup(group, this);
        }
    }

    public void RemoveFromGroup(string group)
    {
        if (_groups.Remove(group))
        {
            Tree?.UnregisterGroup(group, this);
        }
    }

    public bool IsInGroup(string group) => _groups.Contains(group);

    /// <summary>处理优先级：数值小的先被 Process/PhysicsProcess 调用（借鉴 Godot process_priority）。</summary>
    public int ProcessPriority { get; set; }

    // ---- 生命周期钩子（借鉴 Godot）：用户以 protected override 实现 ----

    protected virtual void EnterTree() { }

    protected virtual void Ready() { }

    protected virtual void Process(double delta) { }

    protected virtual void PhysicsProcess(double delta) { }

    /// <summary>固定步子节点完成输入与派生数据更新后调用的后置阶段。</summary>
    protected virtual void PostPhysicsProcess(double delta) { }

    protected virtual void ExitTree() { }

    // ---- 内部调用包装（供 SceneTree 驱动）----

    internal void InvokeEnterTree() => EnterTree();

    internal void InvokeReady() => Ready();

    internal void InvokeProcess(double delta) => Process(delta);

    internal void InvokePhysicsProcess(double delta) => PhysicsProcess(delta);

    internal void InvokePostPhysicsProcess(double delta) => PostPhysicsProcess(delta);

    internal void InvokeExitTree() => ExitTree();

    private static void EnterSubtree(Node node)
    {
        node._lifecycleActive = true;
        node.InvokeEnterTree();
        foreach (var child in node.Children)
        {
            child.Tree = node.Tree;
            EnterSubtree(child);
        }
    }

    private static void AttachSubtree(Node node, SceneTree tree)
    {
        node.Tree = tree;
        node._lifecycleActive = false;
        foreach (var child in node.Children)
        {
            AttachSubtree(child, tree);
        }
    }

    private static void ReadySubtree(Node node)
    {
        foreach (var child in node.Children)
        {
            ReadySubtree(child);
        }

        node.InvokeReady();
    }

    private void ExitSubtree()
    {
        for (var i = _children.Count - 1; i >= 0; i--)
        {
            _children[i].ExitSubtree();
            _children[i].Tree = null;
        }

        if (_lifecycleActive)
        {
            InvokeExitTree();
        }
        foreach (var group in _groups)
        {
            Tree?.UnregisterGroup(group, this);
        }

        _groups.Clear();
        _lifecycleActive = false;
    }

    public void Dispose()
    {
        if (Parent is not null)
        {
            Parent.RemoveChild(this);
        }
    }
}


