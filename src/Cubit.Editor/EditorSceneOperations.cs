using System.Reflection;
using Cubit.Core.Scene;

namespace Cubit.Editor;

/// <summary>编辑器场景树的纯操作服务；不依赖 ImGui，便于 Tool 和 MCP 复用。</summary>
public static class EditorSceneOperations
{
    public static CreateNodeEditorCommand CreateNodeCommand(
        SceneTree tree,
        SceneRegistry registry,
        Node? parent,
        string typeName,
        Action<Node>? configure = null) =>
        new(tree, registry, parent, typeName, configure);

    public static DeleteNodeEditorCommand DeleteNodeCommand(Node node) => new(node);

    public static RenameNodeEditorCommand RenameNodeCommand(Node node, string newName) => new(node, newName);

    public static SetPropertyEditorCommand SetPropertyCommand(object target, PropertyInfo property, object? value) =>
        new(target, property, value);

    /// <summary>从当前会话 registry 创建节点，并挂到指定父节点下。</summary>
    public static Node? CreateNode(
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
            return null;
        }

        var targetParent = parent ?? tree.Root;
        ValidateParent(tree, targetParent);

        var node = registry.InstantiateNode(typeName.Trim());
        if (node is null)
        {
            return null;
        }

        node.Name = MakeUniqueChildName(targetParent, GetSuggestedNodeName(node));
        configure?.Invoke(node);
        targetParent.AddChild(node);
        return node;
    }

    /// <summary>重命名节点；同级名称不区分大小写且必须唯一。</summary>
    public static bool RenameNode(Node node, string newName)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Parent is null || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        var normalized = newName.Trim();
        if (node.Parent.Children.Any(child => child != node &&
            child.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        node.Name = normalized;
        return true;
    }

    /// <summary>删除节点；根节点和脱离树节点不可删除。</summary>
    public static bool DeleteNode(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Parent is null)
        {
            return false;
        }

        node.Parent.RemoveChild(node);
        return true;
    }

    internal static void ValidateParent(SceneTree tree, Node parent)
    {
        if (parent.Tree != tree && !ReferenceEquals(parent, tree.Root))
        {
            throw new InvalidOperationException("节点父级不属于当前 SceneTree");
        }
    }

    internal static string MakeUniqueChildName(Node parent, string baseName)
    {
        var normalized = string.IsNullOrWhiteSpace(baseName) ? "Node" : baseName.Trim();
        if (!parent.Children.Any(child => child.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return normalized;
        }

        for (var index = 2; index < int.MaxValue; index++)
        {
            var candidate = $"{normalized}{index}";
            if (!parent.Children.Any(child => child.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"无法为节点生成唯一名称: {normalized}");
    }

    /// <summary>编辑器创建节点时使用类型名替换 Node 基类的占位默认名。</summary>
    internal static string GetSuggestedNodeName(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return string.IsNullOrWhiteSpace(node.Name) ||
            node.Name.Equals("Node", StringComparison.Ordinal)
            ? node.GetType().Name
            : node.Name.Trim();
    }

}
