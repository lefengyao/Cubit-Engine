using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Cubit.Core.Scene;

namespace Cubit.Animation;

/// <summary>场景树中的时间轴播放器；播放状态只属于节点实例，不使用全局管理器。</summary>
public sealed class AnimationPlayer : Node
{
    private readonly Dictionary<string, AnimationClip> _clips = new(StringComparer.Ordinal);
    private Playback? _current;
    private Playback? _blendFrom;
    private double _blendElapsed;
    private double _blendDuration;
    private int _ownerThreadId;
    private double _speedScale = 1d;
    private IReadOnlyCollection<string> _clipNames = Array.Empty<string>();
    private event Action<string>? _eventTriggered;
    private event Action<string>? _playbackFinished;

    public event Action<string>? EventTriggered
    {
        add
        {
            EnsureMainThread();
            _eventTriggered += value;
        }

        remove
        {
            EnsureMainThread();
            _eventTriggered -= value;
        }
    }

    public event Action<string>? PlaybackFinished
    {
        add
        {
            EnsureMainThread();
            _playbackFinished += value;
        }

        remove
        {
            EnsureMainThread();
            _playbackFinished -= value;
        }
    }

    public IReadOnlyCollection<string> ClipNames
    {
        get
        {
            EnsureMainThread();
            return _clipNames;
        }
    }

    public string? CurrentClipName
    {
        get
        {
            EnsureMainThread();
            return _current?.Name;
        }
    }

    public double CurrentTime
    {
        get
        {
            EnsureMainThread();
            return _current?.Time ?? 0d;
        }
    }

    /// <summary>播放速度；允许负值反向播放，但必须是有限数。</summary>
    public double SpeedScale
    {
        get
        {
            EnsureMainThread();
            return _speedScale;
        }
        set
        {
            EnsureMainThread();
            if (!double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "动画播放速度必须是有限数");
            }

            _speedScale = value;
        }
    }

    public bool IsPlaying
    {
        get
        {
            EnsureMainThread();
            return _current?.Playing == true;
        }
    }

    public void AddClip(string name, AnimationClip clip)
    {
        EnsureMainThread();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("动画片段名称不能为空", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(clip);
        var playbackSnapshot = clip.CreatePlaybackSnapshot();
        if (!_clips.TryAdd(name, playbackSnapshot))
        {
            throw new InvalidOperationException($"动画片段名称重复: {name}");
        }

        _clipNames = Array.AsReadOnly(_clips.Keys.ToArray());
    }

    public void Play(string name, bool restart = true)
    {
        EnsureMainThread();
        if (!_clips.TryGetValue(name, out var clip))
        {
            throw new InvalidOperationException($"找不到动画片段: {name}");
        }

        clip.Validate();
        if (!restart && _current?.Name == name)
        {
            _current.Playing = true;
            return;
        }

        _blendFrom = null;
        _current = ResolvePlayback(name, clip);
        _current.Time = SpeedScale < 0d ? clip.Length : 0d;
        _current.Playing = true;
    }

    /// <summary>在两个片段之间淡化；目标轨道相同的属性使用对应类型插值。</summary>
    public void CrossFade(string name, double duration)
    {
        EnsureMainThread();
        if (!double.IsFinite(duration) || duration < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var previous = _current;
        Play(name);
        if (previous is not null && duration > 0d)
        {
            _blendFrom = previous;
            _blendElapsed = 0d;
            _blendDuration = duration;
        }
    }

    public void Pause()
    {
        EnsureMainThread();
        if (_current is not null)
        {
            _current.Playing = false;
        }
    }

    public void Stop()
    {
        EnsureMainThread();
        if (_current is not null)
        {
            _current.Playing = false;
            _current.Time = SpeedScale < 0d ? _current.Clip.Length : 0d;
        }

        _blendFrom = null;
    }

    public void Seek(double time)
    {
        EnsureMainThread();
        if (_current is null)
        {
            throw new InvalidOperationException("没有正在选择的动画片段");
        }

        if (!double.IsFinite(time) || time < 0d || time > _current.Clip.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }

        _current.Time = time;
        ApplyCurrent();
    }

    protected override void EnterTree()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    protected override void Process(double delta)
    {
        EnsureMainThread();
        if (_current?.Playing != true)
        {
            return;
        }

        if (!double.IsFinite(delta) || delta < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "动画帧时间必须是有限非负数");
        }

        var scaledDelta = delta * _speedScale;
        if (!double.IsFinite(scaledDelta))
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "动画帧时间与播放速度的乘积必须是有限数");
        }

        Advance(scaledDelta);
        if (_blendFrom is not null)
        {
            _blendElapsed += delta;
        }

        ApplyCurrent();
        if (_blendFrom is not null && _blendElapsed >= _blendDuration)
        {
            _blendFrom = null;
        }
    }

    private void Advance(double delta)
    {
        if (_current is null || delta == 0d)
        {
            return;
        }

        var length = _current.Clip.Length;
        var remaining = Math.Abs(delta);
        var forward = delta > 0d;
        if (_current.Clip.Loop && remaining > length * 1024d)
        {
            remaining %= length;
        }

        while (remaining > 0d && _current.Playing)
        {
            if (forward)
            {
                var available = length - _current.Time;
                var step = Math.Min(remaining, available);
                var previous = _current.Time;
                _current.Time += step;
                EmitForward(previous, _current.Time);
                remaining -= step;
                if (remaining <= 0d)
                {
                    if (_current.Time >= length && !_current.Clip.Loop)
                    {
                        FinishCurrent();
                    }
                    break;
                }

                if (!_current.Clip.Loop)
                {
                    FinishCurrent();
                    break;
                }

                _current.Time = 0d;
            }
            else
            {
                var available = _current.Time;
                var step = Math.Min(remaining, available);
                var previous = _current.Time;
                _current.Time -= step;
                EmitReverse(previous, _current.Time);
                remaining -= step;
                if (remaining <= 0d)
                {
                    if (_current.Time <= 0d && !_current.Clip.Loop)
                    {
                        FinishCurrent();
                    }
                    break;
                }

                if (!_current.Clip.Loop)
                {
                    FinishCurrent();
                    break;
                }

                _current.Time = length;
            }
        }
    }

    private void FinishCurrent()
    {
        if (_current is null)
        {
            return;
        }

        _current.Playing = false;
        _playbackFinished?.Invoke(_current.Name);
    }

    private void EmitForward(double from, double to)
    {
        if (_current is null)
        {
            return;
        }

        for (var index = 0; index < _current.Clip.Events.Count; index++)
        {
            var marker = _current.Clip.Events[index];
            if (marker.Time > from && marker.Time <= to)
            {
                _eventTriggered?.Invoke(marker.Name);
            }
        }
    }

    private void EmitReverse(double from, double to)
    {
        if (_current is null)
        {
            return;
        }

        for (var index = _current.Clip.Events.Count - 1; index >= 0; index--)
        {
            var marker = _current.Clip.Events[index];
            if (marker.Time < from && marker.Time >= to)
            {
                _eventTriggered?.Invoke(marker.Name);
            }
        }
    }

    private void ApplyCurrent()
    {
        if (_current is null)
        {
            return;
        }

        var blendAmount = _blendFrom is null || _blendDuration <= 0d
            ? 1f
            : Math.Clamp((float)(_blendElapsed / _blendDuration), 0f, 1f);
        foreach (var track in _current.Tracks)
        {
            if (track.Target.Tree != Tree)
            {
                _current.Playing = false;
                throw new InvalidOperationException($"动画目标节点已离开场景树: {track.Track.TargetPath}");
            }

            if (!track.Track.TrySample(_current.Time, out var currentValue))
            {
                continue;
            }

            if (_blendFrom is not null && blendAmount < 1f &&
                TryFindMatchingTrack(_blendFrom, track, out var previousTrack) &&
                previousTrack.Track.TrySample(_blendFrom.Time, out var previousValue) &&
                previousValue.Kind == currentValue.Kind)
            {
                track.Apply(AnimationValueBlend.Lerp(previousValue, currentValue, blendAmount));
            }
            else
            {
                track.Apply(currentValue);
            }
        }
    }

    private static bool TryFindMatchingTrack(Playback playback, ResolvedTrack target, out ResolvedTrack match)
    {
        foreach (var candidate in playback.Tracks)
        {
            if (candidate.Track.TargetPath == target.Track.TargetPath &&
                candidate.Track.PropertyName.Equals(target.Track.PropertyName, StringComparison.Ordinal))
            {
                match = candidate;
                return true;
            }
        }

        match = null!;
        return false;
    }

    private Playback ResolvePlayback(string name, AnimationClip clip)
    {
        var tracks = new ResolvedTrack[clip.Tracks.Count];
        for (var i = 0; i < clip.Tracks.Count; i++)
        {
            var track = clip.Tracks[i];
            var target = GetNode(track.TargetPath)
                ?? throw new InvalidOperationException($"动画目标节点不存在: {track.TargetPath}");
            tracks[i] = ResolvedTrack.Create(target, track);
        }

        return new Playback(name, clip, tracks);
    }

    private void EnsureMainThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == 0)
        {
            _ownerThreadId = current;
        }
        else if (_ownerThreadId != current)
        {
            throw new InvalidOperationException("AnimationPlayer 只能在拥有它的主线程访问");
        }
    }

    private sealed class Playback(string name, AnimationClip clip, ResolvedTrack[] tracks)
    {
        public string Name { get; } = name;
        public AnimationClip Clip { get; } = clip;
        public ResolvedTrack[] Tracks { get; } = tracks;
        public double Time { get; set; }
        public bool Playing { get; set; }
    }

    private sealed class ResolvedTrack
    {
        private readonly Action<float>? _floatSetter;
        private readonly Action<Vector3>? _vectorSetter;
        private readonly Action<Quaternion>? _quaternionSetter;
        private readonly Action<object?>? _discreteSetter;

        private ResolvedTrack(AnimationTrack track, Action<float>? floatSetter, Action<Vector3>? vectorSetter,
            Action<Quaternion>? quaternionSetter, Action<object?>? discreteSetter, Node target)
        {
            Track = track;
            Target = target;
            _floatSetter = floatSetter;
            _vectorSetter = vectorSetter;
            _quaternionSetter = quaternionSetter;
            _discreteSetter = discreteSetter;
        }

        public AnimationTrack Track { get; }

        public Node Target { get; }

        public static ResolvedTrack Create(Node target, AnimationTrack track)
        {
            var property = target.GetType().GetProperty(track.PropertyName,
                BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"动画目标属性不存在: {target.GetType().Name}.{track.PropertyName}");
            if (property.SetMethod is null || !property.SetMethod.IsPublic)
            {
                throw new InvalidOperationException($"动画目标属性不可写: {track.PropertyName}");
            }

            return track.Kind switch
            {
                AnimationValueKind.Float when property.PropertyType == typeof(float) =>
                    new ResolvedTrack(track, BuildSetter<float>(target, property), null, null, null, target),
                AnimationValueKind.Vector3 when property.PropertyType == typeof(Vector3) =>
                    new ResolvedTrack(track, null, BuildSetter<Vector3>(target, property), null, null, target),
                AnimationValueKind.Quaternion when property.PropertyType == typeof(Quaternion) =>
                    new ResolvedTrack(track, null, null, BuildSetter<Quaternion>(target, property), null, target),
                AnimationValueKind.Discrete when CanAssignDiscreteValues(track, property.PropertyType) =>
                    new ResolvedTrack(track, null, null, null, BuildObjectSetter(target, property), target),
                _ => throw new InvalidOperationException($"动画轨道值种类与属性类型不匹配: {track.PropertyName}"),
            };
        }

        public void Apply(AnimationValue value)
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
                case AnimationValueKind.Discrete:
                    _discreteSetter?.Invoke(value.Discrete);
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

        private static Action<object?> BuildObjectSetter(Node target, PropertyInfo property)
        {
            var value = Expression.Parameter(typeof(object), "value");
            var call = Expression.Call(Expression.Constant(target), property.SetMethod!,
                Expression.Convert(value, property.PropertyType));
            return Expression.Lambda<Action<object?>>(call, value).Compile();
        }

        private static bool CanAssignDiscreteValues(AnimationTrack track, Type propertyType)
        {
            if (propertyType != typeof(string) && propertyType != typeof(bool) &&
                propertyType != typeof(int) && propertyType != typeof(long) && !propertyType.IsEnum)
            {
                return false;
            }

            foreach (var key in track.Keys)
            {
                if (key.Value is null)
                {
                    if (propertyType != typeof(string))
                    {
                        return false;
                    }

                    continue;
                }

                if (key.Value.GetType() != propertyType)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

internal static class AnimationValueBlend
{
    public static AnimationValue Lerp(AnimationValue from, AnimationValue to, float amount) => from.Kind switch
    {
        AnimationValueKind.Float => AnimationValue.FromFloat(float.Lerp(from.Float, to.Float, amount)),
        AnimationValueKind.Vector3 => AnimationValue.FromVector3(Vector3.Lerp(from.Vector3, to.Vector3, amount)),
        AnimationValueKind.Quaternion => AnimationValue.FromQuaternion(Quaternion.Slerp(from.Quaternion, to.Quaternion, amount)),
        AnimationValueKind.Discrete => amount < 1f ? from : to,
        _ => throw new InvalidOperationException("未知动画值种类"),
    };
}
