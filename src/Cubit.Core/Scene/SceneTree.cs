using System.Buffers;

namespace Cubit.Core.Scene;

/// <summary>
/// 场景树（借鉴 Godot SceneTree）：持有根节点，驱动 Process 与固定模拟步（tick）。
/// 模拟 tick 默认 20Hz（MC 风格，可配置）；渲染帧独立，提供插值系数 alpha。
/// 防螺旋死亡：积压超过上限丢弃。
/// </summary>
public sealed class SceneTree : IDisposable
{
    private const int MaxSimulationStepsPerFrame = 5;

    private readonly Dictionary<string, List<Node>> _groups = [];
    private readonly List<DeferredStructuralChange> _deferredStructuralChanges = [];
    private readonly HashSet<Node> _deferredStructuralChangeChildren = [];
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private double _accumulator;
    private int _simulationTicksPerSecond = 20;
    private int _deferredStructuralChangesDepth;
    private bool _disposed;

    public Node Root { get; } = new() { Name = "Root" };

    /// <summary>场景树的创建线程；节点、生命周期和结构变更只能在此线程访问。</summary>
    public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    /// <summary>是否在节点挂载/卸载时运行生命周期；编辑器作者态关闭，预览和游戏态开启。</summary>
    public bool LifecycleEnabled
    {
        get => _lifecycleEnabled;
        set
        {
            EnsureMainThread();
            _lifecycleEnabled = value;
        }
    }

    private bool _lifecycleEnabled = true;

    /// <summary>模拟 tick 频率（Hz）。默认 20（与原版 Minecraft 一致），可配置为 60 等。</summary>
    public int SimulationTicksPerSecond
    {
        get => _simulationTicksPerSecond;
        set
        {
            EnsureMainThread();
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "模拟 tick 频率必须是正数");
            }

            _simulationTicksPerSecond = value;
        }
    }

    /// <summary>Godot 命名兼容别名（PhysicsProcess 由模拟 tick 驱动）。</summary>
    public int PhysicsTicksPerSecond
    {
        get => SimulationTicksPerSecond;
        set => SimulationTicksPerSecond = value;
    }

    public double PhysicsDelta => 1.0 / SimulationTicksPerSecond;

    /// <summary>渲染插值系数 alpha ∈ [0,1]：本渲染帧位于两次模拟 tick 之间的位置。</summary>
    public double PhysicsInterpolationAlpha
    {
        get
        {
            var alpha = _accumulator / PhysicsDelta;
            return alpha < 0 ? 0 : alpha > 1 ? 1 : alpha;
        }
    }

    /// <summary>暂停模拟：tick 不再推进；Process（渲染帧驱动）仍运行，保证渲染/上传继续。</summary>
    private bool _paused;

    public bool Paused
    {
        get => _paused;
        set
        {
            EnsureMainThread();
            _paused = value;
        }
    }

    public SceneTree()
    {
        Root.Tree = this;
    }

    /// <summary>
    /// 延后当前作用域内由已入树节点发起的 AddChild/RemoveChild。
    /// 待处理项在下一固定步开始、物理遍历前按调用顺序提交。
    /// </summary>
    public IDisposable BeginDeferredStructuralChanges()
    {
        EnsureMainThread();
        _deferredStructuralChangesDepth++;
        return new DeferredStructuralChangesScope(this);
    }

    /// <summary>每帧调用一次：先补固定模拟 tick，再跑普通 Process。</summary>
    public void ProcessFrame(double delta)
    {
        EnsureMainThread();
        if (!double.IsFinite(delta) || delta < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "帧 delta 必须是有限非负数");
        }

        if (!LifecycleEnabled)
        {
            return;
        }

        if (!Paused)
        {
            _accumulator += delta;
            var steps = 0;
            while (_accumulator >= PhysicsDelta && steps < MaxSimulationStepsPerFrame)
            {
                ApplyDeferredStructuralChanges();
                PhysicsProcessSubtree(Root, PhysicsDelta);
                _accumulator -= PhysicsDelta;
                steps++;
            }

            if (steps == MaxSimulationStepsPerFrame)
            {
                _accumulator = 0; // 防螺旋死亡：丢弃积压
            }
        }

        ProcessSubtree(Root, delta);
    }

    public Node? GetNode(NodePath path)
    {
        EnsureMainThread();
        return ResolvePath(Root, path);
    }

    /// <summary>
    /// 用新场景根的子节点替换当前 Root 的全部子节点（编辑器“切换运行场景”）。
    /// 旧子节点逐个移除（触发 ExitTree，释放 GPU/流资源）；新子节点挂到 Root（触发 EnterTree/Ready）。
    /// incomingRoot 本身不入树，仅作为场景文件反序列化后的容器。
    /// </summary>
    public void ReplaceChildren(Node incomingRoot)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(incomingRoot);

        for (var i = Root.Children.Count - 1; i >= 0; i--)
        {
            Root.RemoveChild(Root.Children[i]);
        }

        foreach (var child in incomingRoot.Children.ToList())
        {
            // incomingRoot 仅作容器：先解绑（不触发 ExitTree，容器不在树中），再挂入运行树
            incomingRoot.RemoveChild(child);
            Root.AddChild(child);
        }
    }

    public IReadOnlyList<Node> GetNodesInGroup(string group)
    {
        EnsureMainThread();
        return _groups.TryGetValue(group, out var list) ? list : [];
    }

    /// <summary>所有分组名（供编辑器/MCP 枚举）。</summary>
    public string[] Groups
    {
        get
        {
            EnsureMainThread();
            return [.. _groups.Keys];
        }
    }

    internal Node? ResolvePath(Node from, NodePath path)
    {
        var value = path.Value.Trim().Trim('/');
        if (value.Length == 0)
        {
            return null;
        }

        var parts = value.Split('/');
        var index = 0;
        Node current;
        if (parts[0].Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            current = Root;
            index = 1;
        }
        else
        {
            current = from;
        }

        for (; index < parts.Length; index++)
        {
            var next = current.Children.FirstOrDefault(c => c.Name == parts[index]);
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    internal void RegisterGroup(string group, Node node)
    {
        EnsureMainThread();
        if (!_groups.TryGetValue(group, out var list))
        {
            list = [];
            _groups[group] = list;
        }

        if (!list.Contains(node))
        {
            list.Add(node);
        }
    }

    internal void UnregisterGroup(string group, Node node)
    {
        EnsureMainThread();
        if (_groups.TryGetValue(group, out var list))
        {
            list.Remove(node);
        }
    }

    internal bool TryQueueChildAddition(Node parent, Node child) =>
        TryQueueChildAddition(parent, child, index: -1);

    internal bool TryQueueChildAddition(Node parent, Node child, int index) =>
        TryQueueStructuralChange(parent, child, DeferredStructuralChangeKind.Add, index);

    internal bool TryQueueChildRemoval(Node parent, Node child) =>
        TryQueueStructuralChange(parent, child, DeferredStructuralChangeKind.Remove, index: -1);

    internal void ThrowIfChildStructuralChangePending(Node child)
    {
        EnsureMainThread();
        if (_deferredStructuralChangeChildren.Contains(child))
        {
            throw new InvalidOperationException($"节点 {child.Name} 在同一延后结构变更作用域中已有待处理操作");
        }
    }

    private bool TryQueueStructuralChange(Node parent, Node child, DeferredStructuralChangeKind kind, int index)
    {
        EnsureMainThread();
        ThrowIfChildStructuralChangePending(child);
        if (_deferredStructuralChangesDepth == 0)
        {
            return false;
        }

        _deferredStructuralChangeChildren.Add(child);
        _deferredStructuralChanges.Add(new DeferredStructuralChange(parent, child, kind, index));
        return true;
    }

    private void ApplyDeferredStructuralChanges()
    {
        if (_deferredStructuralChangesDepth != 0 || _deferredStructuralChanges.Count == 0)
        {
            return;
        }

        try
        {
            for (var index = 0; index < _deferredStructuralChanges.Count; index++)
            {
                var change = _deferredStructuralChanges[index];
                if (change.Kind == DeferredStructuralChangeKind.Add)
                {
                    var childIndex = change.Index < 0 ? change.Parent.Children.Count : change.Index;
                    change.Parent.AddChildImmediately(change.Child, Math.Min(childIndex, change.Parent.Children.Count));
                }
                else
                {
                    change.Parent.RemoveChildImmediately(change.Child);
                }
            }
        }
        finally
        {
            _deferredStructuralChanges.Clear();
            _deferredStructuralChangeChildren.Clear();
        }
    }

    private static void PhysicsProcessSubtree(Node node, double delta)
    {
        node.InvokePhysicsProcess(delta);
        ProcessChildrenInPriorityOrder(node.Children, delta, physics: true);
        node.InvokePostPhysicsProcess(delta);
    }

    private static void ProcessSubtree(Node node, double delta)
    {
        node.InvokeProcess(delta);
        ProcessChildrenInPriorityOrder(node.Children, delta, physics: false);
    }

    private static void RunSubtree(Node node, double delta, bool physics)
    {
        if (physics)
        {
            PhysicsProcessSubtree(node, delta);
        }
        else
        {
            ProcessSubtree(node, delta);
        }
    }

    /// <summary>按 ProcessPriority 稳定排序子节点后递归处理（小规模插入排序，零分配）。</summary>
    private static void ProcessChildrenInPriorityOrder(IReadOnlyList<Node> children, double delta, bool physics)
    {
        var count = children.Count;
        if (count == 0)
        {
            return;
        }

        if (count == 1)
        {
            RunSubtree(children[0], delta, physics);
            return;
        }

        var nodes = ArrayPool<Node>.Shared.Rent(count);
        var prios = ArrayPool<int>.Shared.Rent(count);
        for (var i = 0; i < count; i++)
        {
            nodes[i] = children[i];
            prios[i] = children[i].ProcessPriority;
        }

        // 稳定插入排序：仅当严格小于才前移，保证同优先级保持插入顺序
        for (var i = 1; i < count; i++)
        {
            var n = nodes[i];
            var p = prios[i];
            var j = i - 1;
            while (j >= 0 && prios[j] > p)
            {
                nodes[j + 1] = nodes[j];
                prios[j + 1] = prios[j];
                j--;
            }

            nodes[j + 1] = n;
            prios[j + 1] = p;
        }

        for (var i = 0; i < count; i++)
        {
            RunSubtree(nodes[i], delta, physics);
        }

        ArrayPool<Node>.Shared.Return(nodes);
        ArrayPool<int>.Shared.Return(prios);
    }

    public void Dispose()
    {
        EnsureMainThread();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = Root.Children.Count - 1; i >= 0; i--)
        {
            Root.RemoveChild(Root.Children[i]);
        }

        _groups.Clear();
    }

    internal void EnsureMainThread()
    {
        if (!IsMainThread)
        {
            throw new InvalidOperationException("SceneTree 只能由创建线程访问");
        }
    }

    private void EndDeferredStructuralChanges()
    {
        if (_deferredStructuralChangesDepth <= 0)
        {
            throw new InvalidOperationException("延后结构变更作用域已结束");
        }

        _deferredStructuralChangesDepth--;
    }

    private readonly record struct DeferredStructuralChange(
        Node Parent,
        Node Child,
        DeferredStructuralChangeKind Kind,
        int Index);

    private enum DeferredStructuralChangeKind
    {
        Add,
        Remove,
    }

    private sealed class DeferredStructuralChangesScope(SceneTree tree) : IDisposable
    {
        private SceneTree? _tree = tree;

        public void Dispose()
        {
            var tree = _tree;
            if (tree is null)
            {
                return;
            }

            _tree = null;
            tree.EndDeferredStructuralChanges();
        }
    }
}
