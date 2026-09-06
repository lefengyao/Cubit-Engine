namespace Cubit.Core.Jobs;

/// <summary>需要异步工作能力的节点由宿主注入共享 JobSystem。</summary>
public interface IJobSystemConsumer
{
    JobSystem? Jobs { get; set; }
}
