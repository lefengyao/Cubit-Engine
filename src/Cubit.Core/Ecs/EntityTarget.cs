namespace Cubit.Core.Ecs;

public readonly record struct EntityTarget(EntityId Entity, int DeferredIndex)
{
    public bool IsDeferred => DeferredIndex >= 0;

    public static EntityTarget Existing(EntityId entity) => new(entity, -1);
}
