namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Event-publisher abstraction that allows <see cref="AnalysisOrchestrationService"/>
/// to broadcast real-time progress without depending on the SignalR infrastructure.
/// The concrete implementation lives in the Api project and uses IHubContext.
/// </summary>
public interface IAnalysisEventPublisher
{
    Task AgentStartedAsync(string runId, string agentName, string description);
    Task AgentCompletedAsync(string runId, string agentName, bool success, long elapsedMs,
        int? itemsProcessed = null, double? scoreValue = null, string? summary = null);
    Task AgentFindingAsync(string runId, string agentName, string category, string severity, string message);
    Task RunCompletedAsync(string runId, string status, DateTime completedAt,
        double overallScore, int resourceCount);
}

/// <summary>
/// No-op publisher used when SignalR is not registered (e.g. unit tests or CLI scenarios).
/// </summary>
public sealed class NullAnalysisEventPublisher : IAnalysisEventPublisher
{
    public static readonly NullAnalysisEventPublisher Instance = new();

    public Task AgentStartedAsync(string runId, string agentName, string description) => Task.CompletedTask;
    public Task AgentCompletedAsync(string runId, string agentName, bool success, long elapsedMs,
        int? itemsProcessed, double? scoreValue, string? summary) => Task.CompletedTask;
    public Task AgentFindingAsync(string runId, string agentName, string category, string severity, string message) => Task.CompletedTask;
    public Task RunCompletedAsync(string runId, string status, DateTime completedAt,
        double overallScore, int resourceCount) => Task.CompletedTask;
}
