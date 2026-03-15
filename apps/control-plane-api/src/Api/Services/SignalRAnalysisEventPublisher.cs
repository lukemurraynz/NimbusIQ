using Atlas.ControlPlane.Api.Hubs;
using Atlas.ControlPlane.Application.Services;
using Microsoft.AspNetCore.SignalR;

namespace Atlas.ControlPlane.Api.Services;

/// <summary>
/// SignalR implementation of <see cref="IAnalysisEventPublisher"/>.
/// Broadcasts analysis progress events to all SignalR clients subscribed to the run group.
/// </summary>
public sealed class SignalRAnalysisEventPublisher : IAnalysisEventPublisher
{
    private readonly IHubContext<AnalysisHub> _hub;

    public SignalRAnalysisEventPublisher(IHubContext<AnalysisHub> hub)
    {
        _hub = hub;
    }

    public Task AgentStartedAsync(string runId, string agentName, string description) =>
        _hub.Clients.Group($"run:{runId}").SendAsync("AgentStarted",
            new AgentStartedEvent(runId, agentName, description, DateTimeOffset.UtcNow));

    public Task AgentCompletedAsync(string runId, string agentName, bool success, long elapsedMs,
        int? itemsProcessed, double? scoreValue, string? summary) =>
        _hub.Clients.Group($"run:{runId}").SendAsync("AgentCompleted",
            new AgentCompletedEvent(runId, agentName, success, itemsProcessed, scoreValue, summary,
                DateTimeOffset.UtcNow, elapsedMs));

    public Task AgentFindingAsync(string runId, string agentName, string category, string severity, string message) =>
        _hub.Clients.Group($"run:{runId}").SendAsync("AgentFinding",
            new AgentFindingEvent(runId, agentName, category, severity, message, DateTimeOffset.UtcNow));

    public Task RunCompletedAsync(string runId, string status, DateTime completedAt,
        double overallScore, int resourceCount) =>
        _hub.Clients.Group($"run:{runId}").SendAsync("RunCompleted", new
        {
            runId,
            status,
            completedAt,
            overallScore,
            resourceCount
        });
}
