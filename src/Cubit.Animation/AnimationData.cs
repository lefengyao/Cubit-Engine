using System.Numerics;
using System.Text.Json;
using Cubit.Core.Scene;

namespace Cubit.Animation;

/// <summary>动画轨道支持的值种类。</summary>
public enum AnimationValueKind
{
    Float,
    Vector3,
    Quaternion,
    Discrete,
}

/// <summary>按秒排序的动画键；值的实际类型由所属轨道决定。</summary>
public readonly record struct AnimationKey(double Time, object? Value);

/// <summary>动画事件标记。</summary>
public readonly record struct AnimationEventMarker(double Time, string Name);

/// <summary>绑定一个 NodePath 属性的通用动画轨道。</summary>
public sealed class AnimationTrack : Resource
{
    private readonly List<AnimationKey> _keys = [];

    public NodePath TargetPath { get; set; }

    public string PropertyName { get; set; } = string.Empty;

    public AnimationValueKind Kind { get; set; }

    public IReadOnlyList<AnimationKey> Keys => _keys;

    public void AddFloatKey(double time, float value)
    {
        SetKind(AnimationValueKind.Float);
        AddKey(time, value);
    }

    public void AddVector3Key(double time, Vector3 value)
    {
        SetKind(AnimationValueKind.Vector3);
        AddKey(time, value);
    }

    public void AddQuaternionKey(double time, Quaternion value)
    {
        SetKind(AnimationValueKind.Quaternion);
        ValidateValue(AnimationValueKind.Quaternion, value);
        AddKey(time, Quaternion.Normalize(value));
    }

    public void AddDiscreteKey(double time, object? value)
    {
        SetKind(AnimationValueKind.Discrete);
        AddKey(time, value);
    }

    internal void SortAndValidate()
    {
        if (string.IsNullOrWhiteSpace(TargetPath.Value) || string.IsNullOrWhiteSpace(PropertyName))
        {
            throw new InvalidOperationException("动画轨道必须有目标路径和属性名");
        }

        if (_keys.Count == 0)
        {
            throw new InvalidOperationException("动画轨道至少需要一个键");
        }

        _keys.Sort(static (left, right) => left.Time.CompareTo(right.Time));
        var previous = -1d;
        foreach (var key in _keys)
        {
            if (!double.IsFinite(key.Time) || key.Time < 0d || key.Time <= previous)
            {
                throw new InvalidOperationException("动画键时间必须是严格递增的有限非负数");
            }

            ValidateValue(Kind, key.Value);
            previous = key.Time;
        }
    }

    internal AnimationTrack Clone()
    {
        var clone = new AnimationTrack
        {
            TargetPath = TargetPath,
            PropertyName = PropertyName,
            Kind = Kind,
        };
        clone._keys.AddRange(_keys);
        return clone;
    }

    internal bool TrySample(double time, out AnimationValue value)
    {
        if (_keys.Count == 0)
        {
            value = default;
            return false;
        }

        if (time <= _keys[0].Time)
        {
            value = AnimationValue.From(Kind, _keys[0].Value);
            return true;
        }

        if (time >= _keys[^1].Time)
        {
            value = AnimationValue.From(Kind, _keys[^1].Value);
            return true;
        }

        var upper = 1;
        while (upper < _keys.Count && _keys[upper].Time <= time)
        {
            upper++;
        }

        var lower = upper - 1;
        var left = _keys[lower];
        var right = _keys[upper];
        if (Kind == AnimationValueKind.Discrete)
        {
            value = AnimationValue.From(Kind, left.Value);
            return true;
        }

        var amount = (float)((time - left.Time) / (right.Time - left.Time));
        value = Kind switch
        {
            AnimationValueKind.Float => AnimationValue.FromFloat(
                float.Lerp((float)left.Value!, (float)right.Value!, amount)),
            AnimationValueKind.Vector3 => AnimationValue.FromVector3(
                Vector3.Lerp((Vector3)left.Value!, (Vector3)right.Value!, amount)),
            AnimationValueKind.Quaternion => AnimationValue.FromQuaternion(
                Quaternion.Slerp((Quaternion)left.Value!, (Quaternion)right.Value!, amount)),
            _ => throw new InvalidOperationException("未知动画值种类"),
        };
        return true;
    }

    private void SetKind(AnimationValueKind kind)
    {
        if (_keys.Count > 0 && Kind != kind)
        {
            throw new InvalidOperationException("同一动画轨道不能混用不同值种类");
        }

        Kind = kind;
    }

    private void AddKey(double time, object? value)
    {
        if (!double.IsFinite(time) || time < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(time), "动画键时间必须是有限非负数");
        }

        ValidateValue(Kind, value);
        _keys.Add(new AnimationKey(time, value));
    }

    private static void ValidateValue(AnimationValueKind kind, object? value)
    {
        var valid = kind switch
        {
            AnimationValueKind.Float => value is float floatValue && float.IsFinite(floatValue),
            AnimationValueKind.Vector3 => value is Vector3 vector && IsFinite(vector),
            AnimationValueKind.Quaternion => value is Quaternion quaternion &&
                IsFinite(quaternion) && quaternion.LengthSquared() > 1e-12f,
            AnimationValueKind.Discrete => value is null || value is string || value is bool ||
                value is int || value is long || value.GetType().IsEnum,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException($"动画键值类型与轨道 {kind} 不匹配", nameof(value));
        }
    }

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>可序列化的通用时间轴动画资源。</summary>
public sealed class AnimationClip : Resource
{
    private readonly List<AnimationTrack> _tracks = [];
    private readonly List<AnimationEventMarker> _events = [];

    [Export("时长")]
    public double Length { get; set; } = 1d;

    [Export("循环")]
    public bool Loop { get; set; }

    public IReadOnlyList<AnimationTrack> Tracks => _tracks;

    public IReadOnlyList<AnimationEventMarker> Events => _events;

    public void AddTrack(AnimationTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        track.SortAndValidate();
        if (_tracks.Any(existing => existing.TargetPath == track.TargetPath &&
                                    existing.PropertyName.Equals(track.PropertyName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("动画片段不能重复绑定同一 NodePath 属性");
        }

        _tracks.Add(track.Clone());
    }

    public void AddEvent(double time, string name)
    {
        if (!double.IsFinite(time) || time < 0d || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("动画事件必须有有限非负时间和名称");
        }

        _events.Add(new AnimationEventMarker(time, name));
        _events.Sort(static (left, right) => left.Time.CompareTo(right.Time));
    }

    internal void Validate()
    {
        if (!double.IsFinite(Length) || Length <= 0d)
        {
            throw new InvalidOperationException("动画片段时长必须是有限正数");
        }

        foreach (var track in _tracks)
        {
            track.SortAndValidate();
            if (track.Keys[^1].Time > Length)
            {
                throw new InvalidOperationException("动画键不能超出片段时长");
            }
        }

        foreach (var marker in _events)
        {
            if (marker.Time > Length)
            {
                throw new InvalidOperationException("动画事件不能超出片段时长");
            }
        }
    }

    /// <summary>为运行态播放器创建独立快照，避免继续引用可编辑的作者资源。</summary>
    internal AnimationClip CreatePlaybackSnapshot()
    {
        Validate();
        var snapshot = new AnimationClip
        {
            Length = Length,
            Loop = Loop,
        };
        foreach (var track in _tracks)
        {
            snapshot._tracks.Add(track.Clone());
        }

        snapshot._events.AddRange(_events);
        return snapshot;
    }

    /// <summary>使用稳定 DTO 保存轨道值，避免 object 反序列化成 JsonElement。</summary>
    public new string ToJson()
    {
        Validate();
        var dto = new ClipDto
        {
            Length = Length,
            Loop = Loop,
            Tracks = _tracks.Select(track => new TrackDto
            {
                TargetPath = track.TargetPath.Value,
                PropertyName = track.PropertyName,
                Kind = track.Kind,
                Keys = track.Keys.Select(key => new KeyDto
                {
                    Time = key.Time,
                    Float = key.Value as float?,
                    Vector3 = key.Value is Vector3 vector ? vector : null,
                    Quaternion = key.Value is Quaternion quaternion ? quaternion : null,
                    Discrete = track.Kind == AnimationValueKind.Discrete
                        ? DiscreteValueDto.FromValue(key.Value)
                        : null,
                }).ToArray(),
            }).ToArray(),
            Events = _events.ToArray(),
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    public static AnimationClip FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<ClipDto>(json, JsonOptions)
            ?? throw new InvalidOperationException("动画片段解析失败");
        var clip = new AnimationClip { Length = dto.Length, Loop = dto.Loop };
        foreach (var trackDto in dto.Tracks ?? [])
        {
            var track = new AnimationTrack
            {
                TargetPath = trackDto.TargetPath,
                PropertyName = trackDto.PropertyName,
                Kind = trackDto.Kind,
            };
            foreach (var key in trackDto.Keys ?? [])
            {
                switch (track.Kind)
                {
                    case AnimationValueKind.Float:
                        track.AddFloatKey(key.Time, key.Float ?? throw new InvalidOperationException("缺少浮点动画值"));
                        break;
                    case AnimationValueKind.Vector3:
                        track.AddVector3Key(key.Time, key.Vector3 ?? throw new InvalidOperationException("缺少 Vector3 动画值"));
                        break;
                    case AnimationValueKind.Quaternion:
                        track.AddQuaternionKey(key.Time, key.Quaternion ?? throw new InvalidOperationException("缺少 Quaternion 动画值"));
                        break;
                    case AnimationValueKind.Discrete:
                        track.AddDiscreteKey(key.Time, key.Discrete?.ToValue());
                        break;
                }
            }
            clip.AddTrack(track);
        }

        foreach (var marker in dto.Events ?? [])
        {
            clip.AddEvent(marker.Time, marker.Name);
        }

        clip.Validate();
        return clip;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class ClipDto
    {
        public double Length { get; set; }
        public bool Loop { get; set; }
        public TrackDto[]? Tracks { get; set; }
        public AnimationEventMarker[]? Events { get; set; }
    }

    private sealed class TrackDto
    {
        public string TargetPath { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public AnimationValueKind Kind { get; set; }
        public KeyDto[]? Keys { get; set; }
    }

    private sealed class KeyDto
    {
        public double Time { get; set; }
        public float? Float { get; set; }
        public Vector3? Vector3 { get; set; }
        public Quaternion? Quaternion { get; set; }
        public DiscreteValueDto? Discrete { get; set; }
    }

    private enum DiscreteValueKind
    {
        Null,
        String,
        Boolean,
        Int32,
        Int64,
        Enum,
    }

    /// <summary>受限离散值编码，不依赖 object 的默认 JSON 推断。</summary>
    private sealed class DiscreteValueDto
    {
        public DiscreteValueKind Kind { get; set; }
        public string? String { get; set; }
        public bool Boolean { get; set; }
        public int Int32 { get; set; }
        public long Int64 { get; set; }
        public string? EnumType { get; set; }
        public string? EnumAssembly { get; set; }
        public string? EnumValue { get; set; }

        public static DiscreteValueDto FromValue(object? value) => value switch
        {
            null => new DiscreteValueDto { Kind = DiscreteValueKind.Null },
            string text => new DiscreteValueDto { Kind = DiscreteValueKind.String, String = text },
            bool boolean => new DiscreteValueDto { Kind = DiscreteValueKind.Boolean, Boolean = boolean },
            int integer => new DiscreteValueDto { Kind = DiscreteValueKind.Int32, Int32 = integer },
            long integer => new DiscreteValueDto { Kind = DiscreteValueKind.Int64, Int64 = integer },
            _ when value.GetType().IsEnum => new DiscreteValueDto
            {
                Kind = DiscreteValueKind.Enum,
                EnumType = value.GetType().FullName,
                EnumAssembly = value.GetType().Assembly.GetName().Name,
                EnumValue = Enum.Format(value.GetType(), value, "D"),
            },
            _ => throw new InvalidOperationException("不支持序列化该离散动画值"),
        };

        public object? ToValue() => Kind switch
        {
            DiscreteValueKind.Null => null,
            DiscreteValueKind.String => String ?? throw new InvalidOperationException("离散字符串动画值缺失"),
            DiscreteValueKind.Boolean => Boolean,
            DiscreteValueKind.Int32 => Int32,
            DiscreteValueKind.Int64 => Int64,
            DiscreteValueKind.Enum => ParseEnum(),
            _ => throw new InvalidOperationException("未知离散动画值种类"),
        };

        private object ParseEnum()
        {
            if (string.IsNullOrWhiteSpace(EnumAssembly) || string.IsNullOrWhiteSpace(EnumType) ||
                string.IsNullOrWhiteSpace(EnumValue))
            {
                throw new InvalidOperationException("离散枚举动画值缺少类型信息");
            }

            // 只查询当前已加载的程序集，资源数据不能借此加载任意代码。
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, EnumAssembly, StringComparison.Ordinal));
            var enumType = assembly?.GetType(EnumType, throwOnError: false, ignoreCase: false);
            if (enumType is null || !enumType.IsEnum)
            {
                throw new InvalidOperationException("离散枚举动画值引用了未加载的枚举类型");
            }

            try
            {
                return Enum.Parse(enumType, EnumValue, ignoreCase: false);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException("离散枚举动画值格式无效", exception);
            }
        }
    }
}

internal readonly struct AnimationValue
{
    public AnimationValueKind Kind { get; }
    public float Float { get; }
    public Vector3 Vector3 { get; }
    public Quaternion Quaternion { get; }
    public object? Discrete { get; }

    private AnimationValue(AnimationValueKind kind, float @float, Vector3 vector3, Quaternion quaternion, object? discrete)
    {
        Kind = kind;
        Float = @float;
        Vector3 = vector3;
        Quaternion = quaternion;
        Discrete = discrete;
    }

    public static AnimationValue From(AnimationValueKind kind, object? value) => kind switch
    {
        AnimationValueKind.Float => FromFloat((float)value!),
        AnimationValueKind.Vector3 => FromVector3((Vector3)value!),
        AnimationValueKind.Quaternion => FromQuaternion((Quaternion)value!),
        AnimationValueKind.Discrete => new AnimationValue(kind, 0f, default, default, value),
        _ => throw new InvalidOperationException("未知动画值种类"),
    };

    public static AnimationValue FromFloat(float value) => new(AnimationValueKind.Float, value, default, default, null);

    public static AnimationValue FromVector3(Vector3 value) => new(AnimationValueKind.Vector3, 0f, value, default, null);

    public static AnimationValue FromQuaternion(Quaternion value) => new(AnimationValueKind.Quaternion, 0f, default, value, null);
}
