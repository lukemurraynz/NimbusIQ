using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Atlas.ControlPlane.Api.Hubs;

/// <summary>
/// SignalR hub for real-time analysis event streaming.
/// Clients join a group named after the analysis run ID to receive live progress events.
///
/// Authorization: Enforced via [Authorize] at hub level. Hub methods require authentication.
/// The negotiate endpoint (handshake) is protected by ASP.NET Core's built-in SignalR
/// auth checks — the Authorization header is validated during handshake before
/// SubscribeToRun or other methods can be invoked.
///
/// Frontend usage:
///   const connection = new HubConnectionBuilder()
///     .withUrl('/hubs/analysis', {
///       // Restrict to SSE/LongPolling to keep the token in the Authorization header.
///       // WebSocket upgrade URLs are logged by reverse proxies; restricting transports
///       // avoids the token appearing as ?access_token= in those logs.
///       transport: HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
///       accessTokenFactory: () => token,
///     })
///     .build();
///   await connection.start();
///   await connection.invoke('SubscribeToRun', runId);
///   connection.on('AgentStarted',   (e) => /* handle */);
///   connection.on('AgentCompleted', (e) => /* handle */);
///   connection.on('AgentFinding',   (e) => /* handle */);
/// </summary>
[Authorize(Policy = "AnalysisRead")]
public class AnalysisHub : Hub
{
    /// <summary>
    /// Subscribe a client to progress events for a specific analysis run.
    /// </summary>
    public async Task SubscribeToRun(string runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"run:{runId}");
    }

    /// <summary>
    /// Unsubscribe a client from a run group.
    /// </summary>
    public async Task UnsubscribeFromRun(string runId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"run:{runId}");
    }
}

/// <summary>
/// Payload for AgentStarted SignalR event.
/// </summary>
public sealed record AgentStartedEvent(
    string RunId,
    string AgentName,
    string Description,
    DateTimeOffset StartedAt);

/// <summary>
/// Payload for AgentCompleted SignalR event.
/// </summary>
public sealed record AgentCompletedEvent(
    string RunId,
    string AgentName,
    bool Success,
    int? ItemsProcessed,
    double? ScoreValue,
    string? Summary,
    DateTimeOffset CompletedAt,
    long ElapsedMs);

/// <summary>
/// Payload for AgentFinding SignalR event — emitted once per key insight.
/// </summary>
public sealed record AgentFindingEvent(
    string RunId,
    string AgentName,
    string Category,
    string Severity,
    string Message,
    DateTimeOffset DetectedAt);
