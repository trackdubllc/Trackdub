namespace Trackdub.Contracts.Pipeline;

public interface IPipelineRunLifecycle
{
    void BeginRun();
    void EndRun();
}
