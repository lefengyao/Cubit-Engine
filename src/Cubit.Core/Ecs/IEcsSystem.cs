namespace Cubit.Core.Ecs;

public interface IEcsSystem
{
    EcsSystemDescriptor Descriptor { get; }

    void Execute(EcsSystemContext context);
}
