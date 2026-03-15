using Atlas.AgentOrchestrator.Contracts;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.ControlPlane.Api.Endpoints;

/// <summary>
/// AG-UI compatible orchestration endpoints that stream Microsoft Agent Framework
/// agent execution as Server-Sent Events.
///
/// Extends AG-UI beyond chat: every analysis run is now transparent — each
/// Atlas agent (DriftDetection, WellArchitected, FinOps, BestPractice, etc.)
/// maps to an AG-UI TOOL_CALL event, making the multi-agent orchestration
/// visible to the user in real-time.
///
/// AG-UI Protocol: https://github.com/ag-ui-protocol/ag-ui
/// Microsoft Agent Framework: each Atlas agent = one tool call in the AG-UI stream.
///
/// Endpoints:
///   POST /api/v1/agents/analysis-stream/{serviceGroupId}
///     Creates an analysis run and streams AG-UI events as each agent completes.
///
///   GET /api/v1/agents/activity-stream
///     Streams a STATE_SNAPSHOT of current system-wide agent activity (recent runs).
/// </summary>
[ApiController]
[Route("api/v1/agents")]
[Authorize(Policy = "AnalysisRead")]
public class AgentOrchestrationController : ControllerBase
{
    private readonly AtlasDbContext _context;
    private readonly ILogger<AgentOrchestrationController> _logger;
    private readonly A2AMessageValidator _a2aValidator;

    // AG-UI event type constants (same as chat controller for consistency)
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

    private static readonly JsonSerializerOptions JsonOpts = AgUiEventTypes.SseJsonOptions;

    public AgentOrchestrationController(
        AtlasDbContext context,
        ILogger<AgentOrchestrationController> logger,
        A2AMessageValidator a2aValidator)
    {
        _context = context;
        _logger = logger;
        _a2aValidator = a2aValidator;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // POST /api/v1/agents/analysis-stream/{serviceGroupId}
    //
    // Creates a new AnalysisRun and immediately streams AG-UI events as each
    // Atlas agent (DriftDetection, WellArchitected, FinOps, BestPractice,
    // ServiceHierarchy) runs in sequence — making the multi-agent orchestration
    // fully transparent via the AG-UI TOOL_CALL_* protocol.
    // ─────────────────────────────────────────────────────────────────────────
    [HttpPost("analysis-stream/{serviceGroupId:guid}")]
    [Authorize(Policy = "AnalysisWrite")]
    public async Task StreamAnalysisRun(Guid serviceGroupId, CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var sg = await _context.ServiceGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == serviceGroupId, ct);

        if (sg is null)
        {
            await WriteEvent(new { type = RunError, message = $"Service group {serviceGroupId} not found." }, ct);
            return;
        }

        var analysisRun = new AnalysisRun
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            Status = AnalysisRunStatus.Queued,
            TriggeredBy =
                User.FindFirstValue("preferred_username") ??
                User.FindFirstValue("oid") ??
                User.Identity?.Name ??
                "unknown",
            CreatedAt = DateTime.UtcNow,
        };
        _context.AnalysisRuns.Add(analysisRun);
        await _context.SaveChangesAsync(ct);

        var runId = analysisRun.Id.ToString();
        var threadId = Guid.NewGuid().ToString();

        try
        {
            await WriteEvent(new { type = RunStarted, threadId, runId, serviceGroupId, serviceGroupName = sg.Name }, ct);
            _logger.LogInformation(
                "AG-UI analysis stream: runId={RunId} serviceGroup={ServiceGroup}",
                runId, sg.Name);

            await StreamPersistedRunEventsAsync(sg, analysisRun, threadId, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AG-UI analysis stream disconnected for run {RunId}", analysisRun.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in AG-UI analysis stream for run {RunId}", runId);
            try { await WriteEvent(new { type = RunError, message = $"Analysis failed: {ex.Message}" }, ct); }
            catch (Exception writeEx) { _logger.LogWarning(writeEx, "Failed to write AG-UI error event for run {RunId}", runId); }
        }
    }

    [HttpPost("a2a/{analysisRunId:guid}")]
    [Authorize(Policy = "AnalysisWrite")]
    public async Task<ActionResult<object>> PostA2AMessage(Guid analysisRunId, [FromBody] A2AMessage message, CancellationToken ct)
    {
        var runExists = await _context.AnalysisRuns.AsNoTracking().AnyAsync(r => r.Id == analysisRunId, ct);
        if (!runExists)
        {
            return NotFound(new { errorCode = "AnalysisRunNotFound", message = $"Analysis run {analysisRunId} not found." });
        }

        var validation = _a2aValidator.Validate(message);
        if (!validation.IsValid)
        {
            var hasMissingRequiredEnvelope =
                string.IsNullOrWhiteSpace(message.MessageId)
                || string.IsNullOrWhiteSpace(message.CorrelationId)
                || string.IsNullOrWhiteSpace(message.SenderAgent)
                || string.IsNullOrWhiteSpace(message.MessageType)
                || message.Payload is null
                || message.Lineage is null;

            if (hasMissingRequiredEnvelope)
            {
                return BadRequest(new { errorCode = "InvalidA2AMessage", errors = validation.Errors });
            }

            _logger.LogWarning(
                "A2A message {MessageId} for run {AnalysisRunId} has schema validation warnings but will be accepted for continuity. Errors: {ErrorCount}",
                message.MessageId,
                analysisRunId,
                validation.Errors.Count);
        }

        var persistedMessageId = TryParseGuid(message.MessageId) ?? Guid.NewGuid();

        _context.AgentMessages.Add(new AgentMessage
        {
            Id = Guid.NewGuid(),
            AnalysisRunId = analysisRunId,
            MessageId = persistedMessageId,
            AgentName = message.SenderAgent,
            AgentRole = "agent",
            MessageType = $"a2a.{message.MessageType}",
            Payload = JsonSerializer.Serialize(message, JsonOpts),
            EvidenceRefs = JsonSerializer.Serialize(message.Lineage?.EvidenceReferences ?? new List<string>(), JsonOpts),
            Confidence = message.Lineage?.ConfidenceScore,
            CreatedAt = message.Timestamp.UtcDateTime
        });

        await _context.SaveChangesAsync(ct);

        return Accepted(new
        {
            analysisRunId,
            messageId = persistedMessageId,
            senderAgent = message.SenderAgent,
            recipientAgent = message.RecipientAgent,
            messageType = message.MessageType,
            validationWarnings = validation.IsValid
                ? null
                : validation.Errors.Select(e => new { e.Path, e.Kind, e.Message })
        });
    }

    [HttpGet("a2a/{analysisRunId:guid}")]
    public async Task<ActionResult<object>> ListA2AMessages(
        Guid analysisRunId,
        [FromQuery] string? senderAgent,
        [FromQuery] string? recipientAgent,
        CancellationToken ct)
    {
        var query = _context.AgentMessages.AsNoTracking()
            .Where(m => m.AnalysisRunId == analysisRunId && m.MessageType.StartsWith("a2a."));

        if (!string.IsNullOrWhiteSpace(senderAgent))
        {
            query = query.Where(m => m.AgentName == senderAgent);
        }

        var messages = await query
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var value = messages
            .Select(m => DeserializeA2AMessage(m.Payload))
            .Where(m => m is not null)
            .Select(m => m!)
            .Where(m => recipientAgent is null || string.Equals(m.RecipientAgent, recipientAgent, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Ok(new { value });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /api/v1/agents/activity-stream
    //
    // Emits a single STATE_SNAPSHOT AG-UI event summarising the system-wide
    // agent activity across all recent analysis runs.  The dashboard's
    // AgentLiveFeedPanel subscribes to this to show what agents are doing
    // right now — extending AG-UI from "just chat" to "platform-wide observability".
    // ─────────────────────────────────────────────────────────────────────────
    [HttpGet("activity-stream")]
    public async Task StreamActivityFeed(CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var runId = Guid.NewGuid().ToString();
        var threadId = Guid.NewGuid().ToString();

        try
        {
            await WriteEvent(new { type = RunStarted, threadId, runId, source = "activity-stream" }, ct);

            // Emit a snapshot every 5 seconds for up to 30 seconds (6 ticks)
            for (var tick = 0; tick < 6 && !ct.IsCancellationRequested; tick++)
            {
                var snapshot = await BuildSystemActivitySnapshot(ct);
                await WriteEvent(new { type = StateSnapshot, snapshot }, ct);

                if (tick < 5)
                    await Task.Delay(5_000, ct);
            }

            await WriteEvent(new { type = RunFinished, threadId, runId }, ct);
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in AG-UI activity stream");
            try { await WriteEvent(new { type = RunError, message = ex.Message }, ct); }
            catch (Exception writeEx) { _logger.LogWarning(writeEx, "Failed to write AG-UI error event for activity stream"); }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams the actual orchestration lifecycle from persisted agent_messages rather than
    /// synthesizing AG-UI events from point-in-time database snapshots.
    /// </summary>
    private async Task StreamPersistedRunEventsAsync(
        ServiceGroup serviceGroup,
        AnalysisRun analysisRun,
        string threadId,
        CancellationToken ct)
    {
        var emittedToolStarts = new HashSet<Guid>();
        var emittedMessages = new HashSet<Guid>();
        var nextSnapshotAt = DateTime.UtcNow;
        var timeoutAt = DateTime.UtcNow.AddMinutes(10);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < timeoutAt)
        {
            var pendingMessages = await _context.AgentMessages
                .AsNoTracking()
                .Where(m => m.AnalysisRunId == analysisRun.Id)
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.MessageId)
                .ToListAsync(ct);

            foreach (var message in pendingMessages.Where(m => emittedMessages.Add(m.Id)))
            {
                await EmitAgUiEventForMessageAsync(message, emittedToolStarts, ct);

                if (string.Equals(message.MessageType, "session-summary", StringComparison.OrdinalIgnoreCase))
                {
                    var finalSnapshot = await BuildFinalSnapshot(analysisRun.Id, ct);
                    await WriteEvent(new { type = StateSnapshot, snapshot = finalSnapshot }, ct);

                    var summaryId = Guid.NewGuid().ToString();
                    await WriteEvent(new { type = TextMessageStart, messageId = summaryId, role = "assistant" }, ct);

                    var summary = BuildAnalysisSummary(serviceGroup.Name, finalSnapshot);
                    foreach (var chunk in Chunk(summary, 10))
                    {
                        if (ct.IsCancellationRequested) break;
                        await WriteEvent(new { type = TextMessageContent, messageId = summaryId, delta = chunk }, ct);
                    }

                    await WriteEvent(new { type = TextMessageEnd, messageId = summaryId }, ct);
                    await WriteEvent(new { type = RunFinished, threadId, runId = analysisRun.Id }, ct);
                    return;
                }
            }

            if (DateTime.UtcNow >= nextSnapshotAt)
            {
                var snapshot = await BuildRunSnapshotAsync(analysisRun.Id, ct);
                await WriteEvent(new { type = StateSnapshot, snapshot }, ct);
                nextSnapshotAt = DateTime.UtcNow.AddSeconds(5);
            }

            var runState = await _context.AnalysisRuns
                .AsNoTracking()
                .Where(r => r.Id == analysisRun.Id)
                .Select(r => new { r.Status, r.CompletedAt })
                .FirstOrDefaultAsync(ct);

            if (runState?.Status is "failed" or "cancelled")
            {
                await WriteEvent(new { type = RunError, runId = analysisRun.Id, status = runState.Status }, ct);
                return;
            }

            await Task.Delay(1000, ct);
        }

        await WriteEvent(new { type = RunError, runId = analysisRun.Id, message = "Timed out waiting for orchestration events." }, ct);
    }

    private async Task EmitAgUiEventForMessageAsync(AgentMessage message, HashSet<Guid> emittedToolStarts, CancellationToken ct)
    {
        var payloadJson = NormalizeJson(message.Payload);
        var payload = payloadJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(payloadJson);
        var metadataJson = NormalizeJson(message.EvidenceRefs);
        var metadata = metadataJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(metadataJson);
        var toolName = ExtractToolName(message.AgentName, payload, metadata);
        var toolCallId = message.MessageId.ToString();

        switch (message.MessageType)
        {
            case "agent.started":
                emittedToolStarts.Add(message.MessageId);
                await WriteEvent(new
                {
                    type = ToolCallStart,
                    toolCallId,
                    toolCallName = toolName,
                    agentName = message.AgentName,
                    metadata = new { agentName = message.AgentName, toolName }
                }, ct);

                await WriteEvent(new
                {
                    type = ToolCallArgs,
                    toolCallId,
                    delta = payloadJson ?? "{}"
                }, ct);
                break;

            case "result":
                if (!emittedToolStarts.Contains(message.MessageId))
                {
                    emittedToolStarts.Add(message.MessageId);
                    await WriteEvent(new
                    {
                        type = ToolCallStart,
                        toolCallId,
                        toolCallName = toolName,
                        agentName = message.AgentName,
                        metadata = new { agentName = message.AgentName, toolName }
                    }, ct);
                }

                await WriteEvent(new
                {
                    type = ToolCallEnd,
                    toolCallId,
                    agentName = message.AgentName,
                    output = payload ?? JsonSerializer.SerializeToElement(new { raw = message.Payload ?? string.Empty }),
                    elapsedMs = TryExtractDuration(metadata)
                }, ct);
                break;

            case "error":
                if (!emittedToolStarts.Contains(message.MessageId))
                {
                    emittedToolStarts.Add(message.MessageId);
                    await WriteEvent(new
                    {
                        type = ToolCallStart,
                        toolCallId,
                        toolCallName = toolName,
                        agentName = message.AgentName,
                        metadata = new { agentName = message.AgentName, toolName }
                    }, ct);
                }

                await WriteEvent(new
                {
                    type = ToolCallEnd,
                    toolCallId,
                    agentName = message.AgentName,
                    error = ExtractMessageText(payload, message.Payload),
                    elapsedMs = TryExtractDuration(metadata)
                }, ct);
                break;
        }
    }

    private async Task<object> BuildFinalSnapshot(Guid analysisRunId, CancellationToken ct)
    {
        var run = await _context.AnalysisRuns.AsNoTracking()
            .Where(r => r.Id == analysisRunId)
            .Select(r => new { r.Id, r.ServiceGroupId, r.CompletedAt })
            .FirstOrDefaultAsync(ct);

        if (run is null)
        {
            return new { analysisRunId, status = "missing" };
        }

        var sg = await _context.ServiceGroups.AsNoTracking()
            .Where(g => g.Id == run.ServiceGroupId)
            .Select(g => new { g.Name, g.Description })
            .FirstOrDefaultAsync(ct);

        int resourceCount = 0;
        var snapshot = await _context.DiscoverySnapshots.AsNoTracking()
            .Where(s => s.AnalysisRunId == analysisRunId)
            .Select(s => new { s.ResourceCount })
            .FirstOrDefaultAsync(ct);
        resourceCount = snapshot?.ResourceCount ?? 0;

        var violationCount = await _context.BestPracticeViolations
            .Where(v => v.ServiceGroupId == run.ServiceGroupId && v.Status == "active")
            .CountAsync(ct);

        var pendingRecs = await _context.Recommendations
            .Where(r => r.ServiceGroupId == run.ServiceGroupId && r.Status == "pending")
            .CountAsync(ct);

        var latestDrift = await _context.DriftSnapshots
            .Where(s => s.ServiceGroupId == run.ServiceGroupId)
            .OrderByDescending(s => s.SnapshotTime)
            .Select(s => new { s.DriftScore, s.TotalViolations })
            .FirstOrDefaultAsync(ct);

        var agentsRun = await _context.AgentMessages.AsNoTracking()
            .Where(m => m.AnalysisRunId == analysisRunId && m.MessageType == "result")
            .Select(m => m.AgentName)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(ct);

        return new
        {
            analysisRunId,
            serviceGroupId = run.ServiceGroupId,
            serviceGroupName = sg?.Name ?? "Unknown",
            resourceCount,
            activeViolations = violationCount,
            pendingRecommendations = pendingRecs,
            driftScore = latestDrift?.DriftScore,
            driftViolations = latestDrift?.TotalViolations,
            lastAnalysisCompleted = run.CompletedAt,
            agentsRun,
            completedAt = DateTime.UtcNow,
        };
    }

    private async Task<object> BuildRunSnapshotAsync(Guid analysisRunId, CancellationToken ct)
    {
        var run = await _context.AnalysisRuns.AsNoTracking()
            .Where(r => r.Id == analysisRunId)
            .Select(r => new { r.Id, r.ServiceGroupId, r.Status, r.CreatedAt, r.CompletedAt })
            .FirstOrDefaultAsync(ct);

        if (run is null)
        {
            return new { analysisRunId, status = "missing" };
        }

        var serviceGroupName = await _context.ServiceGroups.AsNoTracking()
            .Where(g => g.Id == run.ServiceGroupId)
            .Select(g => g.Name)
            .FirstOrDefaultAsync(ct);

        var completedAgents = await _context.AgentMessages.AsNoTracking()
            .Where(m => m.AnalysisRunId == analysisRunId && m.MessageType == "result")
            .Select(m => m.AgentName)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(ct);

        var activeAgents = await _context.AgentMessages.AsNoTracking()
            .Where(m => m.AnalysisRunId == analysisRunId && m.MessageType == "agent.started")
            .Select(m => m.AgentName)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(ct);

        return new
        {
            analysisRunId,
            serviceGroupId = run.ServiceGroupId,
            serviceGroupName,
            status = run.Status,
            startedAt = run.CreatedAt,
            completedAt = run.CompletedAt,
            activeAgents,
            completedAgents
        };
    }

    private async Task<object> BuildSystemActivitySnapshot(CancellationToken ct)
    {
        var recentRuns = await _context.AnalysisRuns.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(10)
            .Select(r => new { r.Id, r.ServiceGroupId, r.Status, r.CreatedAt, r.CompletedAt })
            .ToListAsync(ct);

        var sgIds = recentRuns.Select(r => r.ServiceGroupId).Distinct().ToList();
        var sgNames = await _context.ServiceGroups.AsNoTracking()
            .Where(g => sgIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name, ct);

        var running = recentRuns.Where(r => r.Status == AnalysisRunStatus.Running).ToList();
        var completed = recentRuns.Where(r => r.Status == AnalysisRunStatus.Completed).ToList();
        var queued = recentRuns.Where(r => r.Status == AnalysisRunStatus.Queued).ToList();

        var totalViolations = await _context.BestPracticeViolations
            .Where(v => v.Status == "active")
            .CountAsync(ct);

        var pendingRecs = await _context.Recommendations
            .Where(r => r.Status == "pending")
            .CountAsync(ct);

        return new
        {
            capturedAt = DateTime.UtcNow,
            runningAgents = running.Count,
            queuedRuns = queued.Count,
            completedToday = completed.Count(r => r.CompletedAt >= DateTime.UtcNow.Date),
            totalViolations,
            pendingRecommendations = pendingRecs,
            recentActivity = recentRuns.Take(5).Select(r => new
            {
                runId = r.Id,
                serviceGroupName = sgNames.TryGetValue(r.ServiceGroupId, out var n) ? n : r.ServiceGroupId.ToString(),
                status = r.Status,
                startedAt = r.CreatedAt,
                completedAt = r.CompletedAt,
            }),
            // Emit which agents would be running (maps Microsoft Agent Framework agents to AG-UI events)
            agentCatalog = new[]
            {
                new { name = "DriftDetectionAgent",           capability = "assessDrift",       status = running.Count > 0 ? "active" : "idle" },
                new { name = "BestPracticeEngine",            capability = "evaluateCompliance", status = running.Count > 0 ? "active" : "idle" },
                new { name = "WellArchitectedAssessmentAgent",capability = "scorePillars",       status = "idle" },
                new { name = "FinOpsOptimizerAgent",          capability = "analyzeCosts",       status = "idle" },
                new { name = "CloudNativeMaturityAgent",      capability = "assessMaturity",     status = "idle" },
                new { name = "ServiceHierarchyAnalyzer",      capability = "analyzeHierarchy",   status = "idle" },
            }
        };
    }

    private static string BuildAnalysisSummary(string groupName, object snapshotObj)
    {
        // Serialise and deserialise to access dynamic properties safely
        var json = JsonSerializer.Serialize(snapshotObj, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double? overall = root.TryGetProperty("overallScore", out var o) && o.ValueKind != JsonValueKind.Null
            ? o.GetDouble() : null;
        int violations = root.TryGetProperty("activeViolations", out var v) ? v.GetInt32() : 0;
        int pending = root.TryGetProperty("pendingRecommendations", out var p) ? p.GetInt32() : 0;
        int resources = root.TryGetProperty("resourceCount", out var r) ? r.GetInt32() : 0;

        var sb = new StringBuilder();
        sb.AppendLine($"**Analysis complete for {groupName}**\n");

        if (overall.HasValue)
            sb.AppendLine($"Overall score: **{(overall.Value * 100):F0}%** across {resources} resource(s)");
        else
            sb.AppendLine($"Analysis complete. {resources} resource(s) evaluated.");

        if (violations > 0)
            sb.AppendLine($"• **{violations}** active best-practice violation(s) detected");
        else
            sb.AppendLine("• No best-practice violations detected");

        if (pending > 0)
            sb.AppendLine($"• **{pending}** pending recommendation(s) — review in the **Recommendations** panel");
        else
            sb.AppendLine("• No pending recommendations");

        sb.AppendLine("\n5 agents ran: DriftDetection · BestPractice · WellArchitected · FinOps · ServiceHierarchy");
        return sb.ToString();
    }

    private async Task WriteEvent(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await Response.WriteAsync($"data: {json}\n\n", Encoding.UTF8, ct);
        await Response.Body.FlushAsync(ct);
    }

    private static Guid? TryParseGuid(string value)
        => Guid.TryParse(value, out var parsed) ? parsed : null;

    private static A2AMessage? DeserializeA2AMessage(string? payload)
    {
        var json = NormalizeJson(payload);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<A2AMessage>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeJson(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString()
                : document.RootElement.GetRawText();
        }
        catch
        {
            return payload;
        }
    }

    private static string ExtractToolName(string agentName, JsonElement? payload, JsonElement? metadata)
    {
        if (payload.HasValue && payload.Value.ValueKind == JsonValueKind.Object && payload.Value.TryGetProperty("toolName", out var payloadTool))
        {
            var payloadValue = payloadTool.GetString();
            if (!string.IsNullOrWhiteSpace(payloadValue))
            {
                return payloadValue;
            }
        }

        if (metadata.HasValue && metadata.Value.ValueKind == JsonValueKind.Object && metadata.Value.TryGetProperty("toolName", out var metadataTool))
        {
            var metadataValue = metadataTool.GetString();
            if (!string.IsNullOrWhiteSpace(metadataValue))
            {
                return metadataValue;
            }
        }

        return agentName switch
        {
            "ServiceIntelligence" => "scoreServiceIntelligence",
            "BestPractice" => "evaluateCompliance",
            "DriftDetection" => "assessDrift",
            "ServiceHierarchy" => "analyzeHierarchy",
            "WellArchitected" => "scorePillars",
            "CloudNative" => "assessMaturity",
            "FinOps" => "analyzeCosts",
            "Architecture" => "analyzeArchitecture",
            "Reliability" => "analyzeReliability",
            "Sustainability" => "analyzeSustainability",
            _ => "mediateGovernance"
        };
    }

    private static double? TryExtractDuration(JsonElement? metadata)
    {
        if (metadata.HasValue
            && metadata.Value.ValueKind == JsonValueKind.Object
            && metadata.Value.TryGetProperty("durationMs", out var duration)
            && duration.TryGetDouble(out var elapsedMs))
        {
            return elapsedMs;
        }

        return null;
    }

    private static string ExtractMessageText(JsonElement? payload, string? rawPayload)
    {
        if (payload.HasValue)
        {
            return payload.Value.ValueKind switch
            {
                JsonValueKind.String => payload.Value.GetString() ?? string.Empty,
                JsonValueKind.Object or JsonValueKind.Array => payload.Value.GetRawText(),
                _ => payload.Value.ToString()
            };
        }

        return rawPayload ?? string.Empty;
    }

    private static IEnumerable<string> Chunk(string text, int chunkSize)
    {
        for (var i = 0; i < text.Length; i += chunkSize)
            yield return text[i..Math.Min(i + chunkSize, text.Length)];
    }

    private static bool IsCostTrackable(string? resourceType)
    {
        if (string.IsNullOrEmpty(resourceType)) return false;
        var t = resourceType.ToLowerInvariant();
        return t.Contains("virtualmachine") || t.Contains("storage") ||
               t.Contains("database") || t.Contains("server") ||
               t.Contains("cache") || t.Contains("kubernetes") ||
               t.Contains("containerapp") || t.Contains("functionapp") ||
               t.Contains("webapp");
    }

    private static bool IsRightsizingCandidate(string? resourceType)
    {
        if (string.IsNullOrEmpty(resourceType)) return false;
        var t = resourceType.ToLowerInvariant();
        return t.Contains("virtualmachine") || t.Contains("kubernetes") ||
               t.Contains("database") || t.Contains("cache");
    }
}
