namespace Cubit.Core.Project;

/// <summary>项目向平台呈现层提供的不可变调试文字快照，不包含领域或 GPU 类型。</summary>
public sealed record ProjectDebugOverlaySnapshot(
    bool IsVisible,
    IReadOnlyList<string> LeftLines,
    IReadOnlyList<string> RightLines)
{
    public static ProjectDebugOverlaySnapshot Hidden { get; } = new(false, [], []);
}

/// <summary>项目自身负责采集作者场景状态；平台只消费文字快照。</summary>
public interface IProjectDebugOverlaySource
{
    ProjectDebugOverlaySnapshot CaptureSnapshot();
}
