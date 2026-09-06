using System.Numerics;
using System.Diagnostics;
using Cubit.Core.Diagnostics;
using Cubit.Core.Scene;
using Cubit.Core.Spatial;
using Cubit.Physics.Simulation;
using Cubit.Physics.Shapes;

namespace Cubit.Physics.Scene;

/// <summary>
/// 场景拥有的三维物理世界。第一阶段仅负责主线程登记和宽相位，不执行查询或接触解算。
/// </summary>
public sealed class PhysicsWorld3D : Node
{
    private const float CharacterFloorProbeDistance = 0.02f;
    private const float CharacterContactSkin = 0.005f;
    private readonly Dictionary<CollisionShape3D, ColliderProxy> _colliders = [];
    private readonly List<ColliderProxy> _staticColliders = [];
    private readonly List<ColliderProxy> _dynamicColliders = [];
    private readonly List<PendingCollider> _pending = [];
    private readonly List<PendingCollider> _additions = [];
    private readonly List<CollisionShape3D> _removals = [];
    private readonly HashSet<CollisionShape3D> _seenShapes = [];
    private readonly List<StaticBvhQueryHit<ColliderProxy>> _staticQueryHits = [];
    private readonly List<SpatialQueryHit<ColliderProxy>> _dynamicQueryHits = [];
    private readonly List<ColliderProxy> _queryCandidates = [];
    private readonly List<CharacterMoveCommand> _characterMoveCommands = [];
    private readonly List<RigidBody3D> _rigidBodies = [];
    private readonly List<RigidForceCommand> _rigidForceCommands = [];
    private readonly List<RigidImpulseCommand> _rigidImpulseCommands = [];
    private readonly List<PhysicsContact> _contacts = [];
    private readonly HashSet<RigidBody3D> _contactingRigidBodies = [];
    private readonly Dictionary<RigidBody3D, float> _rigidContactDrifts = [];
    private readonly PhysicsSolver _physicsSolver = new();
    private readonly List<AreaOverlapState> _activeAreaOverlaps = [];
    private readonly List<AreaOverlapState> _currentAreaOverlaps = [];
    private readonly List<PhysicsAreaEvent> _areaEvents = [];
    private readonly HashSet<string> _reportedDiagnostics = new(StringComparer.Ordinal);
    private StaticBvh<ColliderProxy> _staticBvh = new([]);
    private DynamicAabbTree<ColliderProxy>? _dynamicTree;
    private Vector3 _gravity = new(0f, -9.8f, 0f);
    private int _ownerThreadId;
    private long _nextColliderId = 1;

    /// <summary>当前已登记的形状数量。</summary>
    public int ColliderCount => _colliders.Count;

    /// <summary>最近一次碰撞代理重整耗时，供性能诊断读取。</summary>
    public double LastReconcileMilliseconds { get; private set; }

    /// <summary>最近一次静态 BVH 重建耗时，供性能诊断读取。</summary>
    public double LastStaticBvhBuildMilliseconds { get; private set; }

    /// <summary>层级错误和非法静态变换的稳定诊断。</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>施加给所有已唤醒刚体的世界重力。</summary>
    public Vector3 Gravity
    {
        get => _gravity;
        set
        {
            EnsureMainThread();
            if (!IsFinite(value))
            {
                throw new ArgumentException("重力必须为有限向量", nameof(value));
            }

            _gravity = value;
        }
    }

    /// <summary>双方的层与掩码必须同时同意才允许碰撞。</summary>
    public bool CanCollide(uint leftLayer, uint leftMask, uint rightLayer, uint rightMask) =>
        (leftLayer & rightMask) != 0 && (rightLayer & leftMask) != 0;

    /// <summary>由 CharacterBody3D 在主线程排入下一个固定步的运动命令。</summary>
    internal void QueueCharacterMove(CharacterBody3D character, in Vector3 velocity)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(character);
        if (!IsFinite(velocity))
        {
            throw new ArgumentException("角色速度必须为有限向量", nameof(velocity));
        }

        _characterMoveCommands.Add(new CharacterMoveCommand(character, velocity));
    }

    /// <summary>
    /// 由 ECS/数据驱动调用方在固定步主线程即时执行角色运动。
    /// 节点只提供作者化碰撞形状与过滤配置，位置/速度由调用方状态拥有；不会进入旧的 Node 命令队列。
    /// </summary>
    public void MoveCharacterImmediate(CharacterBody3D character, ref CharacterMotionState state, float delta)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(character);
        state.Validate();
        if (!float.IsFinite(delta) || delta < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "角色运动 delta 必须是有限非负数");
        }

        _dynamicTree ??= new DynamicAabbTree<ColliderProxy>();
        ReconcileColliders();
        if (character.World != this)
        {
            throw new InvalidOperationException("角色不属于当前 PhysicsWorld3D");
        }

        character.Position = state.Position;
        character.Velocity = state.Velocity;
        character.IsOnFloor = state.IsOnFloor;
        MoveCharacter(character, state.Velocity * delta);
        RefreshDynamicColliders(character);

        state.Position = character.Position;
        state.Velocity = character.Velocity;
        state.IsOnFloor = character.IsOnFloor;
        state.LastSlideCount = character.LastSlideCount;
    }

    /// <summary>由刚体在主线程排入下一固定步的持续力。</summary>
    internal void QueueForce(RigidBody3D body, in Vector3 force)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(body);
        if (!IsFinite(force))
        {
            throw new ArgumentException("刚体力必须为有限向量", nameof(force));
        }

        _rigidForceCommands.Add(new RigidForceCommand(body, force));
    }

    /// <summary>由刚体在主线程排入下一固定步的瞬时冲量。</summary>
    internal void QueueImpulse(RigidBody3D body, in Vector3 impulse)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(body);
        if (!IsFinite(impulse))
        {
            throw new ArgumentException("刚体冲量必须为有限向量", nameof(impulse));
        }

        _rigidImpulseCommands.Add(new RigidImpulseCommand(body, impulse));
    }

    /// <summary>追加最大距离内的精确射线命中，返回本次追加数量。</summary>
    public int Raycast(
        in Vector3 origin,
        in Vector3 direction,
        float maxDistance,
        in PhysicsQueryFilter filter,
        List<PhysicsRayHit3D> results)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(results);
        var unitDirection = ValidateAndNormalizeDirection(origin, direction, maxDistance);
        var end = origin + unitDirection * maxDistance;
        var queryBounds = new Aabb3(Vector3.Min(origin, end), Vector3.Max(origin, end));
        CollectQueryCandidates(queryBounds);

        var resultStart = results.Count;
        foreach (var proxy in _queryCandidates)
        {
            if (!MatchesFilter(proxy, filter) || proxy.CollisionShape.Shape is not { } shape ||
                !ShapeGeometry.Raycast(shape, GetSimulationTransform(proxy), origin, unitDirection, maxDistance, out var hit))
            {
                continue;
            }

            results.Add(new PhysicsRayHit3D(
                proxy.CollisionObject,
                proxy.CollisionShape,
                hit.Position,
                hit.Normal,
                hit.Distance,
                proxy.ColliderId));
        }

        results.Sort(resultStart, results.Count - resultStart, RayHitComparer.Instance);
        return results.Count - resultStart;
    }

    /// <summary>追加与给定形状精确重叠的碰撞体，返回本次追加数量。</summary>
    public int Overlap(
        Shape3D shape,
        in Transform3D transform,
        in PhysicsQueryFilter filter,
        List<PhysicsOverlapHit3D> results)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(results);
        if (!Transform3D.IsValid(transform))
        {
            throw new ArgumentException("查询变换必须是有效 Transform3D", nameof(transform));
        }

        var queryBounds = GetWorldBounds(shape, transform);
        CollectQueryCandidates(queryBounds);

        var resultStart = results.Count;
        foreach (var proxy in _queryCandidates)
        {
            if (!MatchesFilter(proxy, filter) || proxy.CollisionShape.Shape is not { } candidateShape ||
                !ShapeGeometry.Overlaps(shape, transform, candidateShape, GetSimulationTransform(proxy)))
            {
                continue;
            }

            results.Add(new PhysicsOverlapHit3D(proxy.CollisionObject, proxy.CollisionShape, proxy.ColliderId));
        }

        results.Sort(resultStart, results.Count - resultStart, OverlapHitComparer.Instance);
        return results.Count - resultStart;
    }

    protected override void PostPhysicsProcess(double delta)
    {
        EnsureMainThread();
        if (HasPhysicsWorldAncestor())
        {
            ReportOnce("physics.world.nested", this, "物理世界不能嵌套在另一个物理世界中");
            ClearColliders();
            return;
        }

        _dynamicTree ??= new DynamicAabbTree<ColliderProxy>();
        ReconcileColliders();
        ProcessCharacterMoves((float)delta);
        ProcessRigidBodies((float)delta);
        RefreshDynamicColliders();
        SolveRigidContacts((float)delta);
        RefreshDynamicColliders();
        DetectAreaTransitions();
    }

    private void ProcessCharacterMoves(float delta)
    {
        if (_characterMoveCommands.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var command in _characterMoveCommands)
            {
                if (command.Character.World != this)
                {
                    continue;
                }

                MoveCharacter(command.Character, command.Velocity * delta);
            }
        }
        finally
        {
            _characterMoveCommands.Clear();
        }
    }

    private void ProcessRigidBodies(float delta)
    {
        try
        {
            foreach (var command in _rigidForceCommands)
            {
                if (command.Body.World != this)
                {
                    continue;
                }

                command.Body.Wake();
                command.Body.LinearVelocity += command.Force * command.Body.InverseMass * delta;
            }

            foreach (var command in _rigidImpulseCommands)
            {
                if (command.Body.World != this)
                {
                    continue;
                }

                command.Body.Wake();
                command.Body.LinearVelocity += command.Impulse * command.Body.InverseMass;
            }

            foreach (var body in _rigidBodies)
            {
                if (body.World != this || body.IsSleeping)
                {
                    continue;
                }

                body.LinearVelocity += Gravity * delta;
                ApplyWorldDisplacement(body, body.LinearVelocity * delta);
            }
        }
        finally
        {
            _rigidForceCommands.Clear();
            _rigidImpulseCommands.Clear();
        }
    }

    /// <summary>
    /// 从宽相位候选中构造精确刚体接触，再以固定顺序交给顺序冲量求解器。
    /// 每对动态刚体只由较小 ColliderId 一侧登记，避免重复解算。
    /// </summary>
    private void SolveRigidContacts(float delta)
    {
        _contacts.Clear();
        _contactingRigidBodies.Clear();
        _rigidContactDrifts.Clear();
        foreach (var firstProxy in _dynamicColliders)
        {
            if (firstProxy.CollisionObject is not RigidBody3D firstBody ||
                firstProxy.CollisionShape.Shape is not { } firstShape)
            {
                continue;
            }

            CollectQueryCandidates(firstProxy.Bounds);
            foreach (var secondProxy in _queryCandidates)
            {
                if (ReferenceEquals(firstProxy.CollisionObject, secondProxy.CollisionObject) ||
                    secondProxy.CollisionShape.Shape is not { } secondShape ||
                    (secondProxy.CollisionObject is not StaticBody3D && secondProxy.CollisionObject is not RigidBody3D) ||
                    (secondProxy.CollisionObject is RigidBody3D && firstProxy.ColliderId >= secondProxy.ColliderId) ||
                    !CanCollide(
                        firstBody.CollisionLayer,
                        firstBody.CollisionMask,
                        secondProxy.CollisionObject.CollisionLayer,
                        secondProxy.CollisionObject.CollisionMask) ||
                    !ShapeGeometry.TryGetContact(
                        firstShape,
                        GetSimulationTransform(firstProxy),
                        secondShape,
                        GetSimulationTransform(secondProxy),
                        out var shapeContact))
                {
                    continue;
                }

                _contacts.Add(new PhysicsContact(
                    firstBody,
                    secondProxy.CollisionObject,
                    shapeContact.Position,
                    shapeContact.Normal,
                    shapeContact.Penetration,
                    firstProxy.ColliderId,
                    secondProxy.ColliderId,
                    shapeContact.FeatureId));
                _contactingRigidBodies.Add(firstBody);
                if (secondProxy.CollisionObject is RigidBody3D secondBody)
                {
                    _contactingRigidBodies.Add(secondBody);
                }
            }
        }

        _contacts.Sort(PhysicsContactComparer.Instance);
        if (_contacts.Count > 0)
        {
            _physicsSolver.Solve(this, _contacts, delta);
        }

        UpdateRigidSleepStates();
    }

    /// <summary>供求解器回写位置校正；变换与动态树仍只在物理世界主线程修改。</summary>
    internal void ApplySolverDisplacement(RigidBody3D body, in Vector3 worldDisplacement)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.World != this)
        {
            throw new InvalidOperationException("刚体不属于当前物理世界");
        }

        var drift = worldDisplacement.Length();
        if (!float.IsFinite(drift))
        {
            throw new InvalidOperationException("接触位置校正必须为有限向量");
        }

        if (_rigidContactDrifts.TryGetValue(body, out var previousDrift))
        {
            _rigidContactDrifts[body] = MathF.Max(previousDrift, drift);
        }
        else
        {
            _rigidContactDrifts.Add(body, drift);
        }

        ApplyWorldDisplacement(body, worldDisplacement);
    }

    private void UpdateRigidSleepStates()
    {
        const int stableContactTickLimit = 30;
        const float sleepSpeedSquared = 0.01f * 0.01f;
        const float sleepDriftLimit = 0.001f;
        foreach (var body in _rigidBodies)
        {
            if (body.World != this)
            {
                continue;
            }

            var speedSquared = body.LinearVelocity.LengthSquared();
            var drift = _rigidContactDrifts.GetValueOrDefault(body);
            var hasStableContact = _contactingRigidBodies.Contains(body) &&
                float.IsFinite(speedSquared) && speedSquared <= sleepSpeedSquared && drift <= sleepDriftLimit;
            if (body.IsSleeping)
            {
                if (!hasStableContact)
                {
                    body.Wake();
                }

                continue;
            }

            if (!hasStableContact)
            {
                body.RestingTickCount = 0;
                continue;
            }

            body.RestingTickCount++;
            if (body.RestingTickCount >= stableContactTickLimit)
            {
                body.Sleep();
            }
        }
    }

    private void MoveCharacter(CharacterBody3D character, in Vector3 motion)
    {
        // BeginMove 会清除本步状态；跨步条件必须保留移动前的地面状态，
        // 不能因为下落阶段的圆角接触临时写入 IsOnFloor 就把空中角色抬过方块。
        var wasOnFloor = character.IsOnFloor;
        character.BeginMove();

        if (motion.LengthSquared() <= 1e-12f)
        {
            ProbeCharacterFloor(character);
            return;
        }

        if (!TryGetCharacterCollider(character, out var characterProxy) ||
            characterProxy.CollisionShape.Shape is not { } characterShape)
        {
            return;
        }

        // 首次水平移动可能还没有上一固定步的落地标记；水平移动时也要
        // 重新确认支撑面，避免旧的 IsOnFloor 把角色留在已经离开的边缘。
        // 向上跳跃不做这次探测，保持跳跃阶段的地面状态语义。
        if (motion.Y >= -1e-12f && (!wasOnFloor || MathF.Abs(motion.Y) <= 1e-12f))
        {
            ProbeCharacterFloor(character);
        }

        var canStepUp = wasOnFloor || character.IsOnFloor;

        // 体素角色按轴顺序解算：先处理垂直轴，再分别处理 X/Z 水平轴。
        // 这样两个方块棱角的接触不会合成一个同时阻断 X/Z 的斜法线，
        // 与 Minecraft 的 axisStepOrder 碰撞路径一致。
        var horizontalMotion = new Vector3(motion.X, 0f, motion.Z);
        if (horizontalMotion.LengthSquared() > 1e-12f)
        {
            if (MathF.Abs(motion.Y) > 1e-12f)
            {
                MoveCharacterMotion(
                    character,
                    characterProxy,
                    characterShape,
                    Vector3.UnitY * motion.Y,
                    new Vector3(0f, character.Velocity.Y, 0f),
                    horizontalOnly: false,
                    preserveHorizontalVelocity: false,
                    allowStepUp: false);
            }

            if (MathF.Abs(horizontalMotion.X) > 1e-12f)
            {
                MoveCharacterMotion(
                    character,
                    characterProxy,
                    characterShape,
                    new Vector3(horizontalMotion.X, 0f, 0f),
                    new Vector3(character.Velocity.X, 0f, 0f),
                    horizontalOnly: true,
                    preserveHorizontalVelocity: true,
                    allowStepUp: canStepUp);
            }

            if (MathF.Abs(horizontalMotion.Z) > 1e-12f)
            {
                MoveCharacterMotion(
                    character,
                    characterProxy,
                    characterShape,
                    new Vector3(0f, 0f, horizontalMotion.Z),
                    new Vector3(0f, 0f, character.Velocity.Z),
                    horizontalOnly: true,
                    preserveHorizontalVelocity: true,
                    allowStepUp: canStepUp);
            }
        }
        else
        {
            MoveCharacterMotion(
                character,
                characterProxy,
                characterShape,
                motion,
                character.Velocity,
                horizontalOnly: false,
                preserveHorizontalVelocity: false,
                allowStepUp: false);
        }

        // 一次移动可能在最大滑动次数耗尽时停在多个接触体的交界处。以水平
        // 约束做有限收尾，优先将角色推出低顶棚，但不把台阶棱的斜向法线
        // 转换为额外的垂直位移，也不改变本帧已经结算过的速度。
        var settleMotion = Vector3.Zero;
        for (var iteration = 0; iteration < CharacterBody3D.MaximumSlideCount; iteration++)
        {
            if (!RecoverCharacterPenetration(
                    character,
                    characterProxy,
                    characterShape,
                    Vector3.Zero,
                    horizontalOnly: true,
                    preserveHorizontalVelocity: true,
                    ref settleMotion))
            {
                break;
            }
        }

        // 横向撞到墙角时，最近一次接触法线可能是水平的，不能因此丢掉脚下的地面状态。
        // 只在水平/下降运动后探测，避免角色上升跳跃时被重新判定为落地。
        if (character.Velocity.Y <= 0f)
        {
            ProbeCharacterFloor(character);
        }
    }

    private void MoveCharacterMotion(
        CharacterBody3D character,
        ColliderProxy characterProxy,
        Shape3D characterShape,
        in Vector3 motion,
        in Vector3 collisionVelocity,
        bool horizontalOnly,
        bool preserveHorizontalVelocity,
        bool allowStepUp)
    {
        if (motion.LengthSquared() <= 1e-12f)
        {
            return;
        }

        var remaining = motion;
        var horizontalXAxisOnly = horizontalOnly &&
            MathF.Abs(motion.X) > 1e-12f && MathF.Abs(motion.Z) <= 1e-12f;
        var horizontalZAxisOnly = horizontalOnly &&
            MathF.Abs(motion.Z) > 1e-12f && MathF.Abs(motion.X) <= 1e-12f;
        for (var iteration = 0; iteration < CharacterBody3D.MaximumSlideCount; iteration++)
        {
            if (remaining.LengthSquared() <= 1e-12f)
            {
                break;
            }

            var shapeTransform = GetSimulationTransform(characterProxy);
            if (RecoverCharacterPenetration(
                    character,
                    characterProxy,
                    characterShape,
                    collisionVelocity,
                    horizontalOnly,
                    preserveHorizontalVelocity,
                    ref remaining,
                    horizontalXAxisOnly,
                    horizontalZAxisOnly))
            {
                continue;
            }

            var endPosition = shapeTransform.Position + remaining;
            if (!IsFinite(endPosition))
            {
                ReportOnce(
                    "physics.character.motion-limit",
                    character,
                    "character motion produces a non-finite end position");
                return;
            }

            var endTransform = shapeTransform.WithPosition(endPosition);
            CollectQueryCandidates(Aabb3.Combine(
                GetWorldBounds(characterShape, shapeTransform),
                GetWorldBounds(characterShape, endTransform)));
            SortQueryCandidatesByColliderId();
            if (!ValidateCharacterMotionBudget(character, characterShape, shapeTransform, remaining))
            {
                return;
            }

            var found = false;
            var unresolvedEncounter = false;
            var nearest = default(ShapeSweepHit);
            ColliderProxy? nearestProxy = null;
            foreach (var candidate in _queryCandidates)
            {
                if (ReferenceEquals(candidate.CollisionObject, character) || candidate.CollisionObject is Area3D ||
                    candidate.CollisionShape.Shape is not { } candidateShape ||
                    !CanCollide(
                        character.CollisionLayer,
                        character.CollisionMask,
                        candidate.CollisionObject.CollisionLayer,
                        candidate.CollisionObject.CollisionMask))
                {
                    continue;
                }

                var candidateTransform = GetSimulationTransform(candidate);
                if (ShouldSkipDescendingFloorEdge(
                        character,
                        characterShape,
                        shapeTransform,
                        candidateShape,
                        candidateTransform,
                        horizontalOnly))
                {
                    continue;
                }

                if (!ShapeGeometry.Sweep(
                        characterShape,
                        shapeTransform,
                        remaining,
                        candidateShape,
                        candidateTransform,
                        out var sweep))
                {
                    unresolvedEncounter |= ShapeGeometry.MaySweepEncounter(
                        characterShape,
                        shapeTransform,
                        remaining,
                        candidateShape,
                        candidateTransform);
                    continue;
                }

                if (ShouldSkipDescendingCorner(
                        character,
                        characterShape,
                        shapeTransform,
                        candidateShape,
                        candidateTransform,
                        remaining,
                        sweep.Normal,
                        horizontalOnly))
                {
                    // 轴对齐体素盒的外棱只与胶囊圆角擦碰时，斜法线不是
                    // Minecraft 式的顶面支撑。纯竖直下落必须继续，否则
                    // 会先被抬回一小段、再在下一 tick 继续下落，形成缓降。
                    continue;
                }

                if (!found || sweep.Fraction < nearest.Fraction ||
                    (sweep.Fraction == nearest.Fraction && candidate.ColliderId < nearestProxy!.ColliderId))
                {
                    found = true;
                    nearest = sweep;
                    nearestProxy = candidate;
                }
            }

            // 精确窄相位已找到碰撞时，必须优先按最近命中滑动。另一个
            // 宽相位候选的包络球假阳性不能覆盖真实接触并冻结角色。
            if (unresolvedEncounter && !found)
            {
                var isUnresolvedVerticalAscent = motion.Y > 0f &&
                    MathF.Abs(motion.X) <= 1e-12f &&
                    MathF.Abs(motion.Z) <= 1e-12f;
                if (isUnresolvedVerticalAscent)
                {
                    // 胶囊贴着台阶角点纯向上移动时，采样扫掠可能只看到
                    // 切线接触而无法收敛；向上没有天花板穿透风险，完成本
                    // 次上升即可在后续水平扫掠中脱离角点。
                    ApplyWorldDisplacement(character, remaining);
                    break;
                }

                var isUnresolvedEdgeDescent = motion.Y < -1e-12f &&
                    (motion.X * motion.X + motion.Z * motion.Z) > 1e-12f;
                if (isUnresolvedEdgeDescent && !found)
                {
                    // 已在固定采样预算内完成精确窄相位而未命中时，包络球
                    // 只会在离开方块顶边的下降路径产生宽相位假阳性。冻结
                    // 这段位移会让角色拥有重力速度却悬停在空洞边缘；允许
                    // 完整下降，纯水平擦碰与任何真实命中仍走保守诊断/解算。
                    ApplyWorldDisplacement(character, remaining);
                    break;
                }

                ReportOnce(
                    "physics.character.ambiguous-sweep",
                    character,
                    "character motion has an unresolved bounded sweep encounter");
                return;
            }

            if (!found || nearestProxy is null)
            {
                ApplyWorldDisplacement(character, remaining);
                break;
            }

            if (allowStepUp && TryStepCharacterUp(
                    character,
                    characterProxy,
                    characterShape,
                    shapeTransform,
                    remaining,
                    nearestProxy,
                    nearest))
            {
                continue;
            }

            // 纯向上子扫掠遇到胶囊圆头与方块上角的接触时，法线会带少量向上分量。
            // 这不是天花板，继续完成上升可以让角色脱离角点；向下法线仍按正常逻辑
            // 阻挡跳跃，避免穿过真正的顶面。
            var isVerticalAscent = motion.Y > 0f &&
                MathF.Abs(motion.X) <= 1e-12f &&
                MathF.Abs(motion.Z) <= 1e-12f;
            if (isVerticalAscent && nearest.Normal.Y >= 0f)
            {
                ApplyWorldDisplacement(character, remaining + nearest.Normal * CharacterContactSkin);
                character.AddSlide(new CharacterSlideCollision(
                    nearestProxy.CollisionObject,
                    nearestProxy.CollisionShape,
                    nearest.Position,
                    nearest.Normal,
                    nearestProxy.ColliderId));
                break;
            }

            var travel = remaining * nearest.Fraction;
            var separation = nearest.Normal * CharacterContactSkin;
            if (horizontalXAxisOnly)
            {
                separation.Z = 0f;
            }
            else if (horizontalZAxisOnly)
            {
                separation.X = 0f;
            }

            ApplyWorldDisplacement(character, travel + separation);
            character.AddSlide(new CharacterSlideCollision(
                nearestProxy.CollisionObject,
                nearestProxy.CollisionShape,
                nearest.Position,
                nearest.Normal,
                nearestProxy.ColliderId));
            if (nearestProxy.CollisionShape.Shape is { } nearestShape &&
                IsFloorContact(
                    character,
                    nearestShape,
                    GetSimulationTransform(nearestProxy),
                    nearest.Normal))
            {
                character.IsOnFloor = true;
            }

            remaining *= 1f - nearest.Fraction;
            remaining -= nearest.Normal * Vector3.Dot(remaining, nearest.Normal);
            if (horizontalOnly)
            {
                // 水平子扫掠只负责 XZ 平面。圆角/斜面法线的投影不能把剩余
                // 水平位移转换成下沉，否则角色会在台阶上角反复重扫。
                remaining.Y = 0f;
                // 分轴路径不能凭斜向接触法线凭空生成另一个水平轴的位移。
                // Minecraft 的 axisStepOrder 对每个轴只提交该轴的碰撞结果；
                // 保留这里的约束可避免 X 扫掠在圆角处偷偷写入 Z（反之亦然），
                // 从而造成经过棱角时的额外减速和横向漂移。
                if (horizontalXAxisOnly)
                {
                    remaining.Z = 0f;
                }
                else if (horizontalZAxisOnly)
                {
                    remaining.X = 0f;
                }
            }
            ApplyCharacterVelocityCollision(character, nearest.Normal, collisionVelocity, preserveHorizontalVelocity);
        }
    }

    /// <summary>
    /// 跳跃中，胶囊下半球会先擦到体素顶棱。普通滑动会把水平位移投影成极小切线，
    /// 因此仅对直立胶囊和直立盒做受限上抬：必须能完整越过障碍且没有顶棚碰撞。
    /// </summary>
    private bool TryStepCharacterUp(
        CharacterBody3D character,
        ColliderProxy characterProxy,
        Shape3D characterShape,
        in Transform3D characterTransform,
        in Vector3 horizontalMotion,
        ColliderProxy obstacleProxy,
        in ShapeSweepHit obstacleHit)
    {
        if (character.MaxStepHeight <= 0f ||
            character.UpDirection != Vector3.UnitY ||
            characterShape is not CapsuleShape3D capsule ||
            obstacleProxy.CollisionShape.Shape is not BoxShape3D box)
        {
            return false;
        }

        // MC 的跨步候选只从水平轴碰撞进入。胶囊圆角擦过低台外棱时，
        // 扫掠法线会带较大的向上分量；这已经是地面/角点接触，不能再
        // 将同一接触抬高一次，否则角色离开低台时会被“缓降”回台面。
        if (!float.IsFinite(obstacleHit.Normal.Y) ||
            obstacleHit.Normal.Y >= character.FloorMaxAngleCosine)
        {
            return false;
        }

        var obstacleTransform = GetSimulationTransform(obstacleProxy);
        if (MathF.Abs(Quaternion.Dot(characterTransform.Rotation, Quaternion.Identity)) < 0.9999f ||
            MathF.Abs(Quaternion.Dot(obstacleTransform.Rotation, Quaternion.Identity)) < 0.9999f)
        {
            return false;
        }

        var capsuleRadius = capsule.Radius * characterTransform.Scale;
        var capsuleBottomCenter = characterTransform.Position.Y - capsule.CylinderHeight * characterTransform.Scale * 0.5f;
        var obstacleTop = obstacleTransform.Position.Y + box.Size.Y * obstacleTransform.Scale * 0.5f;
        var lift = obstacleTop + capsuleRadius + CharacterContactSkin - capsuleBottomCenter;
        if (!float.IsFinite(lift) || lift <= CharacterContactSkin || lift > character.MaxStepHeight + 1e-6f)
        {
            return false;
        }

        var liftMotion = Vector3.UnitY * lift;
        var liftedTransform = characterTransform.WithPosition(characterTransform.Position + liftMotion);
        var horizontalEndTransform = liftedTransform.WithPosition(liftedTransform.Position + horizontalMotion);
        CollectQueryCandidates(Aabb3.Combine(
            Aabb3.Combine(
                GetWorldBounds(characterShape, characterTransform),
                GetWorldBounds(characterShape, liftedTransform)),
            GetWorldBounds(characterShape, horizontalEndTransform)));
        SortQueryCandidatesByColliderId();

        foreach (var candidate in _queryCandidates)
        {
            if (ReferenceEquals(candidate.CollisionObject, character) || candidate.CollisionObject is Area3D ||
                candidate.CollisionShape.Shape is not { } candidateShape ||
                !CanCollide(
                    character.CollisionLayer,
                    character.CollisionMask,
                    candidate.CollisionObject.CollisionLayer,
                    candidate.CollisionObject.CollisionMask))
            {
                continue;
            }

            var candidateTransform = GetSimulationTransform(candidate);
            if (!ShapeGeometry.IsSweepWithinSampleBudget(
                    characterShape,
                    characterTransform,
                    liftMotion,
                    candidateShape,
                    candidateTransform))
            {
                return false;
            }

            if (ShapeGeometry.Sweep(
                    characterShape,
                    characterTransform,
                    liftMotion,
                    candidateShape,
                    candidateTransform,
                    out var liftHit))
            {
                if (liftHit.Fraction < 1f - 1e-5f && liftHit.Normal.Y < -1e-4f)
                {
                    return false;
                }
            }

            if (!ShapeGeometry.IsSweepWithinSampleBudget(
                    characterShape,
                    liftedTransform,
                    horizontalMotion,
                    candidateShape,
                    candidateTransform) ||
                ShapeGeometry.Overlaps(characterShape, liftedTransform, candidateShape, candidateTransform) ||
                ShapeGeometry.Sweep(
                    characterShape,
                    liftedTransform,
                    horizontalMotion,
                    candidateShape,
                    candidateTransform,
                    out _))
            {
                return false;
            }
        }

        ApplyWorldDisplacement(character, liftMotion);
        return true;
    }

    /// <summary>先处理已经发生的穿透，再进行连续扫掠，避免旧碰撞体替换后角色卡在实体内部。</summary>
    private bool RecoverCharacterPenetration(
        CharacterBody3D character,
        ColliderProxy characterProxy,
        Shape3D characterShape,
        in Vector3 collisionVelocity,
        bool horizontalOnly,
        bool preserveHorizontalVelocity,
        ref Vector3 remaining,
        bool horizontalXAxisOnly = false,
        bool horizontalZAxisOnly = false)
    {
        var shapeTransform = GetSimulationTransform(characterProxy);
        CollectQueryCandidates(GetWorldBounds(characterShape, shapeTransform));
        SortQueryCandidatesByColliderId();
        ColliderProxy? selectedProxy = null;
        var selectedContact = default(ShapeContact);
        foreach (var candidate in _queryCandidates)
        {
            if (ReferenceEquals(candidate.CollisionObject, character) || candidate.CollisionObject is Area3D ||
                candidate.CollisionShape.Shape is not { } candidateShape ||
                !CanCollide(
                    character.CollisionLayer,
                    character.CollisionMask,
                    candidate.CollisionObject.CollisionLayer,
                    candidate.CollisionObject.CollisionMask) ||
                ShouldSkipDescendingFloorEdge(
                    character,
                    characterShape,
                    shapeTransform,
                    candidateShape,
                    GetSimulationTransform(candidate),
                    horizontalOnly) ||
                !ShapeGeometry.Overlaps(
                    characterShape,
                    shapeTransform,
                    candidateShape,
                    GetSimulationTransform(candidate)) ||
                !ShapeGeometry.TryGetContact(
                    characterShape,
                    shapeTransform,
                    candidateShape,
                    GetSimulationTransform(candidate),
                    out var contact) ||
                !float.IsFinite(contact.Penetration) ||
                contact.Penetration <= 1e-6f)
            {
                continue;
            }

            var candidateNormal = contact.Normal;
            if (!IsFinite(candidateNormal) || candidateNormal.LengthSquared() <= 1e-12f)
            {
                continue;
            }

            candidateNormal = Vector3.Normalize(candidateNormal);
            if (ShouldSkipDescendingCorner(
                    character,
                    characterShape,
                    shapeTransform,
                    candidateShape,
                    GetSimulationTransform(candidate),
                    remaining,
                    candidateNormal,
                    horizontalOnly))
            {
                continue;
            }

            // 水平子扫掠在台阶棱和低顶棚同时初始穿透时，先按台阶棱恢复会
            // 刻意丢弃 Y 修正并提前返回，导致顶棚穿透留到下一帧。保持原有
            // ColliderId 的确定性顺序，但优先选择必须向下推出的顶棚接触。
            if (selectedProxy is not null &&
                (!horizontalOnly || candidateNormal.Y >= -1e-4f || selectedContact.Normal.Y < -1e-4f))
            {
                continue;
            }

            selectedProxy = candidate;
            selectedContact = contact with { Normal = candidateNormal };
        }

        if (selectedProxy is null)
        {
            return false;
        }

        var normal = selectedContact.Normal;
        var correction = normal * (selectedContact.Penetration + CharacterContactSkin);
            if (horizontalOnly && normal.X * normal.X + normal.Z * normal.Z > 1e-12f)
            {
                // 水平子扫掠的斜向圆角接触不能把垂直阶段已经获得的高度
                // 推回去；纯 Y 地面接触仍保留完整分离，避免角色嵌入地面。
                correction.Y = 0f;
                if (horizontalXAxisOnly)
                {
                    correction.Z = 0f;
                }
                else if (horizontalZAxisOnly)
                {
                    correction.X = 0f;
                }
            }

            var allowUpwardCorner = !horizontalOnly &&
                collisionVelocity.Y > 0f &&
                normal.Y >= 0f;
            if (allowUpwardCorner)
            {
                // 垂直上升贴着墙面或台阶上角时，穿透恢复不能把本次上升
                // 吞掉；只要不是朝下的天花板法线，就完成剩余上升，下一
                // 次水平子扫掠会在胶囊越过台阶后继续前进。
                ApplyWorldDisplacement(character, correction + remaining);
                remaining = Vector3.Zero;
                character.AddSlide(new CharacterSlideCollision(
                    selectedProxy.CollisionObject,
                    selectedProxy.CollisionShape,
                    selectedContact.Position,
                    normal,
                    selectedProxy.ColliderId));
                return true;
            }

            ApplyWorldDisplacement(character, correction);
            character.AddSlide(new CharacterSlideCollision(
                selectedProxy.CollisionObject,
                selectedProxy.CollisionShape,
                selectedContact.Position,
                normal,
                selectedProxy.ColliderId));
            if (selectedProxy.CollisionShape.Shape is { } selectedShape &&
                IsFloorContact(
                    character,
                    selectedShape,
                    GetSimulationTransform(selectedProxy),
                    normal))
            {
                character.IsOnFloor = true;
            }

            remaining -= normal * Vector3.Dot(remaining, normal);
            if (horizontalOnly)
            {
                remaining.Y = 0f;
            }
            ApplyCharacterVelocityCollision(character, normal, collisionVelocity, preserveHorizontalVelocity);
            return true;
    }

    private static void ApplyCharacterVelocityCollision(
        CharacterBody3D character,
        in Vector3 normal,
        in Vector3 collisionVelocity,
        bool preserveHorizontalVelocity)
    {
        if (preserveHorizontalVelocity)
        {
            // 上升中的水平子扫掠可能先撞到一格台阶的垂直侧面。保留输入的
            // 水平速度，等垂直扫掠把胶囊抬过台阶后，下一固定步即可继续前进。
            return;
        }

        var correction = normal * Vector3.Dot(collisionVelocity, normal);
        // 分轴扫掠只允许碰撞响应修改本次扫掠涉及的速度轴，避免水平侧壁
        // 的对角法线把正在上升的垂直速度错误地放大或清零。
        if (MathF.Abs(collisionVelocity.X) <= 1e-12f)
        {
            correction.X = 0f;
        }

        if (MathF.Abs(collisionVelocity.Y) <= 1e-12f)
        {
            correction.Y = 0f;
        }

        if (MathF.Abs(collisionVelocity.Z) <= 1e-12f)
        {
            correction.Z = 0f;
        }

        character.Velocity -= correction;
    }

    /// <summary>零位移时仍用短扫掠确认脚下地形，避免旧的 IsOnFloor 状态跨越挖空操作。</summary>
    private void ProbeCharacterFloor(CharacterBody3D character)
    {
        // 每次探测都从“未落地”开始；垂直扫掠可能先碰到圆角并暂时写入
        // IsOnFloor，但最终位置离开支撑面时必须撤销该临时状态。
        character.IsOnFloor = false;
        if (!TryGetCharacterCollider(character, out var characterProxy) ||
            characterProxy.CollisionShape.Shape is not { } characterShape)
        {
            return;
        }

        var shapeTransform = GetSimulationTransform(characterProxy);
        var probeMotion = -character.UpDirection * CharacterFloorProbeDistance;
        var endTransform = shapeTransform.WithPosition(shapeTransform.Position + probeMotion);
        CollectQueryCandidates(Aabb3.Combine(
            GetWorldBounds(characterShape, shapeTransform),
            GetWorldBounds(characterShape, endTransform)));
        SortQueryCandidatesByColliderId();

        var foundFloor = false;
        var nearest = default(ShapeSweepHit);
        ColliderProxy? nearestProxy = null;
        foreach (var candidate in _queryCandidates)
        {
            if (ReferenceEquals(candidate.CollisionObject, character) || candidate.CollisionObject is Area3D ||
                candidate.CollisionShape.Shape is not { } candidateShape ||
                !CanCollide(
                    character.CollisionLayer,
                    character.CollisionMask,
                    candidate.CollisionObject.CollisionLayer,
                    candidate.CollisionObject.CollisionMask) ||
                !ShapeGeometry.Sweep(
                    characterShape,
                    shapeTransform,
                    probeMotion,
                    candidateShape,
                    GetSimulationTransform(candidate),
                    out var sweep) ||
                Vector3.Dot(sweep.Normal, character.UpDirection) < character.FloorMaxAngleCosine)
            {
                continue;
            }

            var candidateTransform = GetSimulationTransform(candidate);
            // 体素碰撞由轴对齐盒组成。胶囊圆角擦过盒体外角时，通用扫掠
            // 会得到带向上分量的斜法线；它没有水平支撑面，不能把角色标记
            // 为站在地面上。保留非轴对齐几何的 FloorMaxAngleCosine 规则。
            if (candidateShape is BoxShape3D &&
                IsAxisAligned(candidateTransform) &&
                sweep.Normal.Y < 0.999f)
            {
                continue;
            }

            if (!foundFloor || sweep.Fraction < nearest.Fraction ||
                (sweep.Fraction == nearest.Fraction && candidate.ColliderId < nearestProxy!.ColliderId))
            {
                foundFloor = true;
                nearest = sweep;
                nearestProxy = candidate;
            }
        }

            if (!foundFloor || nearestProxy is null)
            {
                return;
            }

        ApplyWorldDisplacement(character, probeMotion * nearest.Fraction + nearest.Normal * CharacterContactSkin);
        character.IsOnFloor = true;
    }

    private bool TryGetCharacterCollider(CharacterBody3D character, out ColliderProxy proxy)
    {
        foreach (var candidate in _dynamicColliders)
        {
            if (ReferenceEquals(candidate.CollisionObject, character))
            {
                proxy = candidate;
                return true;
            }
        }

        proxy = null!;
        return false;
    }

    private void SortQueryCandidatesByColliderId()
    {
        for (var index = 1; index < _queryCandidates.Count; index++)
        {
            var candidate = _queryCandidates[index];
            var previous = index - 1;
            while (previous >= 0 && _queryCandidates[previous].ColliderId > candidate.ColliderId)
            {
                _queryCandidates[previous + 1] = _queryCandidates[previous];
                previous--;
            }

            _queryCandidates[previous + 1] = candidate;
        }
    }

    private bool ValidateCharacterMotionBudget(
        CharacterBody3D character,
        Shape3D characterShape,
        in Transform3D characterTransform,
        in Vector3 motion)
    {
        foreach (var candidate in _queryCandidates)
        {
            if (ReferenceEquals(candidate.CollisionObject, character) || candidate.CollisionObject is Area3D ||
                candidate.CollisionShape.Shape is not { } candidateShape ||
                !CanCollide(
                    character.CollisionLayer,
                    character.CollisionMask,
                    candidate.CollisionObject.CollisionLayer,
                    candidate.CollisionObject.CollisionMask))
            {
                continue;
            }

            if (!ShapeGeometry.IsSweepWithinSampleBudget(
                    characterShape,
                    characterTransform,
                    motion,
                    candidateShape,
                    GetSimulationTransform(candidate)))
            {
                ReportOnce(
                    "physics.character.motion-limit",
                    character,
                    "character motion exceeds the bounded collision sampling budget");
                return false;
            }
        }

        return true;
    }

    private void ApplyWorldDisplacement(CollisionObject3D collisionObject, in Vector3 worldDisplacement)
    {
        if (collisionObject.Parent is Node3D parent)
        {
            var parentTransform = parent.GlobalTransform;
            var local = Vector3.Transform(worldDisplacement, Quaternion.Conjugate(parentTransform.Rotation)) /
                parentTransform.Scale;
            collisionObject.Position += local;
        }
        else
        {
            collisionObject.Position += worldDisplacement;
        }

        RefreshDynamicColliders(collisionObject);
    }

    private void ReconcileColliders()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
        _pending.Clear();
        _seenShapes.Clear();
        CollectCandidates(this, 0);

        _removals.Clear();
        foreach (var shape in _colliders.Keys)
        {
            if (!_seenShapes.Contains(shape))
            {
                _removals.Add(shape);
            }
        }

        var staticDirty = false;
        foreach (var shape in _removals)
        {
            var proxy = _colliders[shape];
            _colliders.Remove(shape);
            shape.ColliderId = 0;
            if (proxy.CollisionObject.World == this && !_colliders.Values.Any(item => ReferenceEquals(item.CollisionObject, proxy.CollisionObject)))
            {
                proxy.CollisionObject.World = null;
            }

            if (proxy.CollisionObject is RigidBody3D rigidBody && rigidBody.World != this)
            {
                _rigidBodies.Remove(rigidBody);
            }

            if (proxy.CollisionObject is StaticBody3D)
            {
                _staticColliders.Remove(proxy);
                staticDirty = true;
            }
            else if (proxy.DynamicHandle.IsValid)
            {
                _dynamicTree!.Remove(proxy.DynamicHandle);
                _dynamicColliders.Remove(proxy);
            }
        }

        _additions.Clear();
        foreach (var candidate in _pending)
        {
            if (!_colliders.ContainsKey(candidate.Shape))
            {
                _additions.Add(candidate);
            }
        }

        _additions.Sort(static (left, right) =>
        {
            var pathOrder = StringComparer.Ordinal.Compare(left.Shape.GetPath().Value, right.Shape.GetPath().Value);
            return pathOrder != 0 ? pathOrder : left.TraversalOrdinal.CompareTo(right.TraversalOrdinal);
        });
        foreach (var candidate in _additions)
        {
            var bounds = GetWorldBounds(candidate.Shape);
            var proxy = new ColliderProxy(
                _nextColliderId++,
                candidate.CollisionObject,
                candidate.Shape,
                bounds,
                candidate.Shape.GlobalTransform);
            _colliders.Add(candidate.Shape, proxy);
            candidate.Shape.ColliderId = proxy.ColliderId;
            candidate.CollisionObject.World = this;
            if (candidate.CollisionObject is RigidBody3D rigidBody && !_rigidBodies.Contains(rigidBody))
            {
                _rigidBodies.Add(rigidBody);
            }
            if (candidate.CollisionObject is StaticBody3D)
            {
                _staticColliders.Add(proxy);
                staticDirty = true;
            }
            else
            {
                proxy.DynamicHandle = _dynamicTree!.Insert(proxy, bounds);
                _dynamicColliders.Add(proxy);
            }
        }

        foreach (var proxy in _staticColliders)
        {
            if (!SameTransform(proxy.StaticTransform, proxy.CollisionShape.GlobalTransform))
            {
                ReportOnce(
                    "physics.static.transform",
                    proxy.CollisionShape,
                    "静态碰撞体登记后不能改变变换");
            }
        }

        if (staticDirty)
        {
            var bvhStarted = Stopwatch.GetTimestamp();
            var items = new List<StaticBvhItem<ColliderProxy>>(_staticColliders.Count);
            foreach (var proxy in _staticColliders.OrderBy(item => item.ColliderId))
            {
                items.Add(new StaticBvhItem<ColliderProxy>(proxy, proxy.Bounds));
            }

            _staticBvh = new StaticBvh<ColliderProxy>(items);
            LastStaticBvhBuildMilliseconds =
                (Stopwatch.GetTimestamp() - bvhStarted) * 1000d / Stopwatch.Frequency;
        }
        else
        {
            LastStaticBvhBuildMilliseconds = 0d;
        }
        }
        finally
        {
            LastReconcileMilliseconds =
                (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        }
    }

    private void RefreshDynamicColliders()
    {
        foreach (var proxy in _dynamicColliders)
        {
            var bounds = GetWorldBounds(proxy.CollisionShape);
            proxy.Bounds = bounds;
            _dynamicTree!.Update(proxy.DynamicHandle, bounds);
        }
    }

    private void RefreshDynamicColliders(CollisionObject3D collisionObject)
    {
        foreach (var proxy in _dynamicColliders)
        {
            if (!ReferenceEquals(proxy.CollisionObject, collisionObject))
            {
                continue;
            }

            var bounds = GetWorldBounds(proxy.CollisionShape);
            proxy.Bounds = bounds;
            _dynamicTree!.Update(proxy.DynamicHandle, bounds);
        }
    }

    private void DetectAreaTransitions()
    {
        _currentAreaOverlaps.Clear();
        foreach (var areaProxy in _dynamicColliders)
        {
            if (areaProxy.CollisionObject is not Area3D area || areaProxy.CollisionShape.Shape is not { } areaShape)
            {
                continue;
            }

            CollectQueryCandidates(areaProxy.Bounds);
            foreach (var otherProxy in _queryCandidates)
            {
                if (ReferenceEquals(areaProxy.CollisionObject, otherProxy.CollisionObject) ||
                    otherProxy.CollisionObject is Area3D || otherProxy.CollisionShape.Shape is not { } otherShape ||
                    !CanCollide(
                        areaProxy.CollisionObject.CollisionLayer,
                        areaProxy.CollisionObject.CollisionMask,
                        otherProxy.CollisionObject.CollisionLayer,
                        otherProxy.CollisionObject.CollisionMask) ||
                    !ShapeGeometry.Overlaps(
                        areaShape,
                        GetSimulationTransform(areaProxy),
                        otherShape,
                        GetSimulationTransform(otherProxy)))
                {
                    continue;
                }

                _currentAreaOverlaps.Add(new AreaOverlapState(area, areaProxy.ColliderId, otherProxy));
            }
        }

        _currentAreaOverlaps.Sort(AreaOverlapComparer.Instance);
        _areaEvents.Clear();
        var previousIndex = 0;
        var currentIndex = 0;
        while (previousIndex < _activeAreaOverlaps.Count || currentIndex < _currentAreaOverlaps.Count)
        {
            if (previousIndex >= _activeAreaOverlaps.Count)
            {
                AddAreaEvent(_currentAreaOverlaps[currentIndex++], true);
                continue;
            }

            if (currentIndex >= _currentAreaOverlaps.Count)
            {
                AddAreaEvent(_activeAreaOverlaps[previousIndex++], false);
                continue;
            }

            var order = AreaOverlapComparer.Instance.Compare(
                _activeAreaOverlaps[previousIndex],
                _currentAreaOverlaps[currentIndex]);
            if (order == 0)
            {
                previousIndex++;
                currentIndex++;
            }
            else if (order < 0)
            {
                AddAreaEvent(_activeAreaOverlaps[previousIndex++], false);
            }
            else
            {
                AddAreaEvent(_currentAreaOverlaps[currentIndex++], true);
            }
        }

        _activeAreaOverlaps.Clear();
        _activeAreaOverlaps.AddRange(_currentAreaOverlaps);
        _areaEvents.Sort(AreaEventComparer.Instance);
        if (_areaEvents.Count == 0)
        {
            return;
        }

        using var deferredStructuralChanges = Tree?.BeginDeferredStructuralChanges();
        foreach (var areaEvent in _areaEvents)
        {
            if (areaEvent.Entered)
            {
                areaEvent.Area.EmitBodyEntered(areaEvent.Other);
            }
            else
            {
                areaEvent.Area.EmitBodyExited(areaEvent.Other);
            }
        }
    }

    private void AddAreaEvent(in AreaOverlapState overlap, bool entered) =>
        _areaEvents.Add(new PhysicsAreaEvent(
            overlap.Area,
            overlap.Other.CollisionObject,
            entered,
            overlap.AreaColliderId,
            overlap.Other.ColliderId));

    private int CollectCandidates(Node node, int ordinal)
    {
        var children = node.Children;
        for (var childIndex = 0; childIndex < children.Count; childIndex++)
        {
            var child = children[childIndex];
            if (child is PhysicsWorld3D nestedWorld)
            {
                ReportOnce("physics.world.nested", nestedWorld, "物理世界不能嵌套在另一个物理世界中");
                continue;
            }

            if (child is CollisionShape3D shape)
            {
                var collisionObject = FindCollisionObject(shape);
                if (collisionObject is null)
                {
                    ReportOnce("physics.world.missing", shape, "碰撞形状必须位于碰撞对象和物理世界之下");
                }
                else
                {
                    _pending.Add(new PendingCollider(collisionObject, shape, ordinal));
                    _seenShapes.Add(shape);
                    ordinal++;
                }
            }

            ordinal = CollectCandidates(child, ordinal);
        }

        return ordinal;
    }

    private CollisionObject3D? FindCollisionObject(CollisionShape3D shape)
    {
        for (Node? node = shape.Parent; node is not null && !ReferenceEquals(node, this); node = node.Parent)
        {
            if (node is CollisionObject3D collisionObject)
            {
                return collisionObject;
            }
        }

        return null;
    }

    private void CollectQueryCandidates(in Aabb3 bounds)
    {
        _queryCandidates.Clear();
        _staticQueryHits.Clear();
        _staticBvh.Query(bounds, _staticQueryHits);
        foreach (var hit in _staticQueryHits)
        {
            _queryCandidates.Add(hit.Value);
        }

        if (_dynamicTree is null)
        {
            return;
        }

        _dynamicQueryHits.Clear();
        _dynamicTree.Query(bounds, _dynamicQueryHits);
        foreach (var hit in _dynamicQueryHits)
        {
            _queryCandidates.Add(hit.Value);
        }
    }

    private bool MatchesFilter(ColliderProxy proxy, in PhysicsQueryFilter filter)
    {
        var queryLayer = filter.IsConfigured ? filter.CollisionLayer : uint.MaxValue;
        var queryMask = filter.IsConfigured ? filter.CollisionMask : uint.MaxValue;
        return !ReferenceEquals(proxy.CollisionObject, filter.Exclude) &&
            CanCollide(
                queryLayer,
                queryMask,
                proxy.CollisionObject.CollisionLayer,
                proxy.CollisionObject.CollisionMask);
    }

    private static Vector3 ValidateAndNormalizeDirection(in Vector3 origin, in Vector3 direction, float maxDistance)
    {
        if (!IsFinite(origin) || !IsFinite(direction))
        {
            throw new ArgumentException("射线原点和方向必须为有限数值");
        }

        var directionLengthSquared = direction.LengthSquared();
        if (!float.IsFinite(directionLengthSquared) || directionLengthSquared <= 1e-12f)
        {
            throw new ArgumentException("射线方向不能为零", nameof(direction));
        }

        if (!float.IsFinite(maxDistance) || maxDistance < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "射线最大距离必须为非负有限数值");
        }

        return Vector3.Normalize(direction);
    }

    private static Aabb3 GetWorldBounds(CollisionShape3D shape)
    {
        var collisionShape = shape.Shape ?? throw new InvalidOperationException("CollisionShape3D 缺少 Shape 资源");
        return GetWorldBounds(collisionShape, shape.GlobalTransform);
    }

    private static Aabb3 GetWorldBounds(Shape3D shape, in Transform3D transform)
    {
        var local = shape.GetLocalBounds();
        var matrix = transform.Matrix;
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        for (var corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) == 0 ? local.Min.X : local.Max.X,
                (corner & 2) == 0 ? local.Min.Y : local.Max.Y,
                (corner & 4) == 0 ? local.Min.Z : local.Max.Z);
            point = Vector3.Transform(point, matrix);
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }

        return new Aabb3(minimum, maximum);
    }

    private static bool IsAxisAligned(in Transform3D transform) =>
        MathF.Abs(MathF.Abs(transform.Rotation.W) - 1f) <= 1e-5f &&
        MathF.Abs(transform.Rotation.X) <= 1e-5f &&
        MathF.Abs(transform.Rotation.Y) <= 1e-5f &&
        MathF.Abs(transform.Rotation.Z) <= 1e-5f;

    private static bool ShouldSkipDescendingFloorEdge(
        CharacterBody3D character,
        Shape3D characterShape,
        in Transform3D characterTransform,
        Shape3D candidateShape,
        in Transform3D candidateTransform,
        bool horizontalOnly)
    {
        if (!horizontalOnly || character.Velocity.Y >= -1e-5f ||
            characterShape is not CapsuleShape3D capsule ||
            candidateShape is not BoxShape3D box ||
            !IsAxisAligned(characterTransform) ||
            !IsAxisAligned(candidateTransform))
        {
            return false;
        }

        var capsuleBottom = characterTransform.Position.Y -
            capsule.CylinderHeight * characterTransform.Scale * 0.5f -
            capsule.Radius * characterTransform.Scale;
        var candidateTop = candidateTransform.Position.Y + box.Size.Y * candidateTransform.Scale * 0.5f;
        // 低于胶囊底部的盒顶只接触圆角外棱。保留一个探测距离和皮肤，
        // 让下降中的角色沿水平轴越过外棱，下一垂直步继续完整下落。
        return float.IsFinite(capsuleBottom) && float.IsFinite(candidateTop) &&
            candidateTop <= capsuleBottom + CharacterFloorProbeDistance * 2f + CharacterContactSkin;
    }

    private static bool ShouldSkipDescendingCorner(
        CharacterBody3D character,
        Shape3D characterShape,
        in Transform3D characterTransform,
        Shape3D candidateShape,
        in Transform3D candidateTransform,
        in Vector3 motion,
        in Vector3 normal,
        bool horizontalOnly)
    {
        if (horizontalOnly || motion.Y >= -1e-12f ||
            MathF.Abs(motion.X) > 1e-12f || MathF.Abs(motion.Z) > 1e-12f ||
            characterShape is not CapsuleShape3D || candidateShape is not BoxShape3D ||
            !IsAxisAligned(characterTransform) || !IsAxisAligned(candidateTransform))
        {
            return false;
        }

        var horizontalNormalSquared = normal.X * normal.X + normal.Z * normal.Z;
        // 只有向上的斜法线才是顶面外棱的圆角擦碰；向下的法线代表
        // 天花板/底部真实阻挡，必须保留。
        return normal.Y > 1e-4f && horizontalNormalSquared > 1e-8f;
    }

    private static bool IsFloorContact(
        CharacterBody3D character,
        Shape3D candidateShape,
        in Transform3D candidateTransform,
        in Vector3 normal)
    {
        if (Vector3.Dot(normal, character.UpDirection) < character.FloorMaxAngleCosine)
        {
            return false;
        }

        if (candidateShape is BoxShape3D && IsAxisAligned(candidateTransform))
        {
            // 体素盒没有斜面；其外棱的向上斜法线只能是胶囊圆角
            // 接触，不能让角色进入落地状态。
            return normal.Y >= 0.999f &&
                normal.X * normal.X + normal.Z * normal.Z <= 1e-8f;
        }

        return true;
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool SameTransform(in Transform3D left, in Transform3D right) =>
        left.Position == right.Position && left.Rotation == right.Rotation && left.Scale == right.Scale;

    private static Transform3D GetSimulationTransform(ColliderProxy proxy) =>
        proxy.CollisionObject is StaticBody3D ? proxy.StaticTransform : proxy.CollisionShape.GlobalTransform;

    private bool HasPhysicsWorldAncestor()
    {
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is PhysicsWorld3D)
            {
                return true;
            }
        }

        return false;
    }

    private void ClearColliders()
    {
        foreach (var proxy in _colliders.Values)
        {
            proxy.CollisionShape.ColliderId = 0;
            if (proxy.CollisionObject.World == this)
            {
                proxy.CollisionObject.World = null;
            }
        }

        _colliders.Clear();
        _staticColliders.Clear();
        _dynamicColliders.Clear();
        _activeAreaOverlaps.Clear();
        _currentAreaOverlaps.Clear();
        _areaEvents.Clear();
        _characterMoveCommands.Clear();
        _rigidBodies.Clear();
        _rigidForceCommands.Clear();
        _rigidImpulseCommands.Clear();
        _rigidContactDrifts.Clear();
        _dynamicTree?.Clear();
        _staticBvh = new StaticBvh<ColliderProxy>([]);
    }

    internal void EnsureMainThreadAccess() => EnsureMainThread();

    private void EnsureMainThread()
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == 0)
        {
            _ownerThreadId = currentThreadId;
            return;
        }

        if (_ownerThreadId != currentThreadId)
        {
            throw new InvalidOperationException("PhysicsWorld3D 第一版只能在主线程处理");
        }
    }

    private void ReportOnce(string code, Node node, string message)
    {
        var path = node.GetPath().Value;
        if (_reportedDiagnostics.Add($"{code}\0{path}"))
        {
            Diagnostics.Add(new EngineDiagnostic(code, DiagnosticSeverity.Error, message, nodePath: path));
        }
    }

    private readonly record struct PendingCollider(
        CollisionObject3D CollisionObject,
        CollisionShape3D Shape,
        int TraversalOrdinal);

    private readonly record struct CharacterMoveCommand(CharacterBody3D Character, Vector3 Velocity);

    private readonly record struct RigidForceCommand(RigidBody3D Body, Vector3 Force);

    private readonly record struct RigidImpulseCommand(RigidBody3D Body, Vector3 Impulse);

    private readonly record struct AreaOverlapState(
        Area3D Area,
        long AreaColliderId,
        ColliderProxy Other);

    private sealed class AreaOverlapComparer : IComparer<AreaOverlapState>
    {
        public static AreaOverlapComparer Instance { get; } = new();

        public int Compare(AreaOverlapState left, AreaOverlapState right)
        {
            var areaOrder = left.AreaColliderId.CompareTo(right.AreaColliderId);
            return areaOrder != 0 ? areaOrder : left.Other.ColliderId.CompareTo(right.Other.ColliderId);
        }
    }

    private sealed class AreaEventComparer : IComparer<PhysicsAreaEvent>
    {
        public static AreaEventComparer Instance { get; } = new();

        public int Compare(PhysicsAreaEvent left, PhysicsAreaEvent right)
        {
            var areaOrder = left.AreaColliderId.CompareTo(right.AreaColliderId);
            if (areaOrder != 0)
            {
                return areaOrder;
            }

            var otherOrder = left.OtherColliderId.CompareTo(right.OtherColliderId);
            if (otherOrder != 0)
            {
                return otherOrder;
            }

            return left.Entered == right.Entered ? 0 : left.Entered ? -1 : 1;
        }
    }

    private sealed class RayHitComparer : IComparer<PhysicsRayHit3D>
    {
        public static RayHitComparer Instance { get; } = new();

        public int Compare(PhysicsRayHit3D left, PhysicsRayHit3D right)
        {
            var distanceOrder = left.Distance.CompareTo(right.Distance);
            return distanceOrder != 0 ? distanceOrder : left.ColliderId.CompareTo(right.ColliderId);
        }
    }

    private sealed class OverlapHitComparer : IComparer<PhysicsOverlapHit3D>
    {
        public static OverlapHitComparer Instance { get; } = new();

        public int Compare(PhysicsOverlapHit3D left, PhysicsOverlapHit3D right) =>
            left.ColliderId.CompareTo(right.ColliderId);
    }

    private sealed class PhysicsContactComparer : IComparer<PhysicsContact>
    {
        public static PhysicsContactComparer Instance { get; } = new();

        public int Compare(PhysicsContact left, PhysicsContact right)
        {
            var leftMinimum = Math.Min(left.FirstColliderId, left.SecondColliderId);
            var rightMinimum = Math.Min(right.FirstColliderId, right.SecondColliderId);
            var minimumOrder = leftMinimum.CompareTo(rightMinimum);
            if (minimumOrder != 0)
            {
                return minimumOrder;
            }

            var leftMaximum = Math.Max(left.FirstColliderId, left.SecondColliderId);
            var rightMaximum = Math.Max(right.FirstColliderId, right.SecondColliderId);
            var maximumOrder = leftMaximum.CompareTo(rightMaximum);
            return maximumOrder != 0 ? maximumOrder : left.FeatureId.CompareTo(right.FeatureId);
        }
    }

}
