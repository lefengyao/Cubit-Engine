namespace Cubit.Core.Ecs;

internal interface IComponentStore
{
    Type ComponentType { get; }

    int Count { get; }

    bool Contains(EntityId entity);

    bool Remove(EntityId entity);
}
