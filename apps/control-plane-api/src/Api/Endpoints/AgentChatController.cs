using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.ControlPlane.Api.Endpoints;

/// <summary>
/// AG-UI compatible chat endpoint that streams analysis insights using
/// the Agent-User Interaction (AG-UI) protocol via Server-Sent Events.
///
/// AG-UI Protocol reference: https://github.com/ag-ui-protocol/ag-ui
///
/// Endpoint: POST /api/v1/chat/agent
/// Request:  AgentChatRequest (threadId, runId, messages, state)
/// Response: text/event-stream — AG-UI events (RUN_STARTED, TOOL_CALL_*, TEXT_MESSAGE_*, RUN_FINISHED)
/// </summary>
[ApiController]
[Route("api/v1/chat")]
public class AgentChatController : ControllerBase
{
    private readonly AtlasDbContext _context;
    private readonly AIChatService _aiChatService;
    private readonly ILogger<AgentChatController> _logger;

    // AG-UI event type constants
    // AG-UI event type constants and SSE serializer options are defined in AgUiEventTypes.cs.
    private const string RunStarted = AgUiEventTypes.RunStarted;
    private const string RunFinished = AgUiEventTypes.RunFinished;
    private const string RunError = AgUiEventTypes.RunError;
    private const string TextMessageStart = AgUiEventTypes.TextMessageStart;
    private const string TextMessageContent = AgUiEventTypes.TextMessageContent;
    private const string TextMessageEnd = AgUiEventTypes.TextMessageEnd;
    private const string ToolCallStart = AgUiEventTypes.ToolCallStart;
    private const string ToolCallArgs = AgUiEventTypes.ToolCallArgs;
    private const string ToolCallEnd = AgUiEventTypes.ToolCallEnd;
    private const string StateSnapshot = AgUiEventTypes.StateSnapshot;

    private static readonly JsonSerializerOptions JsonOptions = AgUiEventTypes.SseJsonOptions;

    public AgentChatController(AtlasDbContext context, AIChatService aiChatService, ILogger<AgentChatController> logger)
    {
        _context = context;
        _aiChatService = aiChatService;
        _logger = logger;
    }

    /// <summary>
    /// AG-UI agent endpoint — accepts a chat message and streams back analysis insights.
    /// Implements the AG-UI SSE protocol so any AG-UI compatible client can connect.
    /// </summary>
    [HttpPost("agent")]
    [Authorize(Policy = "AnalysisRead")]
    public async Task AgentChat([FromBody] AgentChatRequest request, CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var threadId = string.IsNullOrWhiteSpace(request.ThreadId) ? Guid.NewGuid().ToString() : request.ThreadId;
        var runId = Guid.NewGuid().ToString();

        try
        {
            // AG-UI: signal the run has started
            await WriteEvent(new { type = RunStarted, threadId, runId }, cancellationToken);

            // Extract the user's question
            var userMessage = request.Messages
                .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                ?.Content ?? "";

            if (string.IsNullOrWhiteSpace(userMessage))
            {
                await WriteEvent(new { type = RunError, message = "No user message found in the request." }, cancellationToken);
                return;
            }

            const int maxMessageLength = 4_000;
            if (userMessage.Length > maxMessageLength)
            {
                await WriteEvent(new { type = RunError, message = $"Message exceeds {maxMessageLength} character limit." }, cancellationToken);
                return;
            }

            _logger.LogInformation("AG-UI chat request: threadId={ThreadId} runId={RunId} question={Question}",
                threadId, runId, userMessage[..Math.Min(100, userMessage.Length)]);

            // ─── Tool call 1: query infrastructure context ─────────────────────────────
            var ctxToolId = Guid.NewGuid().ToString();
            await WriteEvent(new { type = ToolCallStart, toolCallId = ctxToolId, toolCallName = "queryInfrastructureContext" }, cancellationToken);
            await WriteEvent(new
            {
                type = ToolCallArgs,
                toolCallId = ctxToolId,
                delta = JsonSerializer.Serialize(new { query = userMessage, limit = 10 })
            }, cancellationToken);

            // Gather context from database
            var serviceGroups = await _context.ServiceGroups
                .AsNoTracking()
                .OrderByDescending(sg => sg.UpdatedAt)
                .Take(10)
                .Select(sg => new { sg.Id, sg.Name, sg.Description })
                .ToListAsync(cancellationToken);

            var recentRuns = await _context.AnalysisRuns
                .AsNoTracking()
                .OrderByDescending(r => r.CreatedAt)
                .Take(5)
                .Select(r => new { r.Id, r.ServiceGroupId, r.Status, r.CreatedAt, r.CompletedAt })
                .ToListAsync(cancellationToken);

            var topRecommendation = await _context.Recommendations
                .AsNoTracking()
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new
                {
                    r.Id,
                    r.ServiceGroupId,
                    r.Title,
                    r.Category,
                    r.Status,
                    r.Priority
                })
                .FirstOrDefaultAsync(cancellationToken);

            var latestDrift = await _context.DriftSnapshots
                .AsNoTracking()
                .OrderByDescending(s => s.SnapshotTime)
                .Select(s => new
                {
                    s.Id,
                    s.ServiceGroupId,
                    s.DriftScore,
                    s.TotalViolations,
                    s.SnapshotTime
                })
                .FirstOrDefaultAsync(cancellationToken);

            var pendingRecommendations = await _context.Recommendations
                .AsNoTracking()
                .Where(r => r.Status == "pending" || r.Status == "PendingApproval" || r.Status == "manual_review")
                .OrderByDescending(r => r.CreatedAt)
                .Take(40)
                .Select(r => new
                {
                    r.Id,
                    r.ResourceId,
                    r.ServiceGroupId,
                    r.Title,
                    r.Category,
                    r.Priority,
                    r.ActionType
                })
                .ToListAsync(cancellationToken);

            var governanceConflict = pendingRecommendations
                .GroupBy(r => r.ResourceId)
                .SelectMany(group =>
                {
                    var ordered = group.ToList();
                    if (ordered.Count < 2)
                    {
                        return [];
                    }

                    return ordered
                        .SelectMany((first, index) => ordered.Skip(index + 1)
                            .Where(second => first.Id != second.Id &&
                                             (!string.Equals(first.Category, second.Category, StringComparison.OrdinalIgnoreCase) ||
                                              !string.Equals(first.ActionType, second.ActionType, StringComparison.OrdinalIgnoreCase)))
                            .Select(second => new
                            {
                                first.Id,
                                FirstTitle = first.Title,
                                SecondId = second.Id,
                                SecondTitle = second.Title,
                                first.ResourceId,
                                first.ServiceGroupId
                            }))
                        .Take(1);
                })
                .FirstOrDefault();

            await WriteEvent(new
            {
                type = ToolCallEnd,
                toolCallId = ctxToolId,
                output = JsonSerializer.Serialize(new
                {
                    serviceGroupCount = serviceGroups.Count,
                    recentRunCount = recentRuns.Count,
                    serviceGroups
                })
            }, cancellationToken);

            // ─── Tool call 2: topic-specific data queries ──────────────────────────────
            var query = userMessage.ToLowerInvariant();
            List<object> findings = new();
            string toolName;
            object toolArgs;
            object toolResult;

            if (query.Contains("recommend") || query.Contains("suggest") || query.Contains("improve") || query.Contains("fix"))
            {
                toolName = "getRecommendations";
                toolArgs = new { status = "pending", limit = 10 };

                var recs = await _context.Recommendations
                    .AsNoTracking()
                    .Where(r => r.Status == "pending")
                    .OrderByDescending(r => r.CreatedAt)
                    .Take(10)
                    .Select(r => new { r.Id, r.Title, r.Priority, r.Category, r.Status, r.Confidence })
                    .ToListAsync(cancellationToken);

                toolResult = new { count = recs.Count, recommendations = recs };
                findings.AddRange(recs.Select(r => (object)new
                {
                    label = $"[{r.Priority.ToUpper()}] {r.Title}",
                    category = r.Category
                }));
            }
            else if (query.Contains("drift") || query.Contains("change") || query.Contains("shift") || query.Contains("trend"))
            {
                toolName = "getDriftSnapshots";
                toolArgs = new { limit = 5 };

                var snapshots = await _context.DriftSnapshots
                    .AsNoTracking()
                    .OrderByDescending(s => s.SnapshotTime)
                    .Take(5)
                    .Select(s => new { s.Id, s.ServiceGroupId, s.DriftScore, s.SnapshotTime, s.TotalViolations })
                    .ToListAsync(cancellationToken);

                toolResult = new { count = snapshots.Count, snapshots };
                findings.AddRange(snapshots.Select(s => (object)new
                {
                    label = $"Drift score: {s.DriftScore:F1} ({s.TotalViolations} violations)",
                    capturedAt = s.SnapshotTime
                }));
            }
            else if (query.Contains("violation") || query.Contains("compliance") || query.Contains("security") || query.Contains("best practice"))
            {
                toolName = "getBestPracticeViolations";
                toolArgs = new { limit = 10 };

                var violations = await _context.BestPracticeViolations
                    .AsNoTracking()
                    .OrderByDescending(v => v.DetectedAt)
                    .Take(10)
                    .Select(v => new { v.Id, v.ServiceGroupId, v.RuleId, v.Severity, v.DetectedAt })
                    .ToListAsync(cancellationToken);

                toolResult = new { count = violations.Count, violations };
                findings.AddRange(violations.Select(v => (object)new
                {
                    label = $"[{v.Severity.ToUpper()}] Rule {v.RuleId} violation",
                    detectedAt = v.DetectedAt
                }));
            }
            else if (query.Contains("resource") || query.Contains("inventory") || query.Contains("discover"))
            {
                toolName = "getDiscoveredResources";
                toolArgs = new { limit = 10 };

                var resources = await _context.DiscoveredResources
                    .AsNoTracking()
                    .OrderByDescending(r => r.CreatedAt)
                    .Take(10)
                    .Select(r => new { r.Id, r.ResourceName, r.ResourceType, r.Region, r.TelemetryState })
                    .ToListAsync(cancellationToken);

                toolResult = new { count = resources.Count, resources };
                findings.AddRange(resources.Select(r => (object)new
                {
                    label = $"{r.ResourceType}: {r.ResourceName} ({r.Region ?? "unknown"}) — {r.TelemetryState}"
                }));
            }
            else
            {
                // Default: overview of recent analyses
                toolName = "getAnalysisOverview";
                toolArgs = new { limit = 5 };

                var analyses = await _context.AnalysisRuns
                    .AsNoTracking()
                    .OrderByDescending(r => r.CreatedAt)
                    .Take(5)
                    .Select(r => new { r.Id, r.ServiceGroupId, r.Status, r.CreatedAt })
                    .ToListAsync(cancellationToken);

                var sgNames = serviceGroups.ToDictionary(sg => sg.Id, sg => sg.Name);

                toolResult = new
                {
                    count = analyses.Count,
                    analyses = analyses.Select(a => new
                    {
                        a.Id,
                        serviceGroup = sgNames.TryGetValue(a.ServiceGroupId, out var n) ? n : a.ServiceGroupId.ToString(),
                        a.Status,
                        a.CreatedAt
                    })
                };
                findings.AddRange(analyses.Select(a => (object)new
                {
                    label = $"{(sgNames.TryGetValue(a.ServiceGroupId, out var n) ? n : "Group")} — {a.Status}",
                    runAt = a.CreatedAt
                }));
            }

            var dataToolId = Guid.NewGuid().ToString();
            await WriteEvent(new { type = ToolCallStart, toolCallId = dataToolId, toolCallName = toolName }, cancellationToken);
            await WriteEvent(new { type = ToolCallArgs, toolCallId = dataToolId, delta = JsonSerializer.Serialize(toolArgs) }, cancellationToken);
            await WriteEvent(new { type = ToolCallEnd, toolCallId = dataToolId, output = JsonSerializer.Serialize(toolResult) }, cancellationToken);

            // ─── Emit a state snapshot so the UI can bind infrastructure context ─────────
            await WriteEvent(new
            {
                type = StateSnapshot,
                snapshot = new
                {
                    serviceGroupCount = serviceGroups.Count,
                    serviceGroupNames = serviceGroups.Select(sg => sg.Name).ToList(),
                    serviceGroupIds = serviceGroups.Select(sg => sg.Id).ToList(),
                    recentRunStatuses = recentRuns.Select(r => r.Status).Distinct().ToList(),
                    recentRunIds = recentRuns.Select(r => r.Id).ToList(),
                    findingCount = findings.Count,
                    topRecommendation = topRecommendation is null ? null : new
                    {
                        topRecommendation.Id,
                        topRecommendation.ServiceGroupId,
                        topRecommendation.Title,
                        topRecommendation.Category,
                        topRecommendation.Priority,
                        topRecommendation.Status
                    },
                    latestDrift = latestDrift is null ? null : new
                    {
                        latestDrift.Id,
                        latestDrift.ServiceGroupId,
                        latestDrift.DriftScore,
                        latestDrift.TotalViolations,
                        latestDrift.SnapshotTime
                    },
                    governanceConflict,
                    capabilityModes = new
                    {
                        chat = "provider_backed",
                        remediation = "approval_required",
                        drift = latestDrift is null ? "no_data" : "provider_backed"
                    }
                }
            }, cancellationToken);

            // ─── Stream the AI-powered response ─────────────────────────────────
            var messageId = Guid.NewGuid().ToString();
            await WriteEvent(new { type = TextMessageStart, messageId, role = "assistant" }, cancellationToken);

            var infraContext = new InfrastructureContext
            {
                ServiceGroupCount = serviceGroups.Count,
                ServiceGroupNames = serviceGroups.Select(sg => (string)sg.Name).ToList(),
                RecentRunCount = recentRuns.Count,
                CompletedRunCount = recentRuns.Count(r => (string)r.Status == AnalysisRunStatus.Completed),
                PendingRunCount = recentRuns.Count(r => (string)r.Status is "queued" or "running"),
                Findings = findings.Select(f =>
                {
                    var json = JsonSerializer.Serialize(f);
                    using var doc = JsonDocument.Parse(json);
                    var label = doc.RootElement.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                    var category = doc.RootElement.TryGetProperty("category", out var c) ? c.GetString() : null;
                    return new FindingSummary(label, category);
                }).ToList(),
                DetailedDataJson = JsonSerializer.Serialize(toolResult)
            };

            var aiResponse = await _aiChatService.GenerateResponseAsync(userMessage, infraContext, cancellationToken);

            _logger.LogInformation(
                "Chat response generated. Source: {Source}, AI available: {AIAvailable}",
                aiResponse.ConfidenceSource, _aiChatService.IsAIAvailable);

            // Stream in small chunks for AG-UI protocol compliance
            foreach (var chunk in Chunk(aiResponse.Text, chunkSize: 8))
            {
                if (cancellationToken.IsCancellationRequested) break;
                await WriteEvent(new { type = TextMessageContent, messageId, delta = chunk }, cancellationToken);
                await Task.Delay(15, cancellationToken);
            }

            await WriteEvent(new { type = TextMessageEnd, messageId }, cancellationToken);

            // AG-UI: signal the run is done
            await WriteEvent(new { type = RunFinished, threadId, runId }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal, no need to log as error
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in AG-UI chat run {RunId}", runId);
            try
            {
                await WriteEvent(new { type = RunError, message = $"Internal error: {ex.Message}" }, cancellationToken);
            }
            catch
            {
                // Best-effort — response may already be closed
            }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private async Task WriteEvent(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var line = $"data: {json}\n\n";
        await Response.WriteAsync(line, Encoding.UTF8, ct);
        await Response.Body.FlushAsync(ct);
    }

    private static IEnumerable<string> Chunk(string text, int chunkSize)
    {
        for (var i = 0; i < text.Length; i += chunkSize)
            yield return text[i..Math.Min(i + chunkSize, text.Length)];
    }
}

// ─── Request model ─────────────────────────────────────────────────────────────

public record AgentChatRequest
{
    [JsonPropertyName("threadId")]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    [JsonPropertyName("messages")]
    public List<AgentChatMessage> Messages { get; init; } = new();

    [JsonPropertyName("state")]
    public Dictionary<string, object>? State { get; init; }

    [JsonPropertyName("context")]
    public List<Dictionary<string, object>>? Context { get; init; }
}

public record AgentChatMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}
