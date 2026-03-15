using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Background hosted service that polls the <c>analysis_runs</c> table for queued runs
/// and executes the analysis workflow for each one.
///
/// Design decisions:
/// - Uses a polling loop (no message queue dependency) so the system works out-of-the-box.
/// - Resolves scoped services (<see cref="AnalysisOrchestrationService"/>) via a new DI
///   scope per iteration to avoid DbContext lifetime issues.
/// - <see cref="AnalysisOrchestrationService.ExecuteAsync"/> performs an atomic compare-and-swap
///   (queued → running) so duplicate processing is prevented in multi-replica deployments.
/// - Runs are processed sequentially to avoid overwhelming the Resource Graph API.
/// </summary>
public class BackgroundAnalysisService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundAnalysisService> _logger;

    public BackgroundAnalysisService(
        IServiceScopeFactory scopeFactory,
        ILogger<BackgroundAnalysisService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackgroundAnalysisService started; polling every {Interval}s",
            PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueuedRunsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in BackgroundAnalysisService polling loop");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("BackgroundAnalysisService stopped");
    }

    private async Task ProcessQueuedRunsAsync(CancellationToken stoppingToken)
    {
        // Resolve a fresh scope so we get a new DbContext per poll cycle
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        // Fetch queued runs ordered by creation time.
        // AnalysisOrchestrationService.ExecuteAsync atomically claims each run
        // (queued → running) so concurrent replicas cannot process the same run twice.
        var queuedRuns = await db.AnalysisRuns
            .Where(r => r.Status == AnalysisRunStatus.Queued)
            .OrderBy(r => r.CreatedAt)
            .Take(10) // Process at most 10 at a time per poll cycle
            .Select(r => new { r.Id, r.ServiceGroupId, r.CorrelationId })
            .ToListAsync(stoppingToken);

        if (queuedRuns.Count == 0)
            return;

        _logger.LogInformation(
            "BackgroundAnalysisService found {Count} queued run(s) to process", queuedRuns.Count);

        var orchestrator = scope.ServiceProvider.GetRequiredService<AnalysisOrchestrationService>();

        foreach (var run in queuedRuns)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            _logger.LogInformation(
                "Processing queued analysis run {RunId} for service group {ServiceGroupId} [correlation={CorrelationId}]",
                run.Id, run.ServiceGroupId, run.CorrelationId);

            // ExecuteAsync never throws; failures are recorded as "failed" status.
            await orchestrator.ExecuteAsync(run.Id, stoppingToken);
        }
    }
}
