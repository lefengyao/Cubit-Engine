using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Cubit.Core.Scene;

namespace Cubit.Animation;

public enum TweenEase
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

/// <summary>场景树中的单属性补间；不创建全局调度器。</summary>
public sealed class Tween : Node
{
    private TweenBinding? _binding;
    private double _elapsed;
    private int _remainingRepeats;
    private int _ownerThreadId;
    private int _repeatCount;
    private TweenEase _ease = TweenEase.Linear;

    public event Action? Completed;

    public event Action? Canceled;

    public bool IsPlaying { get; private set; }

    public double Elapsed => _elapsed;

    /// <summary>补间缓动曲线，必须是已定义的枚举值。</summary>
    public TweenEase Ease
    {
        get => _ease;
        set
        {
            EnsureMainThread();
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "补间缓动类型无效");
            }

            _ease = value;
        }
    }

    /// <summary>重复次数；-1 表示无限重复，非负数表示额外重复次数。</summary>
    public int RepeatCount
    {
        get => _repeatCount;
        set
        {
            EnsureMainThread();
            if (value < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(RepeatCount), "重复次数只能是 -1 或非负数。");
            }

            _repeatCount = value;
        }
    }

    public void StartFloat(NodePath targetPath, string propertyName, float from, float to, double duration)
    {
        Start(targetPath, propertyName, duration, TweenValue.FromFloat(from), TweenValue.FromFloat(to));
    }

    public void StartVector3(NodePath targetPath, string propertyName, Vector3 from, Vector3 to, double duration)
    {
        Start(targetPath, propertyName, duration, TweenValue.FromVector3(from), TweenValue.FromVector3(to));
    }

    public void StartQuaternion(NodePath targetPath, string propertyName, Quaternion from, Quaternion to, double duration)
    {
        Start(targetPath, propertyName, duration, TweenValue.FromQuaternion(from), TweenValue.FromQuaternion(to));
    }

    public void Cancel()
    {
        EnsureMainThread();
        if (!IsPlaying)
        {
            return;
        }

        IsPlaying = false;
        Canceled?.Invoke();
    }

    protected override void EnterTree()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    protected override void Process(double delta)
    {
        EnsureMainThread();
        if (!IsPlaying)
        {
            return;
        }

        if (!double.IsFinite(delta) || delta < 0d)
        {
            throw new ArgumentException("Tween 帧时间必须是有限非负数", nameof(delta));
        }

        var binding = _binding!;
        _elapsed += delta;
        var amount = Math.Clamp((float)(_elapsed / binding.Duration), 0f, 1f);
        binding.Apply(TweenValue.Lerp(binding.From, binding.To, ApplyEase(amount)));
        if (_elapsed < binding.Duration)
        {
            return;
        }

        if (_remainingRepeats == -1 || _remainingRepeats > 0)
        {
            if (_remainingRepeats > 0)
            {
                _remainingRepeats--;
            }

            _elapsed = 0d;
            binding.Apply(binding.From);
            return;
        }

        IsPlaying = false;
        Completed?.Invoke();
    }

    private void Start(NodePath targetPath, string propertyName, double duration, TweenValue from, TweenValue to)
    {
        EnsureMainThread();
        if (!double.IsFinite(duration) || duration <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var target = GetNode(targetPath) ?? throw new InvalidOperationException($"Tween 目标节点不存在: {targetPath}");
        var property = target.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tween 目标属性不存在: {propertyName}");
        if (property.SetMethod is null || !property.SetMethod.IsPublic)
        {
            throw new InvalidOperationException($"Tween 目标属性不可写: {propertyName}");
        }

        _binding = TweenBinding.Create(target, property, duration, from, to);
        _elapsed = 0d;
        _remainingRepeats = RepeatCount;
        IsPlaying = true;
        _binding.Apply(from);
    }

    private float ApplyEase(float amount) => Ease switch
    {
        TweenEase.Linear => amount,
        TweenEase.EaseIn => amount * amount,
        TweenEase.EaseOut => 1f - (1f - amount) * (1f - amount),
        TweenEase.EaseInOut => amount < 0.5f
            ? 2f * amount * amount
            : 1f - MathF.Pow(-2f * amount + 2f, 2f) / 2f,
        _ => amount,
    };

    private void EnsureMainThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == 0)
        {
            _ownerThreadId = current;
        }
        else if (_ownerThreadId != current)
        {
            throw new InvalidOperationException("Tween 只能在拥有它的主线程访问");
        }
    }

    private readonly struct TweenValue
    {
        public AnimationValueKind Kind { get; }
        public float Float { get; }
        public Vector3 Vector3 { get; }
        public Quaternion Quaternion { get; }

        private TweenValue(AnimationValueKind kind, float @float, Vector3 vector3, Quaternion quaternion)
        {
            Kind = kind;
            Float = @float;
            Vector3 = vector3;
            Quaternion = quaternion;
        }

        public static TweenValue FromFloat(float value)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException("Tween 浮点端点必须是有限数", nameof(value));
            }

            return new TweenValue(AnimationValueKind.Float, value, default, default);
        }

        public static TweenValue FromVector3(Vector3 value)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            {
                throw new ArgumentException("Tween Vector3 端点必须是有限数", nameof(value));
            }

            return new TweenValue(AnimationValueKind.Vector3, 0f, value, default);
        }

        public static TweenValue FromQuaternion(Quaternion value)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
                !float.IsFinite(value.Z) || !float.IsFinite(value.W) ||
                value.LengthSquared() <= 1e-12f)
            {
                throw new ArgumentException("Tween Quaternion 端点必须是有限非零数", nameof(value));
            }

            return new TweenValue(AnimationValueKind.Quaternion, 0f, default, Quaternion.Normalize(value));
        }

        public static TweenValue Lerp(TweenValue from, TweenValue to, float amount) => from.Kind switch
        {
            AnimationValueKind.Float => FromFloat(float.Lerp(from.Float, to.Float, amount)),
            AnimationValueKind.Vector3 => FromVector3(Vector3.Lerp(from.Vector3, to.Vector3, amount)),
            AnimationValueKind.Quaternion => FromQuaternion(Quaternion.Slerp(from.Quaternion, to.Quaternion, amount)),
            _ => throw new InvalidOperationException("Tween 不支持离散值"),
        };
    }

    private sealed class TweenBinding
    {
        private readonly Action<float>? _floatSetter;
        private readonly Action<Vector3>? _vectorSetter;
        private readonly Action<Quaternion>? _quaternionSetter;

        private TweenBinding(double duration, TweenValue from, TweenValue to, Action<float>? floatSetter,
            Action<Vector3>? vectorSetter, Action<Quaternion>? quaternionSetter)
        {
            Duration = duration;
            From = from;
            To = to;
            _floatSetter = floatSetter;
            _vectorSetter = vectorSetter;
            _quaternionSetter = quaternionSetter;
        }

        public double Duration { get; }
        public TweenValue From { get; }
        public TweenValue To { get; }

        public static TweenBinding Create(Node target, PropertyInfo property, double duration, TweenValue from, TweenValue to)
        {
            return from.Kind switch
            {
                AnimationValueKind.Float when property.PropertyType == typeof(float) =>
                    new TweenBinding(duration, from, to, BuildSetter<float>(target, property), null, null),
                AnimationValueKind.Vector3 when property.PropertyType == typeof(Vector3) =>
                    new TweenBinding(duration, from, to, null, BuildSetter<Vector3>(target, property), null),
                AnimationValueKind.Quaternion when property.PropertyType == typeof(Quaternion) =>
                    new TweenBinding(duration, from, to, null, null, BuildSetter<Quaternion>(target, property)),
                _ => throw new InvalidOperationException("Tween 值种类与属性类型不匹配"),
            };
        }

        public void Apply(TweenValue value)
        {
            switch (value.Kind)
            {
                case AnimationValueKind.Float:
                    _floatSetter?.Invoke(value.Float);
                    break;
                case AnimationValueKind.Vector3:
                    _vectorSetter?.Invoke(value.Vector3);
                    break;
                case AnimationValueKind.Quaternion:
                    _quaternionSetter?.Invoke(value.Quaternion);
                    break;
            }
        }

        private static Action<T> BuildSetter<T>(Node target, PropertyInfo property)
        {
            var value = Expression.Parameter(typeof(T), "value");
            var call = Expression.Call(Expression.Constant(target), property.SetMethod!,
                Expression.Convert(value, property.PropertyType));
            return Expression.Lambda<Action<T>>(call, value).Compile();
        }
    }
}
