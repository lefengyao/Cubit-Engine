using Cubit.Core.Ecs;

namespace Cubit.Core.Scene;

/// <summary>把场景树生命周期桥接到可选 ECS 与 Job System。</summary>
public sealed class EcsNode : Node, Jobs.IJobSystemConsumer
{
    private readonly List<IEcsSystem> _systems = [];
    private Jobs.JobSystem? _jobs;
    private EcsScheduler? _scheduler;
    private bool _ownsJobs;

    [Export("ECS worker 数；0 表示自动")]
    public int WorkerCount { get; set; }

    public EcsWorld World { get; } = new();

    public Jobs.JobSystem? Jobs { get; set; }

    public EcsScheduler? Scheduler => _scheduler;

    public bool IsInitialized => _scheduler is not null;

    public void AddSystem(IEcsSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (IsInitialized)
        {
            throw new InvalidOperationException("EcsNode 初始化后不能新增 System；请在入树前完成注册");
        }

        _systems.Add(system);
    }

    protected override void Ready()
    {
        if (WorkerCount < 0)
        {
            throw new InvalidOperationException("EcsNode.WorkerCount 不能小于 0");
        }

        if (IsInitialized)
        {
            return;
        }

        _jobs = Jobs;
        _ownsJobs = _jobs is null;
        _jobs ??= WorkerCount == 0
            ? new Jobs.JobSystem()
            : new Jobs.JobSystem(new Jobs.JobSystemOptions { WorkerCount = WorkerCount });
        try
        {
            _scheduler = new EcsScheduler(World, _jobs);
            foreach (var system in _systems)
            {
                _scheduler.Register(system);
            }
        }
        catch
        {
            _scheduler = null;
            if (_ownsJobs)
            {
                _jobs.Dispose();
            }

            _jobs = null;
            _ownsJobs = false;
            throw;
        }
    }

    protected override void PhysicsProcess(double delta) =>
        _scheduler?.ExecutePhase(EcsSystemPhase.FixedUpdate, delta);

    protected override void Process(double delta)
    {
        _scheduler?.ExecutePhase(EcsSystemPhase.Update, delta);
        _scheduler?.ExecutePhase(EcsSystemPhase.RenderPrepare, delta);
    }

    protected override void ExitTree()
    {
        _scheduler = null;
        if (_ownsJobs)
        {
            _jobs?.Dispose();
        }

        _jobs = null;
        _ownsJobs = false;
    }
}
