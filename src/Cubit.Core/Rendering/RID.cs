namespace Cubit.Core.Rendering;

/// <summary>
/// 渲染服务器资源句柄（借鉴 Godot RID）：不透明 ID，指向 RenderingServer 内的资源。
/// 句柄由服务器分配/释放，节点持有它引用服务器对象。
/// </summary>
public readonly struct RID : IEquatable<RID>
{
    public ulong Value { get; }

    public RID(ulong value)
    {
        Value = value;
    }

    public static RID None => default;

    public bool IsValid => Value != 0;

    public bool Equals(RID other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is RID other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(RID left, RID right) => left.Value == right.Value;

    public static bool operator !=(RID left, RID right) => left.Value != right.Value;

    public override string ToString() => $"RID:{Value}";
}