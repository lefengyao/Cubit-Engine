namespace Cubit.Voxel.World;

/// <summary>确定性伪随机（xorshift64）：同种子永远产生同一序列，用于随机刻/世界生成等需要可回放的场景。</summary>
public sealed class DeterministicRandom
{
    private ulong _state;

    public DeterministicRandom(ulong seed)
    {
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            return 0;
        }

        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return (int)(_state % (ulong)maxExclusive);
    }
}