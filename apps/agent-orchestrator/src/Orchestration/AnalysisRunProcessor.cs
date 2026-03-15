using System.Diagnostics;
using System.Text.Json;
using Atlas.AgentOrchestrator.Agents;
using Npgsql;

namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// Loads analysis run context from the database and drives the multi-agent orchestration workflow.
/// </summary>
public class AnalysisRunProcessor
{
    public const string ActivitySourceName = "Atlas.AgentOrchestrator.AnalysisRun";

    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private readonly NpgsqlDataSource _dataSource;
    private readonly MultiAgentOrchestrator _orchestrator;
    private readonly ILogger<AnalysisRunProcessor> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AnalysisRunProcessor(
        NpgsqlDataSource dataSource,
        MultiAgentOrchestrator orchestrator,
        ILogger<AnalysisRunProcessor> logger)
    {
        _dataSource = dataSource;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task ProcessAnalysisAsync(Guid analysisRunId, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("ProcessAnalysis");
        activity?.SetTag("analysis.runId", analysisRunId);

        var (serviceGroupId, snapshotId, correlationId) = await LoadAnalysisRunAsync(analysisRunId, cancellationToken);

        var snapshot = snapshotId.HasValue
            ? await LoadSnapshotAsync(snapshotId.Value, analysisRunId, cancellationToken)
            : new DiscoverySnapshot { Id = Guid.Empty, ServiceGroupId = serviceGroupId, AnalysisRunId = analysisRunId, ResourceCount = 0 };

        var serviceGraphContextJson = await LoadServiceGraphContextAsync(analysisRunId, cancellationToken);

        var context = new AnalysisContext
        {
            ServiceGroupId = serviceGroupId,
            AnalysisRunId = analysisRunId,
            CorrelationId = correlationId,
            Snapshot = snapshot,
            Metadata = serviceGraphContextJson is not null
                ? new Dictionary<string, object> { ["serviceGraphContext"] = serviceGraphContextJson }
                : new Dictionary<string, object>()
        };

        _logger.LogInformation(
            "Starting multi-agent orchestration for run {RunId} (snapshot {SnapshotId}, {ResourceCount} resources)",
            analysisRunId, snapshotId, snapshot.ResourceCount);

        var result = await _orchestrator.OrchestrateAnalysisAsync(
            context,
            CollaborationProtocol.ConcurrentMediator,
            cancellationToken);

        await PersistAgentMessagesAsync(result.Messages, correlationId, cancellationToken);
        await PersistOrchestratorSummaryAsync(analysisRunId, correlationId, result, cancellationToken);
        await PersistFinOpsOrphanDetectionAsync(analysisRunId, correlationId, result.AgentResults, cancellationToken);
        await PersistSustainabilityAssessmentAsync(analysisRunId, correlationId, result.AgentResults, cancellationToken);

        activity?.SetTag("analysis.agentCount", result.AgentResults.Count);
        activity?.SetTag("analysis.durationMs", result.DurationMs);
    }

    /// <summary>
    /// Extracts orphan detection results from the FinOps agent and persists them as a
    /// dedicated agent_message so the control-plane-api can query and surface them.
    /// </summary>
    private async Task PersistFinOpsOrphanDetectionAsync(
        Guid runId,
        Guid correlationId,
        Dictionary<string, object> agentResults,
        CancellationToken ct)
    {
        if (!agentResults.TryGetValue("FinOps", out var finOpsObj)
            || finOpsObj is not Atlas.AgentOrchestrator.Agents.FinOpsAnalysisResult finOpsResult
            || finOpsResult.ComprehensiveOrphanDetection is null)
        {
            return;
        }

        var orphanData = finOpsResult.ComprehensiveOrphanDetection;
        var payload = JsonSerializer.Serialize(new
        {
            OrphanedResourceCount = orphanData.TotalOrphans,
            orphanData.TotalEstimatedMonthlyCost,
            ByResourceType = orphanData.OrphansByType,
            Resources = orphanData.OrphanedResources.Take(200).Select(r => new
            {
                r.ResourceId,
                ResourceType = r.OrphanType,
                r.ResourceName,
                r.ResourceGroup,
                r.Location,
                r.EstimatedMonthlyCost,
                OrphanReason = r.Description,
                DeletionCommand = r.GetDeletionCommand(),
                PowerShellCommand = r.GetPowerShellDeletionCommand()
            })
        }, JsonOptions);
        var metadata = BuildMessageMetadata(null, correlationId, Activity.Current);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_messages
              ("Id", "AnalysisRunId", "MessageId", "AgentName", "AgentRole", "MessageType", "Payload", "EvidenceRefs", "CreatedAt")
            VALUES (@id, @runId, @messageId, @agentName, @agentRole, @messageType, @payload, @evidenceRefs, @createdAt)
            ON CONFLICT DO NOTHING
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.Parameters.AddWithValue("messageId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("agentName", "finops-optimizer-agent");
        cmd.Parameters.AddWithValue("agentRole", "proposer");
        cmd.Parameters.AddWithValue("messageType", "finops.orphanDetection");
        cmd.Parameters.AddWithValue("payload", payload);
        cmd.Parameters.AddWithValue("evidenceRefs", metadata);
        cmd.Parameters.AddWithValue("createdAt", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation(
            "Persisted orphan detection results for run {RunId}: {Count} orphaned resources, ${Cost:F2}/month",
            runId, orphanData.TotalOrphans, orphanData.TotalEstimatedMonthlyCost);
    }

    /// <summary>
    /// Extracts carbon emission data from the Sustainability agent result and persists it as a
    /// dedicated agent_message (MessageType = "sustainability.carbonEmissions") so the
    /// control-plane-api can query and surface real-time carbon footprint data.
    /// </summary>
    private async Task PersistSustainabilityAssessmentAsync(
        Guid runId,
        Guid correlationId,
        Dictionary<string, object> agentResults,
        CancellationToken ct)
    {
        if (!agentResults.TryGetValue("Sustainability", out var sustainabilityObj)
            || sustainabilityObj is not Atlas.AgentOrchestrator.Agents.AgentAnalysisResult sustainResult)
        {
            return;
        }

        // Parse carbon evidence references added by SustainabilityAgent.
        // Evidence keys are written once per run by SustainabilityAgent; we take the last seen value
        // for carbon_monthly_kg in the unlikely case of duplicate entries.
        double monthlyKg = 0;
        bool hasRealData = false;
        var regionEmissions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in sustainResult.EvidenceReferences)
        {
            const string kgPrefix = "carbon_monthly_kg:";
            const string regionPrefix = "carbon_region_kg:"; // format: "carbon_region_kg:{region}:{kg}"

            if (ev.StartsWith(kgPrefix, StringComparison.Ordinal)
                && double.TryParse(ev[kgPrefix.Length..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var kg))
            {
                monthlyKg = kg;
            }
            else if (ev.Equals("carbon_has_real_data:true", StringComparison.Ordinal))
            {
                hasRealData = true;
            }
            else if (ev.StartsWith(regionPrefix, StringComparison.Ordinal))
            {
                var rest = ev[regionPrefix.Length..];
                var sep = rest.LastIndexOf(':');
                if (sep > 0
                    && double.TryParse(rest[(sep + 1)..], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var regionKg))
                {
                    regionEmissions[rest[..sep]] = regionKg;
                }
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            MonthlyKgCO2e = monthlyKg,
            HasRealData = hasRealData,
            SustainabilityScore = sustainResult.Score,
            Confidence = sustainResult.Confidence,
            RegionEmissions = regionEmissions,
            TopFindings = sustainResult.Findings
                .Where(f => f.Severity is "critical" or "high")
                .Take(5)
                .Select(f => new { f.Category, f.Description, f.Severity, f.Impact }),
            TopRecommendations = sustainResult.Recommendations
                .Where(r => r.Priority is "critical" or "high")
                .Take(3)
                .Select(r => new { r.Priority, r.Category, r.Title, r.Description }),
            AINarrative = sustainResult.AINarrativeSummary
        }, JsonOptions);
        var metadata = BuildMessageMetadata(null, correlationId, Activity.Current);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_messages
              ("Id", "AnalysisRunId", "MessageId", "AgentName", "AgentRole", "MessageType", "Payload", "EvidenceRefs", "CreatedAt")
            VALUES (@id, @runId, @messageId, @agentName, @agentRole, @messageType, @payload, @evidenceRefs, @createdAt)
            ON CONFLICT DO NOTHING
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.Parameters.AddWithValue("messageId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("agentName", "sustainability-agent");
        cmd.Parameters.AddWithValue("agentRole", "analyst");
        cmd.Parameters.AddWithValue("messageType", "sustainability.carbonEmissions");
        cmd.Parameters.AddWithValue("payload", payload);
        cmd.Parameters.AddWithValue("evidenceRefs", metadata);
        cmd.Parameters.AddWithValue("createdAt", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation(
            "Persisted sustainability assessment for run {RunId}: {MonthlyKg:F2} kg CO₂e/month (real data: {HasRealData}), score {Score:F0}",
            runId, monthlyKg, hasRealData, sustainResult.Score);
    }

    private async Task<(Guid serviceGroupId, Guid? snapshotId, Guid correlationId)> LoadAnalysisRunAsync(Guid runId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "ServiceGroupId", "SnapshotId", "CorrelationId" FROM analysis_runs WHERE "Id" = @id""";
        cmd.Parameters.AddWithValue("id", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Analysis run {runId} not found.");
        return (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetGuid(2));
    }

    private async Task<DiscoverySnapshot> LoadSnapshotAsync(Guid snapshotId, Guid analysisRunId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id", "ServiceGroupId", "ResourceCount", "ResourceInventory"
            FROM discovery_snapshots WHERE "Id" = @id
            """;
        cmd.Parameters.AddWithValue("id", snapshotId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new DiscoverySnapshot { Id = snapshotId, ServiceGroupId = Guid.Empty, AnalysisRunId = analysisRunId, ResourceCount = 0 };

        return new DiscoverySnapshot
        {
            Id = reader.GetGuid(0),
            ServiceGroupId = reader.GetGuid(1),
            AnalysisRunId = analysisRunId,
            ResourceCount = reader.GetInt32(2),
            ResourceInventory = reader.IsDBNull(3) ? null : reader.GetString(3)
        };
    }

    private async Task<string?> LoadServiceGraphContextAsync(Guid analysisRunId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Payload" FROM agent_messages
            WHERE "AnalysisRunId" = @runId AND "MessageType" = 'serviceGraphContext'
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("runId", analysisRunId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? null : (string)result;
    }

    private async Task PersistAgentMessagesAsync(List<AgentMessage> messages, Guid correlationId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        foreach (var msg in messages)
        {
            var enrichedMetadata = BuildMessageMetadata(msg.Metadata, correlationId, Activity.Current);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_messages
                  ("Id", "AnalysisRunId", "MessageId", "AgentName", "AgentRole", "MessageType", "Payload", "EvidenceRefs", "CreatedAt")
                VALUES (@id, @runId, @messageId, @agentName, @agentRole, @messageType, @payload, @evidenceRefs, @createdAt)
                """;
            cmd.Parameters.AddWithValue("id", msg.Id != Guid.Empty ? msg.Id : Guid.NewGuid());
            cmd.Parameters.AddWithValue("runId", msg.AnalysisRunId);
            cmd.Parameters.AddWithValue("messageId", Guid.NewGuid());
            cmd.Parameters.AddWithValue("agentName", (object?)(msg.AgentName ?? msg.FromAgent) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("agentRole", ResolveAgentRole(msg));
            cmd.Parameters.AddWithValue("messageType", msg.MessageType);
            cmd.Parameters.AddWithValue("payload", JsonSerializer.Serialize(msg.Content, JsonOptions));
            cmd.Parameters.AddWithValue("evidenceRefs", enrichedMetadata);
            cmd.Parameters.AddWithValue("createdAt", msg.CreatedAt == default ? DateTime.UtcNow : msg.CreatedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string BuildMessageMetadata(
        string? existingMetadata,
        Guid correlationId,
        Activity? activity,
        Dictionary<string, object?>? extra = null)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(existingMetadata))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(existingMetadata, JsonOptions);
                if (parsed is not null)
                {
                    foreach (var (key, value) in parsed)
                    {
                        metadata[key] = value;
                    }
                }
            }
            catch
            {
                metadata["rawMetadata"] = existingMetadata;
            }
        }

        if (correlationId != Guid.Empty)
        {
            metadata["correlationId"] = correlationId.ToString("D");
        }

        if (activity is not null)
        {
            metadata["traceId"] = activity.TraceId.ToString();
            metadata["spanId"] = activity.SpanId.ToString();
            metadata["traceParent"] = activity.Id;
        }

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                metadata[key] = value;
            }
        }

        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    private static string ResolveAgentRole(AgentMessage message)
    {
        if (message.FromAgent.Equals("MultiAgentOrchestrator", StringComparison.OrdinalIgnoreCase))
        {
            return "orchestrator";
        }

        if (message.AgentName?.Contains("Mediator", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "mediator";
        }

        return "executor";
    }

    private async Task PersistOrchestratorSummaryAsync(Guid runId, Guid correlationId, AgentCollaborationResult result, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            result.DurationMs,
            agentCount = result.AgentResults.Count,
            status = result.Session.Status,
            outcome = result.FinalOutcome
        }, JsonOptions);
        var metadata = BuildMessageMetadata(null, correlationId, Activity.Current);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_messages
              ("Id", "AnalysisRunId", "MessageId", "AgentName", "AgentRole", "MessageType", "Payload", "EvidenceRefs", "CreatedAt")
            VALUES (@id, @runId, @messageId, @agentName, @agentRole, @messageType, @payload, @evidenceRefs, @createdAt)
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.Parameters.AddWithValue("messageId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("agentName", "MultiAgentOrchestrator");
        cmd.Parameters.AddWithValue("agentRole", "orchestrator");
        cmd.Parameters.AddWithValue("messageType", "session-summary");
        cmd.Parameters.AddWithValue("payload", payload);
        cmd.Parameters.AddWithValue("evidenceRefs", metadata);
        cmd.Parameters.AddWithValue("createdAt", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
