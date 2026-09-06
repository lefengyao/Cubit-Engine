using Cubit.Voxel.World;
using System.Threading;

namespace Cubit.Voxel.Input;

/// <summary>体素玩法输入快照，不污染引擎通用输入契约。</summary>
public readonly record struct VoxelInputSnapshot(bool Break, bool Place, ushort SelectedBlock);

/// <summary>体素插件的挖掘、放置与选块输入状态。</summary>
public sealed class VoxelInputState
{
    private int _breakHeld;
    private int _placeHeld;
    private int _selectedBlock = BlockRegistry.Grass;

    /// <summary>设置挖掘按住状态；允许平台 UI 线程写入，由游戏线程读取快照。</summary>
    public void SetBreak(bool pressed) => Volatile.Write(ref _breakHeld, pressed ? 1 : 0);

    /// <summary>设置放置按住状态；允许平台 UI 线程写入，由游戏线程读取快照。</summary>
    public void SetPlace(bool pressed) => Volatile.Write(ref _placeHeld, pressed ? 1 : 0);

    public void SetSelectedBlock(ushort block)
    {
        _ = BlockRegistry.Get(block);
        Volatile.Write(ref _selectedBlock, block);
    }

    /// <summary>由游戏线程读取当前体素操作状态。</summary>
    public VoxelInputSnapshot BuildSnapshot() => new(
        Volatile.Read(ref _breakHeld) != 0,
        Volatile.Read(ref _placeHeld) != 0,
        checked((ushort)Volatile.Read(ref _selectedBlock)));
}
