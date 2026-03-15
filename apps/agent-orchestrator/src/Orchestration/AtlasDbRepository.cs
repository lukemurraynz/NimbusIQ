using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// Repository for Atlas database operations (T024 - Discovery Persistence)
/// </summary>
public class AtlasDbRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<AtlasDbRepository> _logger;

    public AtlasDbRepository(NpgsqlDataSource dataSource, ILogger<AtlasDbRepository> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>
    /// Update analysis run status (T024)
    /// </summary>
    public async Task UpdateAnalysisRunStatusAsync(
        Guid analysisRunId,
        string status,
        DateTime? startedAt = null,
        DateTime? completedAt = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE ""AnalysisRuns""
            SET 
                ""Status"" = @status,
                ""StartedAt"" = COALESCE(@startedAt, ""StartedAt""),
                ""CompletedAt"" = COALESCE(@completedAt, ""CompletedAt"")
            WHERE ""Id"" = @id", conn);

        cmd.Parameters.AddWithValue("id", analysisRunId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("startedAt", (object?)startedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("completedAt", (object?)completedAt ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Updated analysis run {AnalysisRunId} to status {Status}",
            analysisRunId, status);
    }

    /// <summary>
    /// Save discovery snapshot (T024)
    /// </summary>
    public async Task<Guid> SaveDiscoverySnapshotAsync(
        Guid serviceGroupId,
        Guid analysisRunId,
        Guid correlationId,
        DiscoveryResult discoveryResult,
        CancellationToken cancellationToken = default)
    {
        var snapshotId = Guid.NewGuid();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Insert discovery snapshot
            await using var snapshotCmd = new NpgsqlCommand(@"
                INSERT INTO ""DiscoverySnapshots"" (
                    ""Id"", ""ServiceGroupId"", ""AnalysisRunId"", ""CorrelationId"",
                    ""SnapshotTime"", ""InventoryHash"", ""ResourceInventory"",
                    ""TelemetryHealth"", ""DependencyGraph"", ""Status"",
                    ""ResourceCount"", ""DependencyCount"", ""AnomalyCount"",
                    ""CapturedBy"", ""CreatedAt""
                ) VALUES (
                    @id, @serviceGroupId, @analysisRunId, @correlationId,
                    @snapshotTime, @inventoryHash, @resourceInventory,
                    @telemetryHealth, @dependencyGraph, @status,
                    @resourceCount, @dependencyCount, @anomalyCount,
                    @capturedBy, @createdAt
                )", conn, transaction);

            snapshotCmd.Parameters.AddWithValue("id", snapshotId);
            snapshotCmd.Parameters.AddWithValue("serviceGroupId", serviceGroupId);
            snapshotCmd.Parameters.AddWithValue("analysisRunId", analysisRunId);
            snapshotCmd.Parameters.AddWithValue("correlationId", correlationId);
            snapshotCmd.Parameters.AddWithValue("snapshotTime", DateTimeOffset.UtcNow);
            snapshotCmd.Parameters.AddWithValue("inventoryHash", ComputeInventoryHash(discoveryResult));
            snapshotCmd.Parameters.AddWithValue("resourceInventory", JsonSerializer.Serialize(discoveryResult.Resources));
            snapshotCmd.Parameters.AddWithValue("telemetryHealth", JsonSerializer.Serialize(discoveryResult.TelemetryContext));
            snapshotCmd.Parameters.AddWithValue("dependencyGraph", JsonSerializer.Serialize(discoveryResult.DependencyMap));
            snapshotCmd.Parameters.AddWithValue("status", DetermineSnapshotStatus(discoveryResult));
            snapshotCmd.Parameters.AddWithValue("resourceCount", discoveryResult.Resources.Count);
            snapshotCmd.Parameters.AddWithValue("dependencyCount", discoveryResult.DependencyMap.Count);
            snapshotCmd.Parameters.AddWithValue("anomalyCount", discoveryResult.Anomalies.Count);
            snapshotCmd.Parameters.AddWithValue("capturedBy", "DiscoveryWorkflow");
            snapshotCmd.Parameters.AddWithValue("createdAt", DateTime.UtcNow);

            await snapshotCmd.ExecuteNonQueryAsync(cancellationToken);

            // Insert discovered resources
            foreach (var resource in discoveryResult.Resources)
            {
                await using var resourceCmd = new NpgsqlCommand(@"
                    INSERT INTO ""DiscoveredResources"" (
                        ""Id"", ""SnapshotId"", ""AzureResourceId"", ""ResourceType"",
                        ""ResourceName"", ""Region"", ""Sku"", ""Metadata"",
                        ""TelemetryState"", ""CreatedAt""
                    ) VALUES (
                        @id, @snapshotId, @azureResourceId, @resourceType,
                        @resourceName, @region, @sku, @metadata,
                        @telemetryState, @createdAt
                    )", conn, transaction);

                resourceCmd.Parameters.AddWithValue("id", Guid.NewGuid());
                resourceCmd.Parameters.AddWithValue("snapshotId", snapshotId);
                resourceCmd.Parameters.AddWithValue("azureResourceId", resource.Id);
                resourceCmd.Parameters.AddWithValue("resourceType", resource.Type);
                resourceCmd.Parameters.AddWithValue("resourceName", resource.Name);
                resourceCmd.Parameters.AddWithValue("region", (object?)resource.Location ?? DBNull.Value);
                resourceCmd.Parameters.AddWithValue("sku", resource.Sku != null ? JsonSerializer.Serialize(resource.Sku) : DBNull.Value);
                resourceCmd.Parameters.AddWithValue("metadata", resource.Properties != null ? JsonSerializer.Serialize(resource.Properties) : DBNull.Value);
                resourceCmd.Parameters.AddWithValue("telemetryState", DetermineTelemetryState(resource.Id, discoveryResult.TelemetryContext));
                resourceCmd.Parameters.AddWithValue("createdAt", DateTime.UtcNow);

                await resourceCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Update analysis run with snapshot ID
            await using var updateRunCmd = new NpgsqlCommand(@"
                UPDATE ""AnalysisRuns""
                SET ""SnapshotId"" = @snapshotId
                WHERE ""Id"" = @analysisRunId", conn, transaction);

            updateRunCmd.Parameters.AddWithValue("snapshotId", snapshotId);
            updateRunCmd.Parameters.AddWithValue("analysisRunId", analysisRunId);

            await updateRunCmd.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Saved discovery snapshot {SnapshotId} with {ResourceCount} resources for analysis run {AnalysisRunId}",
                snapshotId, discoveryResult.Resources.Count, analysisRunId);

            return snapshotId;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Failed to save discovery snapshot for analysis run {AnalysisRunId}", analysisRunId);
            throw;
        }
    }

    /// <summary>
    /// Save agent message with scores (T025)
    /// </summary>
    public async Task SaveAgentMessageAsync(
        Guid analysisRunId,
        string agentName,
        string agentRole,
        string messageType,
        object payload,
        Guid? parentMessageId = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO ""AgentMessages"" (
                ""Id"", ""AnalysisRunId"", ""MessageId"", ""ParentMessageId"",
                ""AgentName"", ""AgentRole"", ""MessageType"", ""Payload"",
                ""SentAt"", ""CreatedAt""
            ) VALUES (
                @id, @analysisRunId, @messageId, @parentMessageId,
                @agentName, @agentRole, @messageType, @payload,
                @sentAt, @createdAt
            )", conn);

        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("analysisRunId", analysisRunId);
        cmd.Parameters.AddWithValue("messageId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("parentMessageId", (object?)parentMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("agentName", agentName);
        cmd.Parameters.AddWithValue("agentRole", agentRole);
        cmd.Parameters.AddWithValue("messageType", messageType);
        cmd.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload));
        cmd.Parameters.AddWithValue("sentAt", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("createdAt", DateTime.UtcNow);

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Saved agent message from {AgentName} ({MessageType}) for analysis run {AnalysisRunId}",
            agentName, messageType, analysisRunId);
    }

    private string ComputeInventoryHash(DiscoveryResult result)
    {
        // Compute hash of resource IDs for change detection
        var resourceIds = string.Join("|", result.Resources.Select(r => r.Id).OrderBy(id => id));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(resourceIds))).ToLowerInvariant();
    }

    private string DetermineSnapshotStatus(DiscoveryResult result)
    {
        // Determine snapshot status based on telemetry coverage (T020)
        var coverage = result.TelemetryContext.ConfidenceImpact;

        return coverage switch
        {
            >= 0.9m => "completed", // 90%+ coverage = complete
            >= 0.5m => "partial",   // 50-90% coverage = partial
            _ => "failed"           // <50% coverage = failed
        };
    }

    private string DetermineTelemetryState(string resourceId, TelemetryContext context)
    {
        // Determine telemetry state for resource (T020)
        if (context.MetricsAvailable.Contains(resourceId))
        {
            return "healthy";
        }
        else if (context.MissingTelemetry.Contains(resourceId))
        {
            return "missing";
        }
        else
        {
            return "degraded";
        }
    }
}
