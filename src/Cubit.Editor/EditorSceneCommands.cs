using System.Reflection;
using Cubit.Core.Scene;

namespace Cubit.Editor;

/// <summary>创建节点命令；重做时复用原节点实例，保持其子树和运行时状态。</summary>
public sealed class CreateNodeEditorCommand : IEditorCommand
{
    private readonly SceneTree _tree;
    private readonly SceneRegistry _registry;
    private readonly Node _parent;
    private readonly string _typeName;
    private readonly Action<Node>? _configure;
    private int _index = -1;

    public Node? CreatedNode { get; private set; }

    public string Description => $"添加节点 {_typeName}";

    public CreateNodeEditorCommand(
        SceneTree tree,
        SceneRegistry registry,
        Node? parent,
        string typeName,
        Action<Node>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(registry);
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException("节点类型名不能为空", nameof(typeName));
        }

        _tree = tree;
        _registry = registry;
        _parent = parent ?? tree.Root;
        _typeName = typeName.Trim();
        _configure = configure;
        EditorSceneOperations.ValidateParent(_tree, _parent);
    }

    public void Execute()
    {
        if (CreatedNode is null)
        {
            var node = _registry.InstantiateNode(_typeName)
                ?? throw new InvalidOperationException($"当前会话未注册节点类型: {_typeName}");
            node.Name = EditorSceneOperations.MakeUniqueChildName(
                _parent,
                EditorSceneOperations.GetSuggestedNodeName(node));
            _configure?.Invoke(node);
            _parent.AddChild(node);
            CreatedNode = node;
            _index = _parent.Children.Count - 1;
            return;
        }

        if (CreatedNode.Parent is not null)
        {
            throw new InvalidOperationException($"节点 {CreatedNode.Name} 已经挂载到父级");
        }

        _parent.AddChildAt(CreatedNode, _index);
    }

    public void Undo()
    {
        if (CreatedNode?.Parent is not null)
        {
            CreatedNode.Parent.RemoveChild(CreatedNode);
        }
    }
}

/// <summary>删除节点命令；撤销时恢复父级和原始兄弟顺序。</summary>
public sealed class DeleteNodeEditorCommand : IEditorCommand
{
    private readonly Node _node;
    private readonly Node _parent;
    private readonly int _index;

    public string Description => $"删除节点 {_node.Name}";

    public DeleteNodeEditorCommand(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _node = node;
        _parent = node.Parent ?? throw new InvalidOperationException("根节点或脱离树节点不能删除");
        _index = -1;
        for (var index = 0; index < _parent.Children.Count; index++)
        {
            if (ReferenceEquals(_parent.Children[index], node))
            {
                _index = index;
                break;
            }
        }
        if (_index < 0)
        {
            throw new InvalidOperationException("节点不在父级的子节点列表中");
        }
    }

    public void Execute()
    {
        if (!ReferenceEquals(_node.Parent, _parent))
        {
            throw new InvalidOperationException("删除命令的节点父级已发生变化");
        }

        _parent.RemoveChild(_node);
    }

    public void Undo()
    {
        if (_node.Parent is not null)
        {
            throw new InvalidOperationException("删除命令的节点尚未脱离父级");
        }

        _parent.AddChildAt(_node, _index);
    }
}

/// <summary>重命名节点命令；复用场景树的同级唯一名称校验。</summary>
public sealed class RenameNodeEditorCommand : IEditorCommand
{
    private readonly Node _node;
    private readonly string _oldName;
    private readonly string _newName;

    public string Description => $"重命名节点 {_oldName} 为 {_newName}";

    public RenameNodeEditorCommand(Node node, string newName)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("节点名称不能为空", nameof(newName));
        }

        _node = node;
        _oldName = node.Name;
        _newName = newName.Trim();
    }

    public void Execute() => Rename(_newName);

    public void Undo() => Rename(_oldName);

    private void Rename(string name)
    {
        if (!EditorSceneOperations.RenameNode(_node, name))
        {
            throw new InvalidOperationException($"节点名称冲突或节点已脱离树: {name}");
        }
    }
}

/// <summary>Inspector 属性编辑命令；记录编辑前值以支持精确恢复。</summary>
public sealed class SetPropertyEditorCommand : IEditorCommand
{
    private readonly object _target;
    private readonly PropertyInfo _property;
    private readonly object? _oldValue;
    private readonly object? _newValue;

    public string Description => $"编辑属性 {_property.Name}";

    public SetPropertyEditorCommand(object target, PropertyInfo property, object? newValue)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        if (!property.CanRead || property.SetMethod is null || property.SetMethod.IsPublic is false)
        {
            throw new ArgumentException("属性必须可读且拥有公开 setter", nameof(property));
        }

        if (newValue is not null && !property.PropertyType.IsInstanceOfType(newValue) &&
            !(property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is not null &&
              Nullable.GetUnderlyingType(property.PropertyType)!.IsInstanceOfType(newValue)))
        {
            throw new ArgumentException($"值类型与属性不匹配: {property.PropertyType.FullName}", nameof(newValue));
        }

        _target = target;
        _property = property;
        _oldValue = property.GetValue(target);
        _newValue = newValue;
    }

    public void Execute() => _property.SetValue(_target, _newValue);

    public void Undo() => _property.SetValue(_target, _oldValue);
}
