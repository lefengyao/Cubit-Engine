namespace Cubit.Core.Jobs;

public sealed class JobExecutionException : Exception
{
    public JobExecutionException(string jobName, Exception innerException)
        : base($"Job '{jobName}' 执行失败: {innerException.Message}", innerException)
    {
        JobName = jobName;
    }

    public string JobName { get; }
}
