namespace Cubit.Core.Scene;

/// <summary>节点路径（借鉴 Godot NodePath）。示例："/root/World/Camera"。</summary>
public readonly record struct NodePath(string Value)
{
    public static implicit operator NodePath(string value) => new(value);

    public override string ToString() => Value;
}
