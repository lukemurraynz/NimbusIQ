using Npgsql;

namespace Atlas.AgentOrchestrator.Orchestration;

public class Worker(
    ILogger<Worker> logger,
    NpgsqlDataSource dataSource,
    AnalysisRunProcessor processor) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // Runs stuck in 'orchestrating' longer than this are assumed crashed and recovered
    private static readonly TimeSpan StuckRunTimeout = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Agent orchestration worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverStuckRunsAsync(stoppingToken);
                await ProcessPendingRunsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during agent orchestration poll cycle.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
        logger.LogInformation("Agent orchestration worker stopped.");
    }

    private async Task ProcessPendingRunsAsync(CancellationToken ct)
    {
        var runIds = await FindPendingRunsAsync(ct);
        foreach (var runId in runIds)
        {
            if (ct.IsCancellationRequested) break;
            if (!await TryClaimRunAsync(runId, ct)) continue;

            try
            {
                logger.LogInformation("Processing analysis run {RunId}", runId);
                await processor.ProcessAnalysisAsync(runId, ct);
                await MarkRunCompletedAsync(runId, ct);
                logger.LogInformation("Completed agent orchestration for run {RunId}", runId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to orchestrate analysis run {RunId}", runId);
                await ReturnRunToCompletedAsync(runId, ct);
            }
        }
    }

    private async Task<List<Guid>> FindPendingRunsAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ar."Id"
            FROM analysis_runs ar
            WHERE ar."Status" = 'completed'
              AND NOT EXISTS (
                SELECT 1 FROM agent_messages am
                WHERE am."AnalysisRunId" = ar."Id"
                  AND am."AgentRole" = 'orchestrator'
              )
            ORDER BY ar."CreatedAt"
            LIMIT 5
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    // Optimistic concurrency: only one worker instance claims a run.
    // Records the claim time via StartedAt so stuck runs can be recovered.
    private async Task<bool> TryClaimRunAsync(Guid runId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE analysis_runs SET "Status" = 'orchestrating', "StartedAt" = @claimedAt
            WHERE "Id" = @id AND "Status" = 'completed'
            """;
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("claimedAt", DateTime.UtcNow);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Recovers runs stuck in 'orchestrating' state beyond the timeout threshold.
    /// This prevents runs from being permanently stuck if the processor crashed.
    /// </summary>
    private async Task RecoverStuckRunsAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE analysis_runs SET "Status" = 'completed'
                WHERE "Status" = 'orchestrating'
                  AND COALESCE("StartedAt", "CreatedAt") < @cutoff
                RETURNING "Id"
                """;
            cmd.Parameters.AddWithValue("cutoff", DateTime.UtcNow - StuckRunTimeout);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var recoveredId = reader.GetGuid(0);
                logger.LogWarning("Recovered stuck orchestrating run {RunId} — returning to completed for retry.", recoveredId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recover stuck orchestrating runs.");
        }
    }

    private async Task MarkRunCompletedAsync(Guid runId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE analysis_runs SET "Status" = 'completed', "CompletedAt" = @completedAt
            WHERE "Id" = @id AND "Status" = 'orchestrating'
            """;
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("completedAt", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ReturnRunToCompletedAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE analysis_runs SET "Status" = 'completed'
                WHERE "Id" = @id AND "Status" = 'orchestrating'
                """;
            cmd.Parameters.AddWithValue("id", runId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to revert run {RunId} status after orchestration error.", runId);
        }
    }
}
