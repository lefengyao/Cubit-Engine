namespace Cubit.Core.Ecs;

public readonly record struct EntityId(int Index, int Generation)
{
    public bool IsValid => Index >= 0 && Generation > 0;
}
