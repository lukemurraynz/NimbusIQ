using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Azure;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecommendationCitationEnricher = Atlas.ControlPlane.Application.Recommendations.RecommendationCitationEnricher;
using RecommendationConfidenceModel = Atlas.ControlPlane.Application.Recommendations.RecommendationConfidenceModel;
using CostEvidenceSignals = Atlas.ControlPlane.Application.Recommendations.CostEvidenceSignals;
using GroundingProvenance = Atlas.ControlPlane.Application.Recommendations.GroundingProvenance;
using IRecommendationGroundingClient = Atlas.ControlPlane.Application.Recommendations.IRecommendationGroundingClient;
using IAzureMcpValueEvidenceClient = Atlas.ControlPlane.Application.ValueTracking.IAzureMcpValueEvidenceClient;

namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Executes the full analysis workflow for a queued <see cref="AnalysisRun"/>:
///   queued → running → (discovery + scoring) → completed | partial | failed
///
/// Called by <see cref="BackgroundAnalysisService"/> for each pending run.
/// All state transitions are persisted to PostgreSQL so the LRO status endpoint
/// reflects real progress.
/// </summary>
public class AnalysisOrchestrationService
{
    private readonly IDbContextFactory<AtlasDbContext> _dbContextFactory;
    private readonly AzureDiscoveryService _discoveryService;
    private readonly ScoringService _scoringService;
    private readonly AIChatService? _aiChatService;
    private readonly IImpactFactorInsightService? _insightService;
    private readonly IRecommendationGroundingClient? _groundingClient;
    private readonly IAzureMcpValueEvidenceClient? _azureMcpValueEvidenceClient;
    private readonly ILogger<AnalysisOrchestrationService> _logger;
    private readonly IAnalysisEventPublisher _events;

    private const double SecurityThreshold = 0.6;
    private static readonly object RulePackSync = new();
    private static RuleSeed[]? _cachedSeedRules;
    private static string _loadedRulePackVersion = "embedded-default";
    private static string _loadedRulePackSource = "embedded";

    public AnalysisOrchestrationService(
        IDbContextFactory<AtlasDbContext> dbContextFactory,
        AzureDiscoveryService discoveryService,
        ScoringService scoringService,
        ILogger<AnalysisOrchestrationService> logger,
        IAnalysisEventPublisher? events = null,
        AIChatService? aiChatService = null,
        IImpactFactorInsightService? insightService = null,
        IRecommendationGroundingClient? groundingClient = null,
        IAzureMcpValueEvidenceClient? azureMcpValueEvidenceClient = null)
    {
        _dbContextFactory = dbContextFactory;
        _discoveryService = discoveryService;
        _scoringService = scoringService;
        _aiChatService = aiChatService;
        _insightService = insightService;
        _groundingClient = groundingClient;
        _azureMcpValueEvidenceClient = azureMcpValueEvidenceClient;
        _logger = logger;
        _events = events ?? NullAnalysisEventPublisher.Instance;
    }

    // Helper: fire-and-forget broadcast AgentStarted, logging any failures for observability.
    private void BroadcastAgentStarted(string runId, string agentName, string description) =>
        _ = _events.AgentStartedAsync(runId, agentName, description)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogWarning(t.Exception, "Failed to broadcast AgentStarted for {AgentName} in run {RunId}", agentName, runId);
            }, TaskScheduler.Default);

    // Helper: fire-and-forget broadcast AgentCompleted, logging any failures for observability.
    private void BroadcastAgentCompleted(
        string runId, string agentName, bool success, Stopwatch sw,
        int? itemsProcessed = null, double? scoreValue = null, string? summary = null) =>
        _ = _events.AgentCompletedAsync(runId, agentName, success, sw.ElapsedMilliseconds,
                itemsProcessed, scoreValue, summary)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogWarning(t.Exception, "Failed to broadcast AgentCompleted for {AgentName} in run {RunId}", agentName, runId);
            }, TaskScheduler.Default);

    // Helper: fire-and-forget broadcast a single AgentFinding, logging any failures for observability.
    private void BroadcastFinding(
        string runId, string agentName, string category, string severity, string message) =>
        _ = _events.AgentFindingAsync(runId, agentName, category, severity, message)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogWarning(t.Exception, "Failed to broadcast AgentFinding for {AgentName} in run {RunId}", agentName, runId);
            }, TaskScheduler.Default);

    /// <summary>
    /// Executes the analysis workflow for the specified run.
    /// Does not throw; all exceptions are caught and translated to a "failed" status.
    /// </summary>
    public async Task ExecuteAsync(Guid analysisRunId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting analysis execution for run {RunId}", analysisRunId);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var run = await db.AnalysisRuns
            .Include(r => r.ServiceGroup)
                .ThenInclude(sg => sg.Scopes)
            .FirstOrDefaultAsync(r => r.Id == analysisRunId, cancellationToken);

        if (run == null)
        {
            _logger.LogWarning("Analysis run {RunId} not found; skipping", analysisRunId);
            return;
        }

        var correlationId = run.CorrelationId;
        var runIdStr = analysisRunId.ToString();

        try
        {
            // Step 1: Atomically claim the run
            var claimed = await ClaimAndStartRunAsync(db, analysisRunId, correlationId, cancellationToken);
            if (!claimed)
                return;

            // Step 2: Execute discovery phase
            var (snapshot, discoveryResult, bestPracticeViolations) = await ExecuteDiscoveryPhaseAsync(
                db, run, runIdStr, correlationId, cancellationToken);

            // Step 3: Execute analysis phase (scoring, recommendations)
            var (scores, recommendations, categoryScores) = await ExecuteAnalysisPhaseAsync(
                db, run, runIdStr, discoveryResult, bestPracticeViolations, correlationId, cancellationToken);

            // Step 4: Persist final results and timeline events
            await PersistFinalResultsAsync(
                db, run, snapshot, discoveryResult, scores, recommendations, categoryScores,
                bestPracticeViolations, runIdStr, correlationId, cancellationToken);

            _logger.LogInformation(
                "Analysis run {RunId} → {Status} (resources={Count}, partial={IsPartial}) [correlation={CorrelationId}]",
                analysisRunId, run.Status, discoveryResult.Resources.Count,
                discoveryResult.IsPartial, correlationId);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex,
                "Analysis run {RunId} cancelled [correlation={CorrelationId}]",
                analysisRunId, correlationId);

            // Re-fetch a fresh context in case the original context has pending changes
            await using var failDb = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            await SetFailedAsync(failDb, analysisRunId, AnalysisRunStatus.Cancelled, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Analysis run {RunId} failed [correlation={CorrelationId}]",
                analysisRunId, correlationId);

            await using var failDb = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            await SetFailedAsync(failDb, analysisRunId, AnalysisRunStatus.Failed, CancellationToken.None);
        }
    }

    /// <summary>
    /// Atomically claims a queued analysis run and transitions it to "running" status.
    /// Returns false if the run is not in queued state (already claimed or completed).
    /// </summary>
    private async Task<bool> ClaimAndStartRunAsync(
        AtlasDbContext db, Guid analysisRunId, Guid correlationId, CancellationToken cancellationToken)
    {
        var claimed = await db.AnalysisRuns
            .Where(r => r.Id == analysisRunId && r.Status == AnalysisRunStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AnalysisRunStatus.Running)
                .SetProperty(r => r.StartedAt, DateTime.UtcNow),
                cancellationToken);

        if (claimed == 0)
        {
            _logger.LogInformation(
                "Analysis run {RunId} is not in queued state (already claimed or completed); skipping [correlation={CorrelationId}]",
                analysisRunId, correlationId);
            return false;
        }

        _logger.LogInformation(
            "Analysis run {RunId} → running [correlation={CorrelationId}]",
            analysisRunId, correlationId);
        return true;
    }

    /// <summary>
    /// Executes the discovery phase: scans Azure resources, persists snapshot and discovered resources,
    /// builds service graph, and evaluates best-practice rules.
    /// </summary>
    private async Task<(DiscoverySnapshot Snapshot, DiscoveryResult Discovery, IReadOnlyList<BestPracticeViolation> Violations)>
        ExecuteDiscoveryPhaseAsync(
            AtlasDbContext db,
            AnalysisRun run,
            string runIdStr,
            Guid correlationId,
            CancellationToken cancellationToken)
    {
        // Discover Azure resources
        var swDiscovery = Stopwatch.StartNew();
        BroadcastAgentStarted(runIdStr, "DiscoveryAgent", "Scanning Azure resources via Resource Graph");
        var allHierarchyArmIds = await GetHierarchyArmIdsAsync(db, run.ServiceGroup, cancellationToken);

        var discoveryResult = await _discoveryService.DiscoverAsync(
            run.ServiceGroup, allHierarchyArmIds, correlationId, cancellationToken);
        swDiscovery.Stop();

        BroadcastAgentCompleted(runIdStr, "DiscoveryAgent", true, swDiscovery,
            itemsProcessed: discoveryResult.Resources.Count,
            summary: $"Found {discoveryResult.Resources.Count} Azure resource(s)");
        if (discoveryResult.IsPartial)
            BroadcastFinding(runIdStr, "DiscoveryAgent", "Discovery", "warning",
                "Partial discovery: some scopes returned no data. Verify managed-identity permissions.");

        // Persist DiscoverySnapshot
        var inventoryJson = JsonSerializer.Serialize(
            discoveryResult.Resources.Select(r => new
            {
                r.ArmId,
                r.Name,
                r.ResourceType,
                r.Location,
                r.ResourceGroup,
                r.SubscriptionId,
                r.Sku,
                r.Tags,
                r.Kind
            }));

        var inventoryHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(inventoryJson)));

        var snapshot = new DiscoverySnapshot
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = run.ServiceGroupId,
            AnalysisRunId = run.Id,
            CorrelationId = correlationId,
            SnapshotTime = DateTimeOffset.UtcNow,
            InventoryHash = inventoryHash,
            ResourceInventory = inventoryJson,
            Status = discoveryResult.IsPartial ? AnalysisRunStatus.Partial : AnalysisRunStatus.Completed,
            ResourceCount = discoveryResult.Resources.Count,
            DependencyCount = 0,
            AnomalyCount = 0,
            CapturedBy = "AnalysisOrchestrationService",
            CreatedAt = DateTime.UtcNow
        };

        db.DiscoverySnapshots.Add(snapshot);

        // Persist DiscoveredResource rows in batch
        var previousAutoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var resourceEntities = discoveryResult.Resources.Select(resource => new DiscoveredResource
            {
                Id = Guid.NewGuid(),
                SnapshotId = snapshot.Id,
                AzureResourceId = resource.ArmId,
                ResourceType = resource.ResourceType,
                ResourceName = resource.Name,
                Region = resource.Location,
                Sku = resource.Sku,
                Metadata = BuildMetadataJson(resource),
                TelemetryState = "unknown",
                CreatedAt = DateTime.UtcNow
            }).ToList();
            db.DiscoveredResources.AddRange(resourceEntities);
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
        }

        // Link snapshot to run
        run.SnapshotId = snapshot.Id;
        await db.SaveChangesAsync(cancellationToken);

        // Infer dependency graph and persist
        var dependencyEdges = AzureDiscoveryService.InferDependencyEdges(discoveryResult.Resources);
        if (dependencyEdges.Count > 0)
        {
            snapshot.DependencyGraph = JsonSerializer.Serialize(
                dependencyEdges.Select(e => new { sourceId = e.SourceId, targetId = e.TargetId, type = e.Type }));
            snapshot.DependencyCount = dependencyEdges.Count;
            await db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Discovery snapshot {SnapshotId} stored with {Count} resources, {EdgeCount} dependency edges [correlation={CorrelationId}]",
            snapshot.Id, discoveryResult.Resources.Count, dependencyEdges.Count, correlationId);

        // Build service knowledge graph
        var serviceGraphJson = ServiceGraphContextBuilder.Build(run.ServiceGroupId, discoveryResult.Resources);
        db.AgentMessages.Add(new AgentMessage
        {
            Id = Guid.NewGuid(),
            AnalysisRunId = run.Id,
            MessageId = Guid.NewGuid(),
            AgentName = "ServiceGraphContextBuilder",
            AgentRole = "system",
            MessageType = "serviceGraphContext",
            Payload = serviceGraphJson,
            Confidence = 1.0m,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);

        // Evaluate best-practice rules
        var swBp = Stopwatch.StartNew();
        BroadcastAgentStarted(runIdStr, "BestPracticeAgent", "Evaluating compliance against best-practice rules");
        var bestPracticeViolations = await PersistBestPracticeViolationsAsync(db, run, discoveryResult.Resources, cancellationToken);
        swBp.Stop();
        BroadcastAgentCompleted(runIdStr, "BestPracticeAgent", true, swBp,
            summary: "Best-practice evaluation complete");

        return (snapshot, discoveryResult, bestPracticeViolations);
    }

    /// <summary>
    /// Executes the analysis phase: calculates WAF scores, generates recommendations,
    /// and persists agent messages and score snapshots.
    /// </summary>
    private async Task<(ScoreResult Scores, List<Recommendation> Recommendations, (string Name, double Value, string Summary)[] CategoryScores)>
        ExecuteAnalysisPhaseAsync(
            AtlasDbContext db,
            AnalysisRun run,
            string runIdStr,
            DiscoveryResult discoveryResult,
            IReadOnlyList<BestPracticeViolation> bestPracticeViolations,
            Guid correlationId,
            CancellationToken cancellationToken)
    {
        // Calculate scores
        var swScoring = Stopwatch.StartNew();
        BroadcastAgentStarted(runIdStr, "ScoringAgent", "Calculating Well-Architected Framework scores");
        var scores = _scoringService.Calculate(discoveryResult.Resources, correlationId);
        swScoring.Stop();
        BroadcastAgentCompleted(runIdStr, "ScoringAgent", true, swScoring,
            scoreValue: scores.GetAverageScore(),
            summary: $"WAF score: {scores.GetAverageScore():P0}");

        // Map raw dimensions to WAF pillars with category-aligned weights
        var categoryScores = new (string Name, double Value, string Summary)[]
        {
            ("Architecture",
                Math.Round((scores.Completeness * 0.45 + scores.Availability * 0.35 + scores.Security * 0.20), 4),
                $"Based on completeness ({scores.Completeness:P0}), availability ({scores.Availability:P0}), and security ({scores.Security:P0})"),
            ("FinOps",
                Math.Round((scores.CostEfficiency * 0.50 + scores.TaggingCoverage * 0.30 + scores.Utilization * 0.20), 4),
                $"Based on cost efficiency ({scores.CostEfficiency:P0}), tagging coverage ({scores.TaggingCoverage:P0}), and utilization ({scores.Utilization:P0})"),
            ("Reliability",
                Math.Round((scores.Availability * 0.55 + scores.Resiliency * 0.25 + scores.Security * 0.20), 4),
                $"Based on availability ({scores.Availability:P0}), resiliency ({scores.Resiliency:P0}), and security ({scores.Security:P0})"),
            ("Sustainability",
                Math.Round((scores.Utilization * 0.50 + scores.GreenRegionUsage * 0.30 + scores.CostEfficiency * 0.20), 4),
                $"Based on utilization ({scores.Utilization:P0}), green-region usage ({scores.GreenRegionUsage:P0}), and cost efficiency ({scores.CostEfficiency:P0})"),
            ("Security",
                scores.Security,
                $"Based on identity, network, and data protection signals ({scores.Security:P0})")
        };

        var scoreNow = DateTime.UtcNow;
        foreach (var (name, value, summary) in categoryScores)
        {
            var agentName = $"{name}Agent";
            var swCat = Stopwatch.StartNew();
            BroadcastAgentStarted(runIdStr, agentName, $"Evaluating {name} posture");

            db.AgentMessages.Add(new AgentMessage
            {
                Id = Guid.NewGuid(),
                AnalysisRunId = run.Id,
                MessageId = Guid.NewGuid(),
                AgentName = name,
                AgentRole = "executor",
                MessageType = "score",
                Payload = JsonSerializer.Serialize(new
                {
                    category = name.ToLowerInvariant(),
                    score = (int)Math.Round(value * 100),
                    normalized = value,
                    summary,
                    resource_count = scores.ResourceCount,
                    scored_at = scoreNow.ToString("O"),
                    completeness = Math.Round(scores.Completeness * 100),
                    cost_efficiency = Math.Round(scores.CostEfficiency * 100),
                    availability = Math.Round(scores.Availability * 100),
                    security = Math.Round(scores.Security * 100)
                }),
                Confidence = (decimal)value,
                CreatedAt = scoreNow
            });

            swCat.Stop();
            BroadcastAgentCompleted(runIdStr, agentName, true, swCat,
                scoreValue: value,
                summary: $"{name} score: {value:P0}");
        }

        // Persist ScoreSnapshots for time-series tracking
        var prevSnapshots = await db.ScoreSnapshots
            .Where(s => s.ServiceGroupId == run.ServiceGroupId)
            .OrderByDescending(s => s.RecordedAt)
            .ToListAsync(cancellationToken);

        var previousSnapshots = prevSnapshots
            .GroupBy(s => s.Category)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var (name, value, summary) in categoryScores)
        {
            var scoreInt = (int)Math.Round(value * 100);
            string? deltaJson = null;
            if (previousSnapshots.TryGetValue(name, out var prev))
            {
                deltaJson = JsonSerializer.Serialize(new
                {
                    previousScore = prev.Score,
                    delta = scoreInt - prev.Score,
                    previousRunId = prev.AnalysisRunId,
                    previousRecordedAt = prev.RecordedAt.ToString("O")
                });
            }

            db.ScoreSnapshots.Add(new ScoreSnapshot
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = run.ServiceGroupId,
                AnalysisRunId = run.Id,
                Category = name,
                Score = scoreInt,
                Confidence = Math.Round(value, 4),
                Dimensions = JsonSerializer.Serialize(
                    BuildCategoryDimensions(name, scores, Array.Empty<BestPracticeViolation>())),
                DeltaFromPrevious = deltaJson,
                ResourceCount = scores.ResourceCount,
                RecordedAt = scoreNow,
                CreatedAt = scoreNow
            });
        }

        // Generate recommendations
        var swRec = Stopwatch.StartNew();
        BroadcastAgentStarted(runIdStr, "RecommendationAgent", "Generating prioritised recommendations");
        var recommendations = GenerateRecommendations(
            run,
            scores,
            discoveryResult.Resources,
            bestPracticeViolations,
            discoveryResult.AdvisorRecommendations ?? [],
            discoveryResult.PolicyFindings ?? [],
            discoveryResult.DefenderAssessments ?? []);

        // Enrich recommendations with AI-generated descriptions when Azure AI Foundry is available
        if (_aiChatService?.IsAIAvailable == true && recommendations.Count > 0)
        {
            try
            {
                await EnrichRecommendationsWithAIAsync(recommendations, scores, discoveryResult.Resources, cancellationToken);
                var enrichedCandidates = recommendations.Count(r =>
                    string.Equals(r.ConfidenceSource, "heuristic", StringComparison.OrdinalIgnoreCase));
                _logger.LogInformation(
                    "AI-enriched {Count} heuristic recommendation(s) via Azure AI Foundry [correlation={CorrelationId}]",
                    enrichedCandidates, correlationId);
            }
            catch (Exception aiEx)
            {
                _logger.LogWarning(aiEx,
                    "AI enrichment failed for recommendations in run {RunId}; keeping heuristic text [correlation={CorrelationId}]",
                    run.Id, correlationId);
            }
        }

        await RecommendationCitationEnricher.EnrichInPlaceAsync(recommendations, _groundingClient, cancellationToken);
        var costSignals = await BuildCostEvidenceSignalsAsync(db, run.ServiceGroupId, cancellationToken);
        ApplyCompositeConfidence(recommendations, costSignals);
        AttachLineageCheckpoints(recommendations);

        db.AgentMessages.Add(BuildRecommendationQualityMessage(
            run.Id,
            recommendations,
            discoveryResult,
            bestPracticeViolations.Count));

        // Replace stale pending recommendations
        var staleRecs = await db.Recommendations
            .Where(r => r.ServiceGroupId == run.ServiceGroupId && r.Status == "pending")
            .ToListAsync(cancellationToken);
        if (staleRecs.Count > 0)
        {
            db.Recommendations.RemoveRange(staleRecs);
            _logger.LogInformation(
                "Removed {StaleCount} stale pending recommendation(s) for service group {ServiceGroupId} before adding {NewCount} new ones [correlation={CorrelationId}]",
                staleRecs.Count, run.ServiceGroupId, recommendations.Count, correlationId);
        }

        db.Recommendations.AddRange(recommendations);
        swRec.Stop();
        BroadcastAgentCompleted(runIdStr, "RecommendationAgent", true, swRec,
            itemsProcessed: recommendations.Count,
            summary: $"Generated {recommendations.Count} recommendation(s)");
        foreach (var rec in recommendations.Take(3))
            BroadcastFinding(runIdStr, "RecommendationAgent", rec.Category ?? "General",
                rec.Priority switch { "critical" => "critical", "high" => "high", _ => "medium" },
                rec.Title);

        _logger.LogInformation(
            "Generated {Count} recommendation(s) for run {RunId} [correlation={CorrelationId}]",
            recommendations.Count, run.Id, correlationId);

        return (scores, recommendations, categoryScores);
    }

    /// <summary>
    /// Persists final analysis results: drift snapshot, timeline events, and run completion status.
    /// </summary>
    private async Task PersistFinalResultsAsync(
        AtlasDbContext db,
        AnalysisRun run,
        DiscoverySnapshot snapshot,
        DiscoveryResult discoveryResult,
        ScoreResult scores,
        List<Recommendation> recommendations,
        (string Name, double Value, string Summary)[] categoryScores,
        IReadOnlyList<BestPracticeViolation> bestPracticeViolations,
        string runIdStr,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        // Capture drift snapshot
        var driftSnapshot = CreateDriftSnapshot(run.ServiceGroupId, scores, discoveryResult.Resources, bestPracticeViolations);
        db.DriftSnapshots.Add(driftSnapshot);

        _logger.LogInformation(
            "Created drift snapshot for service group {ServiceGroupId}: score={DriftScore:F1}, violations={TotalViolations} [correlation={CorrelationId}]",
            run.ServiceGroupId, driftSnapshot.DriftScore, driftSnapshot.TotalViolations, correlationId);

        // Record analysis_completed timeline event
        var timelineNow = DateTime.UtcNow;
        var avgScore = scores.GetAverageScore();
        var timelineDescription = $"Analysis completed: {discoveryResult.Resources.Count} resources evaluated across " +
            $"{discoveryResult.Resources.Select(r => r.SubscriptionId).Distinct().Count()} subscription(s). " +
            $"Overall WAF score: {avgScore:P0}.";
        var timelineImpact = recommendations.Count > 0
            ? $"{recommendations.Count} recommendation(s) generated — " +
              $"{recommendations.Count(r => r.Priority == "critical" || r.Priority == "high")} high-priority."
            : "No new recommendations — all dimensions above threshold.";
        db.TimelineEvents.Add(new TimelineEvent
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = run.ServiceGroupId,
            AnalysisRunId = run.Id,
            EventType = "analysis_completed",
            EventCategory = "analysis",
            ScoreImpact = avgScore,
            DeltaSummary = recommendations.Count > 0
                ? $"{recommendations.Count(r => r.Priority == "critical" || r.Priority == "high")} high-priority findings"
                : "No new findings",
            EventTime = timelineNow,
            EventPayload = JsonSerializer.Serialize(new
            {
                overallScore = avgScore,
                resourceCount = discoveryResult.Resources.Count,
                isPartial = discoveryResult.IsPartial,
                correlationId,
                description = timelineDescription,
                impact = timelineImpact
            }),
            CreatedAt = timelineNow
        });

        // Granular timeline events for score changes
        var prevSnapshotsForTimeline = await db.ScoreSnapshots
            .Where(s => s.ServiceGroupId == run.ServiceGroupId)
            .OrderByDescending(s => s.RecordedAt)
            .ToListAsync(cancellationToken);
        var previousSnapshotsForTimeline = prevSnapshotsForTimeline
            .GroupBy(s => s.Category)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var (name, value, _) in categoryScores)
        {
            if (previousSnapshotsForTimeline.TryGetValue(name, out var prevSnap))
            {
                var scoreInt = (int)Math.Round(value * 100);
                var scoreDelta = scoreInt - prevSnap.Score;
                if (Math.Abs(scoreDelta) >= 3)
                {
                    db.TimelineEvents.Add(new TimelineEvent
                    {
                        Id = Guid.NewGuid(),
                        ServiceGroupId = run.ServiceGroupId,
                        AnalysisRunId = run.Id,
                        EventType = "score_change",
                        EventCategory = "analysis",
                        ScoreImpact = scoreDelta,
                        DeltaSummary = $"{name} {(scoreDelta > 0 ? "improved" : "declined")} by {Math.Abs(scoreDelta)} points",
                        EventTime = timelineNow,
                        EventPayload = JsonSerializer.Serialize(new
                        {
                            category = name,
                            previousScore = prevSnap.Score,
                            currentScore = scoreInt,
                            delta = scoreDelta
                        }),
                        CreatedAt = timelineNow
                    });
                }
            }
        }

        // Granular timeline events per drift category
        if (bestPracticeViolations.Count > 0)
        {
            var driftGroups = bestPracticeViolations
                .Where(v => !string.IsNullOrWhiteSpace(v.DriftCategory))
                .GroupBy(v => v.DriftCategory!)
                .ToList();

            foreach (var group in driftGroups)
            {
                var critCount = group.Count(v => string.Equals(v.Severity, "Critical", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v.Severity, "High", StringComparison.OrdinalIgnoreCase));

                db.TimelineEvents.Add(new TimelineEvent
                {
                    Id = Guid.NewGuid(),
                    ServiceGroupId = run.ServiceGroupId,
                    AnalysisRunId = run.Id,
                    EventType = "drift_detected",
                    EventCategory = "drift",
                    ScoreImpact = -group.Count(),
                    DeltaSummary = $"{group.Key}: {group.Count()} violation(s), {critCount} high-priority",
                    EventTime = timelineNow,
                    EventPayload = JsonSerializer.Serialize(new
                    {
                        driftCategory = group.Key,
                        violationCount = group.Count(),
                        criticalHighCount = critCount,
                        sampleResources = group.Select(v => v.ResourceId).Distinct().Take(5).ToArray()
                    }),
                    CreatedAt = timelineNow
                });
            }
        }

        // Timeline events for critical/high recommendations
        foreach (var rec in recommendations.Where(r => r.Priority is "critical" or "high").Take(5))
        {
            db.TimelineEvents.Add(new TimelineEvent
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = run.ServiceGroupId,
                AnalysisRunId = run.Id,
                EventType = "recommendation_generated",
                EventCategory = "recommendation",
                ScoreImpact = null,
                DeltaSummary = $"[{rec.Priority}] {rec.Title}",
                EventTime = timelineNow,
                EventPayload = JsonSerializer.Serialize(new
                {
                    recommendationId = rec.Id,
                    title = rec.Title,
                    category = rec.Category,
                    priority = rec.Priority,
                    confidenceSource = rec.ConfidenceSource
                }),
                CreatedAt = timelineNow
            });
        }

        // Transition to completed or partial
        run.Status = discoveryResult.IsPartial ? AnalysisRunStatus.Partial : AnalysisRunStatus.Completed;
        run.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // Broadcast final completion
        _ = _events.RunCompletedAsync(
                runIdStr, run.Status, run.CompletedAt ?? DateTime.UtcNow,
                scores.GetAverageScore(), discoveryResult.Resources.Count)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogWarning(t.Exception, "Failed to broadcast RunCompleted for run {RunId}", runIdStr);
            }, TaskScheduler.Default);
    }

    private static async Task SetFailedAsync(
        AtlasDbContext db, Guid runId, string status, CancellationToken ct)
    {
        var run = await db.AnalysisRuns.FindAsync([runId], ct);
        if (run != null)
        {
            run.Status = status;
            run.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private static string BuildMetadataJson(DiscoveredAzureResource resource)
    {
        return JsonSerializer.Serialize(new
        {
            resource.ResourceGroup,
            resource.SubscriptionId,
            resource.Kind,
            resource.Tags
        });
    }

    /// <summary>
    /// Collects the ExternalKey (ARM ID) of <paramref name="root"/> and all its descendants
    /// via an iterative BFS over <c>ParentServiceGroupId</c> relationships.
    /// Used so the Service Group member query covers the full hierarchy in a single call.
    /// </summary>
    private static async Task<IReadOnlyList<string>> GetHierarchyArmIdsAsync(
        AtlasDbContext db, ServiceGroup root, CancellationToken ct)
    {
        var allArmIds = new List<string> { root.ExternalKey };
        var queue = new Queue<Guid>();
        queue.Enqueue(root.Id);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            var children = await db.ServiceGroups
                .AsNoTracking()
                .Where(sg => sg.ParentServiceGroupId == parentId)
                .Select(sg => new { sg.Id, sg.ExternalKey })
                .ToListAsync(ct);

            foreach (var child in children)
            {
                allArmIds.Add(child.ExternalKey);
                queue.Enqueue(child.Id);
            }
        }

        return allArmIds;
    }

    /// <summary>
    /// Derives actionable recommendations using grounded sources first (rule engine + Azure Advisor),
    /// then supplements with deterministic scoring heuristics when needed.
    /// </summary>
    private static List<Recommendation> GenerateRecommendations(
        AnalysisRun run,
        ScoreResult scores,
        IReadOnlyList<DiscoveredAzureResource> resources,
        IReadOnlyList<BestPracticeViolation> bestPracticeViolations,
        IReadOnlyList<DiscoveredAdvisorRecommendation> advisorRecommendations,
        IReadOnlyList<DiscoveredPolicyFinding> policyFindings,
        IReadOnlyList<DiscoveredDefenderAssessment> defenderAssessments)
    {
        var now = DateTime.UtcNow;
        var recs = new List<Recommendation>();
        var firstResourceId = resources.FirstOrDefault()?.ArmId ?? $"/serviceGroups/{run.ServiceGroupId:D}";

        recs.AddRange(GenerateRuleBasedRecommendations(run, bestPracticeViolations, firstResourceId, now));
        recs.AddRange(GenerateAdvisorRecommendations(run, advisorRecommendations, firstResourceId, now));
        recs.AddRange(GeneratePolicyRecommendations(run, policyFindings, firstResourceId, now));
        recs.AddRange(GenerateDefenderRecommendations(run, defenderAssessments, firstResourceId, now));
        recs.AddRange(GenerateHeuristicRecommendations(run, scores, resources, now));

        return recs
            .GroupBy(r => $"{r.ResourceId}|{r.Title}|{r.RecommendationType}", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
                g.OrderByDescending(r => GroundingRank(r.ConfidenceSource))
                 .ThenByDescending(r => r.Confidence)
                 .First())
            .ToList();
    }

    private static IEnumerable<Recommendation> GenerateRuleBasedRecommendations(
        AnalysisRun run,
        IReadOnlyList<BestPracticeViolation> violations,
        string fallbackResourceId,
        DateTime now)
    {
        foreach (var group in violations
            .Where(v => v.Rule != null)
            .GroupBy(v => v.RuleId)
            .OrderByDescending(g => SeverityWeight(g.First().Severity))
            .Take(20))
        {
            var sample = group.First();
            var rule = sample.Rule;
            if (rule == null)
                continue;

            var affected = group.Select(v => v.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var priority = NormalizePriority(rule.Severity);
            var category = MapRuleCategory(rule);

            yield return new Recommendation
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = run.ServiceGroupId,
                AnalysisRunId = run.Id,
                CorrelationId = run.CorrelationId,
                ResourceId = string.IsNullOrWhiteSpace(sample.ResourceId) ? fallbackResourceId : sample.ResourceId,
                Category = category,
                RecommendationType = category == "FinOps" ? "cost" : "governance",
                ActionType = "optimize",
                TargetEnvironment = "prod",
                Title = rule.Name,
                Description = $"{affected} resource(s) violate rule {rule.RuleId} ({rule.Source}).",
                Rationale = rule.Rationale ?? rule.Description,
                Impact = $"Improves alignment with {rule.Pillar} guidance by remediating {affected} affected resource(s).",
                ProposedChanges = rule.RemediationGuidance ?? "Apply remediation guidance for the violated rule.",
                Summary = $"{rule.Source} rule {rule.RuleId} violation detected on {affected} resource(s).",
                Confidence = SeverityToConfidence(rule.Severity),
                ConfidenceSource = "rule_engine",
                EvidenceReferences = rule.References,
                TriggerReason = "rule_violation",
                ChangeContext = JsonSerializer.Serialize(new
                {
                    ruleId = rule.RuleId,
                    source = rule.Source,
                    pillar = rule.Pillar,
                    affectedResources = affected,
                    driftCategory = sample.DriftCategory,
                    currentFieldLabel = GetCurrentFieldLabel(rule.RuleId),
                    currentValues = group
                        .Select(v => GetViolationCurrentValue(rule.RuleId, v.CurrentState))
                        .Where(v => v != "—")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(v => v)
                        .Take(5)
                        .ToArray(),
                    requiredValue = GetRequiredValueForRule(rule.RuleId)
                }),
                EstimatedImpact = JsonSerializer.Serialize(new
                {
                    costDelta = category == "FinOps" ? 5.0 : 0.0,
                    performanceDelta = 0.0,
                    availabilityDelta = category == "Reliability" ? SeverityWeight(rule.Severity) * 2.0 : 0.0,
                    securityDelta = rule.Pillar.Equals("Security", StringComparison.OrdinalIgnoreCase) ? SeverityWeight(rule.Severity) * 3.0 : 0.0
                }),
                TradeoffProfile = JsonSerializer.Serialize(new
                {
                    improves = new[] { rule.Pillar },
                    degrades = Array.Empty<string>(),
                    neutral = new[] { "CostOptimization" }
                }),
                RiskProfile = JsonSerializer.Serialize(new
                {
                    currentRisk = rule.Severity.ToLowerInvariant(),
                    mitigatedRisk = "low",
                    residualRisk = "minimal",
                    riskCategory = rule.Category
                }),
                ImpactedServices = JsonSerializer.Serialize(
                    group.Select(v => v.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray()),
                ApprovalMode = "single",
                RequiredApprovals = 1,
                ReceivedApprovals = 0,
                Status = "pending",
                Priority = priority,
                CreatedAt = now,
                UpdatedAt = now
            };
        }
    }

    private static IEnumerable<Recommendation> GenerateAdvisorRecommendations(
        AnalysisRun run,
        IReadOnlyList<DiscoveredAdvisorRecommendation> advisorRecommendations,
        string fallbackResourceId,
        DateTime now)
    {
        foreach (var advisor in advisorRecommendations
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .GroupBy(a => $"{a.ResourceId}|{a.RecommendationTypeId}|{a.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(20))
        {
            var category = MapAdvisorCategory(advisor.Category);
            var titlePrefix = "Azure Advisor";
            var title = $"{titlePrefix}: {advisor.Name}";
            var desc = advisor.Description ?? "Azure Advisor identified an actionable improvement opportunity.";
            var remediation = advisor.Remediation ?? "Review the Azure Advisor recommendation details and apply the suggested remediation.";
            var refs = JsonSerializer.Serialize(
                new[]
                {
                    advisor.RecommendationId,
                    advisor.LearnMoreLink ?? "https://learn.microsoft.com/azure/advisor/advisor-overview"
                }.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray());

            yield return new Recommendation
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = run.ServiceGroupId,
                AnalysisRunId = run.Id,
                CorrelationId = run.CorrelationId,
                ResourceId = string.IsNullOrWhiteSpace(advisor.ResourceId) ? fallbackResourceId : advisor.ResourceId!,
                Category = category,
                RecommendationType = category == "FinOps" ? "cost" : "governance",
                ActionType = "optimize",
                TargetEnvironment = "prod",
                Title = title,
                Description = desc,
                Rationale = "Grounded directly in Azure Advisor assessment signals for this subscription/resource set.",
                Impact = remediation,
                ProposedChanges = remediation,
                Summary = $"{advisor.Category ?? "General"} advisor recommendation with {advisor.Impact ?? "unknown"} impact.",
                Confidence = AdvisorImpactToConfidence(advisor.Impact),
                ConfidenceSource = "azure_advisor",
                EvidenceReferences = refs,
                TriggerReason = "advisor",
                ChangeContext = JsonSerializer.Serialize(new
                {
                    advisorCategory = advisor.Category,
                    impact = advisor.Impact,
                    recommendationId = advisor.RecommendationId
                }),
                EstimatedImpact = JsonSerializer.Serialize(new
                {
                    costDelta = category == "FinOps" ? 10.0 : 0.0,
                    performanceDelta = category == "Architecture" ? 5.0 : 0.0,
                    availabilityDelta = category == "Reliability" ? 8.0 : 0.0,
                    securityDelta = 0.0
                }),
                TradeoffProfile = JsonSerializer.Serialize(new
                {
                    improves = new[] { category },
                    degrades = Array.Empty<string>(),
                    neutral = new[] { "Sustainability" }
                }),
                RiskProfile = JsonSerializer.Serialize(new
                {
                    currentRisk = NormalizePriority(advisor.Impact),
                    mitigatedRisk = "low",
                    residualRisk = "minimal",
                    riskCategory = advisor.Category ?? "General"
                }),
                ApprovalMode = "single",
                RequiredApprovals = 1,
                ReceivedApprovals = 0,
                Status = "pending",
                Priority = NormalizePriority(advisor.Impact),
                CreatedAt = now,
                UpdatedAt = now
            };
        }
    }

    private static IEnumerable<Recommendation> GeneratePolicyRecommendations(
        AnalysisRun run,
        IReadOnlyList<DiscoveredPolicyFinding> policyFindings,
        string fallbackResourceId,
        DateTime now)
    {
        foreach (var finding in policyFindings
            .Where(f => !string.IsNullOrWhiteSpace(f.PolicyDefinitionName) || !string.IsNullOrWhiteSpace(f.PolicyAssignmentName))
            .GroupBy(f => $"{f.ResourceId}|{f.PolicyDefinitionId}|{f.PolicyAssignmentId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(20))
        {
            var titleCore = finding.PolicyDefinitionName ?? finding.PolicyAssignmentName ?? "Policy non-compliance";
            var remediation = "Review policy assignment details and remediate the resource configuration to reach compliance.";
            var refs = JsonSerializer.Serialize(
                new[]
                {
                    finding.PolicyDefinitionId,
                    finding.PolicyAssignmentId,
                    "https://learn.microsoft.com/azure/governance/policy/overview"
                }.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray());

            var priority = PolicyPriority(finding);
            var impactPct = priority == "critical" ? 8.0 : priority == "high" ? 5.0 : 3.0;

            yield return new Recommendation
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = run.ServiceGroupId,
                AnalysisRunId = run.Id,
                CorrelationId = run.CorrelationId,
                ResourceId = string.IsNullOrWhiteSpace(finding.ResourceId) ? fallbackResourceId : finding.ResourceId!,
                Category = "Architecture",
                RecommendationType = "governance",
                ActionType = "optimize",
                TargetEnvironment = "prod",
                Title = $"Azure Policy: {titleCore}",
                Description = finding.Description ?? "Azure Policy reported this resource as non-compliant.",
                Rationale = "Grounded in Azure Policy compliance state for the discovered scope.",
                Impact = remediation,
                ProposedChanges = remediation,
                Summary = $"{finding.ComplianceState ?? "NonCompliant"} policy state detected.",
                Confidence = 0.86m,
                ConfidenceSource = "azure_policy",
                EvidenceReferences = refs,
                EstimatedImpact = JsonSerializer.Serialize(new
                {
                    costDelta = 0.0,
                    performanceDelta = 0.0,
                    availabilityDelta = impactPct,
                    securityDelta = impactPct,
                    implementationCost = "Low",
                    timeToImplement = "1-2 hours"
                }),
                ChangeContext = JsonSerializer.Serialize(new
                {
                    policyDefinitionId = finding.PolicyDefinitionId,
                    policyAssignmentId = finding.PolicyAssignmentId,
                    complianceState = finding.ComplianceState
                }),
                TradeoffProfile = JsonSerializer.Serialize(new
                {
                    improves = new[] { "Architecture" },
                    degrades = Array.Empty<string>(),
                    neutral = new[] { "FinOps", "Sustainability" }
                }),
                RiskProfile = JsonSerializer.Serialize(new
                {
                    currentRisk = priority,
                    mitigatedRisk = "low",
                    residualRisk = "minimal",
                    riskCategory = "Compliance"
                }),
                ImpactedServices = JsonSerializer.Serialize(
                    new[] { finding.ResourceId }.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray()),
                TriggerReason = "policy",
                ApprovalMode = "single",
                RequiredApprovals = 1,
                ReceivedApprovals = 0,
                Status = "pending",
                Priority = priority,
                CreatedAt = now,
                UpdatedAt = now
            };
        }
    }

    private static IEnumerable<Recommendation> GenerateDefenderRecommendations(
        AnalysisRun run,
        IReadOnlyList<DiscoveredDefenderAssessment> defenderAssessments,
        string fallbackResourceId,
        DateTime now)
    {
        foreach (var assessment in defenderAssessments
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) &&
                        string.Equals(a.StatusCode, "Unhealthy", StringComparison.OrdinalIgnoreCase))
            .GroupBy(a => $"{a.ResourceId}|{a.AssessmentId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(20))
        {
            var remediation = assessment.Remediation ?? "Apply the Defender for Cloud remediation guidance in Azure Portal.";
            var refs = JsonSerializer.Serialize(
                new[]
                {
                    assessment.AssessmentId,
                    assessment.LearnMoreLink ?? "https://learn.microsoft.com/azure/defender-for-cloud/recommendations-reference"
                }.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray());

            var severity = NormalizePriority(assessment.Severity);
            var defImpactPct = severity == "critical" ? 10.0 : severity == "high" ? 6.0 : 3.0;

            yield return new Recommendation
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = run.ServiceGroupId,
                AnalysisRunId = run.Id,
                CorrelationId = run.CorrelationId,
                ResourceId = string.IsNullOrWhiteSpace(assessment.ResourceId) ? fallbackResourceId : assessment.ResourceId!,
                Category = "Architecture",
                RecommendationType = "governance",
                ActionType = "optimize",
                TargetEnvironment = "prod",
                Title = $"Defender for Cloud: {assessment.Name}",
                Description = assessment.Description ?? "Defender for Cloud reported an unhealthy assessment that requires remediation.",
                Rationale = "Grounded in Defender for Cloud assessment status for this discovered scope.",
                Impact = remediation,
                ProposedChanges = remediation,
                Summary = $"{assessment.Severity ?? "Unknown"} Defender assessment is unhealthy.",
                Confidence = DefenderSeverityToConfidence(assessment.Severity),
                ConfidenceSource = "defender_for_cloud",
                EvidenceReferences = refs,
                EstimatedImpact = JsonSerializer.Serialize(new
                {
                    costDelta = 0.0,
                    performanceDelta = 0.0,
                    availabilityDelta = defImpactPct,
                    securityDelta = defImpactPct,
                    implementationCost = severity == "critical" ? "Medium" : "Low",
                    timeToImplement = severity == "critical" ? "2-4 hours" : "1-2 hours"
                }),
                ChangeContext = JsonSerializer.Serialize(new
                {
                    assessmentId = assessment.AssessmentId,
                    severity = assessment.Severity,
                    statusCode = assessment.StatusCode
                }),
                TradeoffProfile = JsonSerializer.Serialize(new
                {
                    improves = new[] { "Security", "Architecture" },
                    degrades = Array.Empty<string>(),
                    neutral = new[] { "FinOps" }
                }),
                RiskProfile = JsonSerializer.Serialize(new
                {
                    currentRisk = severity,
                    mitigatedRisk = "low",
                    residualRisk = "minimal",
                    riskCategory = "Security"
                }),
                ImpactedServices = JsonSerializer.Serialize(
                    new[] { assessment.ResourceId }.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray()),
                TriggerReason = "rule_violation",
                ApprovalMode = "single",
                RequiredApprovals = 1,
                ReceivedApprovals = 0,
                Status = "pending",
                Priority = severity,
                CreatedAt = now,
                UpdatedAt = now
            };
        }
    }

    /// <summary>
    /// Deterministic fallback/supplement recommendations from scored metadata dimensions.
    /// </summary>
    private static List<Recommendation> GenerateHeuristicRecommendations(
        AnalysisRun run,
        ScoreResult scores,
        IReadOnlyList<DiscoveredAzureResource> resources,
        DateTime now)
    {
        var recs = new List<Recommendation>();

        if (resources.Count == 0)
            return recs;

        var firstResourceId = resources[0].ArmId;

        // Availability: resources on Basic/Free/Developer SKUs lack HA guarantees
        var lowSkuResources = resources
            .Where(r => r.Sku != null && (
                r.Sku.Contains("Basic", StringComparison.OrdinalIgnoreCase) ||
                r.Sku.Contains("Free", StringComparison.OrdinalIgnoreCase) ||
                r.Sku.Contains("Developer", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (scores.Availability < 0.8 || lowSkuResources.Count > 0)
        {
            recs.Add(new Recommendation
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = run.ServiceGroupId,
                AnalysisRunId = run.Id,
                CorrelationId = run.CorrelationId,
                ResourceId = lowSkuResources.Count > 0 ? lowSkuResources[0].ArmId : firstResourceId,
                Category = "Reliability",
                RecommendationType = "reliability",
                ActionType = "upgrade",
                TargetEnvironment = "prod",
                Title = "Upgrade to production-grade SKU tiers",
                Description = $"{(lowSkuResources.Count > 0 ? lowSkuResources.Count : resources.Count)} resource(s) " +
                              "use Basic/Free/Developer SKUs that lack production SLA guarantees.",
                Rationale = "Basic tier SKUs do not include SLA commitments or redundancy features required for production.",
                Impact = "Upgrading to Standard or Premium tiers improves availability from ~95% to 99.9%+.",
                ProposedChanges = "Migrate identified resources from Basic/Free/Developer to Standard or higher SKU tiers.",
                Summary = $"Availability score {scores.Availability:P0} — upgrade low-tier resources to improve SLA.",
                Confidence = (decimal)Math.Round(1.0 - scores.Availability, 4),
                ConfidenceSource = "heuristic",
                TriggerReason = "rule_violation",
                EstimatedImpact = JsonSerializer.Serialize(new
                {
                    costDelta = -15.0,
                    performanceDelta = 5.0,
                    availabilityDelta = Math.Round((0.999 - scores.Availability) * 100, 1),
                    securityDelta = 0.0
                }),
                TradeoffProfile = JsonSerializer.Serialize(new
                {
                    improves = new[] { "Reliability", "Availability" },
                    degrades = new[] { "CostOptimization" },
                    neutral = new[] { "Security" }
                }),
                RiskProfile = JsonSerializer.Serialize(new
                {
                    currentRisk = scores.Availability < 0.5 ? "high" : "medium",
                    mitigatedRisk = "low",
                    residualRisk = "minimal",
                    riskCategory = "Reliability"
                }),
                ImpactedServices = JsonSerializer.Serialize(
                    lowSkuResources.Select(r => r.ArmId).Take(10).ToArray()),
                ApprovalMode = "single",
                RequiredApprovals = 1,
                ReceivedApprovals = 0,
                Status = "pending",
                Priority = scores.Availability < 0.5 ? "high" : "medium",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        // Cost efficiency: only fires when a significant fraction of billable resources lack cost attribution.
        // Non-billable types (managed identities, role assignments, etc.) are excluded from scoring.
        if (scores.CostEfficiency < 0.6)
        {
            var billableWithoutCost = resources
                .Where(r => !IsNonBillableResourceType(r.ResourceType))
                .Where(r => !IsBillableResourceType(r.ResourceType))
                .Take(3)
                .ToList();
            var exampleResources = billableWithoutCost.Count > 0
                ? string.Join(", ", billableWithoutCost.Select(r => r.ResourceType.Split('/').LastOrDefault() ?? r.ResourceType))
                : "various resource types";

            recs.Add(new Recommendation
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = run.ServiceGroupId,
                AnalysisRunId = run.Id,
                CorrelationId = run.CorrelationId,
                ResourceId = billableWithoutCost.Count > 0 ? billableWithoutCost[0].ArmId : firstResourceId,
                Category = "FinOps",
                RecommendationType = "cost",
                ActionType = "optimize",
                TargetEnvironment = "prod",
                Title = "Improve cost tracking coverage",
                Description = $"Only {scores.CostEfficiency:P0} of billable resources are standard types visible in Azure Cost Management. " +
                              $"Resource types without cost tracking include: {exampleResources}.",
                Rationale = "Incomplete cost tracking prevents accurate FinOps analysis and right-sizing recommendations.",
                Impact = "Full cost visibility enables right-sizing, reserved instance planning, and budget alerts.",
                ProposedChanges = "Assign cost-center tags to all resources and enable Azure Cost Management exports.",
                Summary = $"Cost efficiency score {scores.CostEfficiency:P0} — improve cost attribution coverage.",
                Confidence = (decimal)Math.Round(1.0 - scores.CostEfficiency, 4),
                ConfidenceSource = "heuristic",
                TriggerReason = "cost_anomaly",
                EstimatedImpact = JsonSerializer.Serialize(new
                {
                    costDelta = 20.0,
                    performanceDelta = 0.0,
                    availabilityDelta = 0.0,
                    securityDelta = 0.0
                }),
                TradeoffProfile = JsonSerializer.Serialize(new
                {
                    improves = new[] { "CostOptimization", "OperationalExcellence" },
                    degrades = Array.Empty<string>(),
                    neutral = new[] { "Reliability", "Security" }
                }),
                RiskProfile = JsonSerializer.Serialize(new
                {
                    currentRisk = "medium",
                    mitigatedRisk = "low",
                    residualRisk = "minimal",
                    riskCategory = "FinOps"
                }),
                ApprovalMode = "single",
                RequiredApprovals = 1,
                ReceivedApprovals = 0,
                Status = "pending",
                Priority = "medium",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        // Completeness: resources without regional metadata are harder to govern
        if (scores.Completeness < 0.8)
        {
            recs.Add(new Recommendation
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = run.ServiceGroupId,
                AnalysisRunId = run.Id,
                CorrelationId = run.CorrelationId,
                ResourceId = firstResourceId,
                Category = "Architecture",
                RecommendationType = "governance",
                ActionType = "optimize",
                TargetEnvironment = "prod",
                Title = "Improve resource metadata and regional placement",
                Description = $"Only {scores.Completeness:P0} of resources have regional assignments. " +
                              "Global or unassigned resources reduce analysis accuracy.",
                Rationale = "Complete metadata is required for WAF assessments, cost attribution, and compliance reporting.",
                Impact = "Tagging and region assignment enables drift detection, cost management, and WAF assessments.",
                ProposedChanges = "Assign regions and required tags to all resources. Use Azure Policy to enforce tagging.",
                Summary = $"Completeness score {scores.Completeness:P0} — tag and place ungrouped resources.",
                Confidence = (decimal)Math.Round(1.0 - scores.Completeness, 4),
                ConfidenceSource = "heuristic",
                TriggerReason = "drift_detected",
                EstimatedImpact = JsonSerializer.Serialize(new
                {
                    costDelta = 5.0,
                    performanceDelta = 0.0,
                    availabilityDelta = 3.0,
                    securityDelta = 2.0
                }),
                TradeoffProfile = JsonSerializer.Serialize(new
                {
                    improves = new[] { "OperationalExcellence", "CostOptimization" },
                    degrades = Array.Empty<string>(),
                    neutral = new[] { "Reliability" }
                }),
                RiskProfile = JsonSerializer.Serialize(new
                {
                    currentRisk = scores.Completeness < 0.5 ? "high" : "low",
                    mitigatedRisk = "minimal",
                    residualRisk = "minimal",
                    riskCategory = "Governance"
                }),
                ApprovalMode = "single",
                RequiredApprovals = 1,
                ReceivedApprovals = 0,
                Status = "pending",
                Priority = scores.Completeness < 0.5 ? "high" : "low",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        // Security: recommend improvements when security score is below threshold
        if (scores.Security < SecurityThreshold)
        {
            recs.Add(new Recommendation
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = run.ServiceGroupId,
                AnalysisRunId = run.Id,
                CorrelationId = run.CorrelationId,
                ResourceId = firstResourceId,
                Category = "Architecture",
                RecommendationType = "governance",
                ActionType = "optimize",
                TargetEnvironment = "prod",
                Title = "Improve security posture across resources",
                Description = $"Security score {scores.Security:P0} is below the {SecurityThreshold:P0} threshold. Review public access settings, TLS enforcement, managed identities, and firewall rules.",
                Rationale = "Resources with public network access, missing TLS enforcement, or absent managed identities reduce the overall security posture.",
                Impact = "Restricting public access, enforcing TLS, using managed identities, and enabling firewalls reduces attack surface.",
                ProposedChanges = "Disable public network access where possible, enforce HTTPS/TLS, assign managed identities, and configure firewalls or private endpoints.",
                Summary = $"Security score {scores.Security:P0} — harden network and identity configuration.",
                Confidence = (decimal)Math.Round(1.0 - scores.Security, 4),
                ConfidenceSource = "heuristic",
                TriggerReason = "rule_violation",
                EstimatedImpact = JsonSerializer.Serialize(new
                {
                    costDelta = -2.0,
                    performanceDelta = 0.0,
                    availabilityDelta = 0.0,
                    securityDelta = Math.Round((1.0 - scores.Security) * 30, 1)
                }),
                TradeoffProfile = JsonSerializer.Serialize(new
                {
                    improves = new[] { "Security" },
                    degrades = new[] { "CostOptimization" },
                    neutral = new[] { "Reliability", "PerformanceEfficiency" }
                }),
                RiskProfile = JsonSerializer.Serialize(new
                {
                    currentRisk = scores.Security < 0.3 ? "critical" : "medium",
                    mitigatedRisk = "low",
                    residualRisk = "minimal",
                    riskCategory = "Security"
                }),
                ApprovalMode = "single",
                RequiredApprovals = 1,
                ReceivedApprovals = 0,
                Status = "pending",
                Priority = "medium",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        return recs;
    }

    /// <summary>
    /// Calls Azure AI Foundry (GPT-4) to enrich heuristic recommendations with
    /// AI-generated descriptions, rationale, and impact analysis.
    /// Individual failures are caught so one bad recommendation doesn't break the batch.
    /// </summary>
    private async Task EnrichRecommendationsWithAIAsync(
        List<Recommendation> recommendations,
        ScoreResult scores,
        IReadOnlyList<DiscoveredAzureResource> resources,
        CancellationToken cancellationToken)
    {
        var resourceSummary = resources
            .GroupBy(r => r.ResourceType)
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        var scoreContext = $"Availability={scores.Availability:P0}, Security={scores.Security:P0}, " +
                           $"Completeness={scores.Completeness:P0}, CostEfficiency={scores.CostEfficiency:P0}";

        foreach (var rec in recommendations.Where(r => string.Equals(r.ConfidenceSource, "heuristic", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var prompt = $$"""
                    You are an Azure Well-Architected Framework expert.
                    Given this infrastructure governance recommendation, provide an improved description
                    that is specific, actionable, and references actual Azure services and best practices.

                    Original recommendation: {{rec.Title}}
                    Category: {{rec.Category}}
                    Priority: {{rec.Priority}}
                    Current heuristic summary: {{rec.Summary}}
                    Proposed changes: {{rec.ProposedChanges}}

                    Infrastructure scores: {{scoreContext}}
                    Resources analyzed: {{string.Join(", ", resourceSummary.Take(10))}}
                    Total resources: {{resources.Count}}

                    Provide a JSON response with exactly these fields:
                    {"description": "<2-3 sentence actionable description>", "rationale": "<why this matters for WAF alignment>", "impact": "<expected improvement>"}
                    """;

                var context = new InfrastructureContext
                {
                    ServiceGroupCount = 1,
                    ServiceGroupNames = ["Current Analysis"],
                    RecentRunCount = 1,
                    CompletedRunCount = 1,
                    PendingRunCount = 0,
                    Findings = [new FindingSummary($"{rec.Title} ({rec.Priority})", rec.Category)],
                    DetailedDataJson = JsonSerializer.Serialize(new { scores = scoreContext, resourceTypes = resourceSummary.Take(10) })
                };

                var aiResponse = await _aiChatService!.GenerateResponseAsync(prompt, context, cancellationToken);

                if (aiResponse.ConfidenceSource.StartsWith("ai_foundry", StringComparison.OrdinalIgnoreCase))
                {
                    // Try to parse structured JSON from the AI response
                    if (TryParseEnrichment(aiResponse.Text, out var description, out var rationale, out var impact))
                    {
                        rec.Summary = description;
                        rec.ProposedChanges = $"{rec.ProposedChanges}\n\nRationale: {rationale}\nExpected Impact: {impact}";
                    }
                    else
                    {
                        rec.Summary = aiResponse.Text.Length > 500
                            ? aiResponse.Text[..500]
                            : aiResponse.Text;
                    }
                    // Keep the original grounding source (rule_engine / azure_advisor / heuristic).
                    // AI here is enrichment, not the primary recommendation source.
                    if (string.IsNullOrWhiteSpace(rec.ConfidenceSource))
                        rec.ConfidenceSource = "ai_foundry";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI enrichment failed for recommendation {RecId}; keeping heuristic text", rec.Id);
            }
        }
    }

    private static bool TryParseEnrichment(string text, out string description, out string rationale, out string impact)
    {
        description = rationale = impact = string.Empty;
        try
        {
            // Strip markdown code fences if AI wrapped the response
            var json = text.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = json.IndexOf('\n');
                var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNewline > 0 && lastFence > firstNewline)
                    json = json[(firstNewline + 1)..lastFence].Trim();
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            rationale = root.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "";
            impact = root.TryGetProperty("impact", out var i) ? i.GetString() ?? "" : "";
            return !string.IsNullOrWhiteSpace(description);
        }
        catch (JsonException)
        {
            // Intentionally swallow malformed JSON from AI response; return false to signal parse failure.
            return false;
        }
    }

    private static AgentMessage BuildRecommendationQualityMessage(
        Guid analysisRunId,
        IReadOnlyList<Recommendation> recommendations,
        DiscoveryResult discoveryResult,
        int violationCount)
    {
        var sourceBreakdown = recommendations
            .GroupBy(r => r.ConfidenceSource ?? "unknown", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var groundedCount = recommendations.Count(r =>
            !string.Equals(r.ConfidenceSource, "heuristic", StringComparison.OrdinalIgnoreCase));
        var averageConfidence = recommendations.Count > 0
            ? recommendations.Average(r => (double)r.Confidence)
            : 0.0;

        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AnalysisRunId = analysisRunId,
            MessageId = Guid.NewGuid(),
            AgentName = "RecommendationQualityAgent",
            AgentRole = "system",
            MessageType = "recommendationQuality",
            Payload = JsonSerializer.Serialize(new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                totalRecommendations = recommendations.Count,
                groundedRecommendations = groundedCount,
                heuristicRecommendations = recommendations.Count - groundedCount,
                averageConfidence = Math.Round(averageConfidence, 4),
                sourceBreakdown,
                bestPracticeViolationCount = violationCount,
                advisorSignalCount = discoveryResult.AdvisorRecommendations?.Count ?? 0,
                policySignalCount = discoveryResult.PolicyFindings?.Count ?? 0,
                defenderSignalCount = discoveryResult.DefenderAssessments?.Count ?? 0,
                rulePackVersion = _loadedRulePackVersion,
                rulePackSource = _loadedRulePackSource
            }),
            Confidence = recommendations.Count > 0
                ? (decimal)Math.Round(averageConfidence, 4)
                : 0m,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string MapRuleCategory(BestPracticeRule rule)
    {
        if (rule.Pillar.Equals("CostOptimization", StringComparison.OrdinalIgnoreCase) ||
            rule.Category.Equals("Cost", StringComparison.OrdinalIgnoreCase))
            return "FinOps";

        if (rule.Pillar.Equals("Reliability", StringComparison.OrdinalIgnoreCase))
            return "Reliability";

        return "Architecture";
    }

    private static object BuildCategoryDimensions(
        string category,
        ScoreResult scores,
        IReadOnlyList<BestPracticeViolation> violations)
    {
        // Category-specific dimensions with aligned WAF pillars for explainability
        var (dimensions, wafPillarValue, pillarName) = category switch
        {
            "Architecture" => (
                new Dictionary<string, double>
                {
                    ["completeness"] = scores.Completeness,
                    ["availability"] = scores.Availability,
                    ["security"] = scores.Security
                },
                Math.Round(scores.Completeness * 0.45 + scores.Availability * 0.35 + scores.Security * 0.20, 4),
                "performanceEfficiency"
            ),
            "FinOps" => (
                new Dictionary<string, double>
                {
                    ["costEfficiency"] = scores.CostEfficiency,
                    ["taggingCoverage"] = scores.TaggingCoverage,
                    ["resourceUtilization"] = scores.Utilization
                },
                Math.Round(scores.CostEfficiency * 0.50 + scores.TaggingCoverage * 0.30 + scores.Utilization * 0.20, 4),
                "costOptimization"
            ),
            "Reliability" => (
                new Dictionary<string, double>
                {
                    ["availability"] = scores.Availability,
                    ["resiliency"] = scores.Resiliency,
                    ["security"] = scores.Security
                },
                Math.Round(scores.Availability * 0.55 + scores.Resiliency * 0.25 + scores.Security * 0.20, 4),
                "reliability"
            ),
            "Sustainability" => (
                new Dictionary<string, double>
                {
                    ["resourceUtilization"] = scores.Utilization,
                    ["carbonSignal"] = scores.GreenRegionUsage,
                    ["costEfficiency"] = scores.CostEfficiency
                },
                Math.Round(scores.Utilization * 0.50 + scores.GreenRegionUsage * 0.30 + scores.CostEfficiency * 0.20, 4),
                "sustainability"
            ),
            "Security" => (
                new Dictionary<string, double>
                {
                    ["securityPosture"] = scores.Security
                },
                scores.Security,
                "securityPillar"
            ),
            _ => (
                new Dictionary<string, double>
                {
                    ["availability"] = scores.Availability,
                    ["completeness"] = scores.Completeness,
                    ["costEfficiency"] = scores.CostEfficiency,
                    ["security"] = scores.Security
                },
                (scores.Availability + scores.Completeness + scores.CostEfficiency + scores.Security) / 4.0,
                "overall"
            )
        };

        // Build category-specific WAF pillars object dynamically
        var wafPillars = new Dictionary<string, object>
        {
            [pillarName] = wafPillarValue
        };

        return new
        {
            dimensions,
            wafPillars,
            topImpactFactors = BuildTopImpactFactors(category, scores, violations)
        };
    }

    private static object[] BuildTopImpactFactors(
        string category,
        ScoreResult scores,
        IReadOnlyList<BestPracticeViolation> violations)
    {
        var factors = new List<object>();

        var relevant = violations
            .Where(v => v.Rule != null && MapRuleCategory(v.Rule).Equals(category, StringComparison.OrdinalIgnoreCase))
            .GroupBy(v => v.Rule!.Name)
            .OrderByDescending(g => g.Count())
            .Take(3);

        foreach (var group in relevant)
        {
            var firstViolation = group.First();
            var rule = firstViolation.Rule!;
            var driftCategories = group
                .Where(v => !string.IsNullOrEmpty(v.DriftCategory))
                .Select(v => v.DriftCategory!)
                .Distinct()
                .ToList();

            factors.Add(new
            {
                factor = group.Key,
                description = rule.Description,
                rationale = rule.Rationale,
                remediationGuidance = rule.RemediationGuidance,
                remediationIac = rule.RemediationIac,
                affectedResources = group.Count(),
                severity = firstViolation.Severity,
                driftCategories = driftCategories.Count > 0 ? driftCategories : new List<string> { "configuration" },
                delta = -SeverityWeight(firstViolation.Severity) * group.Count(),
                ruleId = rule.RuleId,
                pillar = rule.Pillar
            });
        }

        if (factors.Count == 0)
        {
            var (dimName, dimValue) = category switch
            {
                "Architecture" => ("completeness", scores.Completeness),
                "FinOps" => ("costEfficiency", scores.CostEfficiency),
                "Reliability" => ("availability", scores.Availability),
                "Sustainability" => ("resourceUtilization", scores.Utilization),
                "Security" => ("security", scores.Security),
                _ => ("overall", scores.GetAverageScore())
            };
            factors.Add(new
            {
                factor = $"Primary {dimName} driver ({dimValue:P0})",
                affectedResources = scores.ResourceCount,
                severity = dimValue < 0.5 ? "High" : dimValue < 0.75 ? "Medium" : "Info",
                delta = -(int)Math.Round((1.0 - dimValue) * 20)
            });
        }

        return factors.ToArray();
    }

    private static string MapAdvisorCategory(string? advisorCategory)
    {
        if (string.IsNullOrWhiteSpace(advisorCategory))
            return "Architecture";

        return advisorCategory.ToLowerInvariant() switch
        {
            "cost" => "FinOps",
            "reliability" or "highavailability" => "Reliability",
            _ => "Architecture"
        };
    }

    private static int GroundingRank(string? confidenceSource) =>
        confidenceSource?.ToLowerInvariant() switch
        {
            "rule_engine" => 5,
            "azure_policy" => 4,
            "defender_for_cloud" => 4,
            "azure_advisor" => 4,
            "heuristic" => 2,
            "ai_foundry" => 1,
            _ => 0
        };

    private static decimal DefenderSeverityToConfidence(string? severity) =>
        NormalizePriority(severity) switch
        {
            "critical" => 0.96m,
            "high" => 0.90m,
            "medium" => 0.82m,
            _ => 0.74m
        };

    private static string PolicyPriority(DiscoveredPolicyFinding finding)
    {
        var name = $"{finding.PolicyDefinitionName} {finding.PolicyAssignmentName}";
        if (name.Contains("security", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("defender", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("network", StringComparison.OrdinalIgnoreCase))
            return "high";

        return "medium";
    }

    private static decimal SeverityToConfidence(string? severity) =>
        NormalizePriority(severity) switch
        {
            "critical" => 0.95m,
            "high" => 0.88m,
            "medium" => 0.78m,
            _ => 0.70m
        };

    private static decimal AdvisorImpactToConfidence(string? impact) =>
        NormalizePriority(impact) switch
        {
            "critical" => 0.95m,
            "high" => 0.90m,
            "medium" => 0.80m,
            _ => 0.72m
        };

    private static int SeverityWeight(string? severity) =>
        NormalizePriority(severity) switch
        {
            "critical" => 4,
            "high" => 3,
            "medium" => 2,
            _ => 1
        };

    private static string NormalizePriority(string? raw) =>
        raw?.ToLowerInvariant() switch
        {
            "critical" => "critical",
            "high" => "high",
            "medium" or "moderate" => "medium",
            "low" => "low",
            // Advisor often uses High/Medium/Low impact; keep a safe default.
            _ => "medium"
        };

    private static bool IsNonBillableResourceType(string resourceType)
    {
        var nonBillable = new[] { "userassignedidentities", "managedidentities",
            "roleassignments", "roledefinitions", "locks",
            "policyassignments", "policydefinitions", "policysetdefinitions",
            "diagnosticsettings", "activitylogalerts", "actiongroups",
            "privateendpoints", "privatednszone", "networkwatchers",
            "networkintentpolicies" };
        return nonBillable.Any(nb => resourceType.Contains(nb, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBillableResourceType(string resourceType)
    {
        var billable = new[] { "virtualmachines", "storageaccounts", "sqldatabases", "cosmosdb",
            "functionapps", "webapps", "managedclusters", "containerapps",
            "redis", "postgresql", "mysql", "mariadb", "sqlservers",
            "loadbalancers", "applicationgateways", "frontdoors",
            "cognitiveservices", "signalr", "eventgrids", "eventhubs",
            "servicebusnamespaces", "keyvaults", "disks", "publicipaddresses",
            "virtualnetworkgateways", "expressroutecircuits", "firewalls",
            "containerregistries", "loganalytics", "appinsights",
            "apimanagement", "searchservices", "batchaccounts" };
        return billable.Any(t => resourceType.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyCompositeConfidence(List<Recommendation> recommendations, CostEvidenceSignals? costSignals)
    {
        foreach (var recommendation in recommendations)
        {
            GroundingProvenance? grounding = null;
            if (!string.IsNullOrWhiteSpace(recommendation.ChangeContext))
            {
                try
                {
                    using var doc = JsonDocument.Parse(recommendation.ChangeContext);
                    if (doc.RootElement.TryGetProperty("grounding", out var groundingEl))
                    {
                        var source = groundingEl.TryGetProperty("groundingSource", out var sourceEl)
                            ? sourceEl.GetString() ?? "seeded_rule"
                            : "seeded_rule";
                        var query = groundingEl.TryGetProperty("groundingQuery", out var queryEl)
                            ? queryEl.GetString() ?? string.Empty
                            : string.Empty;
                        var timestamp = groundingEl.TryGetProperty("groundingTimestampUtc", out var tsEl)
                            && DateTime.TryParse(tsEl.GetString(), out var parsed)
                            ? parsed.ToUniversalTime()
                            : DateTime.UtcNow;
                        var runId = groundingEl.TryGetProperty("groundingToolRunId", out var runIdEl)
                            ? runIdEl.GetString()
                            : null;

                        var recency = source.Equals("learn_mcp", StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.6;
                        var quality = source.Equals("learn_mcp", StringComparison.OrdinalIgnoreCase) ? 0.85 : 0.5;

                        grounding = new GroundingProvenance(
                            source,
                            query,
                            timestamp,
                            runId,
                            quality,
                            recency);
                    }
                }
                catch
                {
                    // Preserve existing confidence when context parsing fails.
                }
            }

            recommendation.Confidence = RecommendationConfidenceModel.CalculateWeightedConfidence(
                recommendation,
                grounding,
                costSignals);
            recommendation.ConfidenceSource = "composite";
        }
    }

    private async Task<CostEvidenceSignals?> BuildCostEvidenceSignalsAsync(
        AtlasDbContext db,
        Guid serviceGroupId,
        CancellationToken cancellationToken)
    {
        if (_azureMcpValueEvidenceClient is null)
        {
            return null;
        }

        var scopes = await db.ServiceGroupScopes
            .AsNoTracking()
            .Where(s => s.ServiceGroupId == serviceGroupId)
            .Select(s => new { s.SubscriptionId, s.ResourceGroup })
            .ToListAsync(cancellationToken);

        if (scopes.Count == 0)
        {
            return new CostEvidenceSignals(0.0, 0.0, 0.0);
        }

        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var previousMonthStart = monthStart.AddMonths(-1);
        var previousMonthDays = DateTime.DaysInMonth(previousMonthStart.Year, previousMonthStart.Month);
        var elapsedDaysInCurrentMonth = Math.Max((now.Date - monthStart.Date).Days + 1, 1);

        var successful = 0;
        var freshnessScores = new List<double>(scopes.Count);
        var anomalyScores = new List<double>(scopes.Count);

        foreach (var scope in scopes)
        {
            var evidence = await _azureMcpValueEvidenceClient.TryGetCostEvidenceAsync(
                scope.SubscriptionId,
                scope.ResourceGroup,
                monthStart,
                now,
                elapsedDaysInCurrentMonth,
                previousMonthDays,
                cancellationToken);

            if (evidence is null)
            {
                continue;
            }

            successful++;

            var ageDays = Math.Max(0.0, (now - evidence.LastQueriedAtUtc).TotalDays);
            freshnessScores.Add(ageDays <= 1 ? 1.0 : ageDays <= 3 ? 0.9 : ageDays <= 7 ? 0.75 : 0.5);

            var severity = Math.Min(1.0, evidence.AnomalyCount / 10.0);
            anomalyScores.Add(severity);
        }

        var scopeCoverage = Math.Clamp((double)successful / scopes.Count, 0.0, 1.0);
        var freshness = freshnessScores.Count > 0 ? freshnessScores.Average() : 0.0;
        var anomalySeverity = anomalyScores.Count > 0 ? anomalyScores.Average() : 0.0;

        return new CostEvidenceSignals(scopeCoverage, freshness, anomalySeverity);
    }

    private static void AttachLineageCheckpoints(List<Recommendation> recommendations)
    {
        foreach (var recommendation in recommendations)
        {
            Dictionary<string, object?> context;
            if (!string.IsNullOrWhiteSpace(recommendation.ChangeContext))
            {
                try
                {
                    context = JsonSerializer.Deserialize<Dictionary<string, object?>>(recommendation.ChangeContext!)
                        ?? new Dictionary<string, object?>();
                }
                catch
                {
                    context = new Dictionary<string, object?>();
                }
            }
            else
            {
                context = new Dictionary<string, object?>();
            }

            context["lineage"] = new
            {
                version = "v1",
                checkpoints = new object[]
                {
                    new { step = "discovery", source = recommendation.TriggerReason ?? "analysis", timestampUtc = recommendation.CreatedAt },
                    new { step = "policy_advisor_defender_evidence", source = recommendation.ConfidenceSource, timestampUtc = recommendation.UpdatedAt },
                    new { step = "grounding", source = "learn_mcp_or_seeded", timestampUtc = DateTime.UtcNow },
                    new { step = "final_recommendation", source = "composite", timestampUtc = DateTime.UtcNow }
                }
            };

            recommendation.ChangeContext = JsonSerializer.Serialize(context);
        }
    }

    /// <summary>
    /// Creates a drift snapshot from scoring results and actual best-practice violations.
    /// DriftScore 0 = no drift (perfect), 100 = maximum drift.
    /// </summary>
    private static DriftSnapshot CreateDriftSnapshot(
        Guid serviceGroupId,
        ScoreResult scores,
        IReadOnlyList<DiscoveredAzureResource> resources,
        IReadOnlyList<BestPracticeViolation> violations)
    {
        var total = resources.Count;

        // DriftScore: 0 = perfectly compliant, 100 = fully drifted
        var driftScore = (decimal)Math.Round(Math.Max(0.0, Math.Min(100.0, (1.0 - scores.GetAverageScore()) * 100)), 2);

        // Derive severity buckets from actual violations (High → critical/high, Medium → medium, Low → low)
        int critical = violations.Count(v => string.Equals(v.Severity, "Critical", StringComparison.OrdinalIgnoreCase));
        int high = violations.Count(v => string.Equals(v.Severity, "High", StringComparison.OrdinalIgnoreCase));
        int medium = violations.Count(v => string.Equals(v.Severity, "Medium", StringComparison.OrdinalIgnoreCase));
        int low = violations.Count(v => string.Equals(v.Severity, "Low", StringComparison.OrdinalIgnoreCase));
        int totalViolations = violations.Count;

        // Build per-category breakdown from the actual violations' rules.
        // When there are no violations fall back to score-based estimation so the snapshot
        // is still meaningful even on a clean environment.
        Dictionary<string, int> categoryMap;
        if (violations.Count > 0)
        {
            categoryMap = violations
                .GroupBy(v => v.Rule?.Category ?? "General")
                .ToDictionary(g => g.Key, g => g.Count());
        }
        else
        {
            categoryMap = new Dictionary<string, int>
            {
                ["availability"] = total > 0 ? (int)Math.Round(total * (1.0 - scores.Availability)) : 0,
                ["completeness"] = total > 0 ? (int)Math.Round(total * (1.0 - scores.Completeness)) : 0,
                ["security"] = total > 0 ? (int)Math.Round(total * (1.0 - scores.Security)) : 0,
                ["cost"] = total > 0 ? (int)Math.Round(total * (1.0 - scores.CostEfficiency)) : 0
            };
        }

        var categoryBreakdown = JsonSerializer.Serialize(categoryMap);

        return new DriftSnapshot
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroupId,
            SnapshotTime = DateTime.UtcNow,
            TotalViolations = totalViolations,
            CriticalViolations = critical,
            HighViolations = high,
            MediumViolations = medium,
            LowViolations = low,
            DriftScore = driftScore,
            CategoryBreakdown = categoryBreakdown,
            TrendAnalysis = null, // Computed by the drift trends endpoint from historical snapshots
            CreatedAt = DateTime.UtcNow
        };
    }

    private sealed record RuleSeed(
        string RuleId,
        string Source,
        string Category,
        string Pillar,
        string Name,
        string Description,
        string Severity,
        string Rationale,
        string[] ApplicabilityScope,
        string EvaluationQuery,
        string RemediationGuidance,
        string[] References);

    private static readonly RuleSeed[] DefaultSeedRules =
    [
        new(
            "ATLAS-SKU-001", "Custom", "Reliability", "Reliability",
            "Production SKU required",
            "Resources using Basic, Free, or Developer SKUs do not include production SLA guarantees.",
            "High",
            "Low-tier SKUs reduce availability guarantees for production workloads.",
            ["*"],
            "sku not in [Basic,Free,Developer]",
            "Upgrade to Standard or Premium SKU for production services.",
            ["https://learn.microsoft.com/azure/well-architected/reliability/design-scale"]),
        new(
            "ATLAS-LOC-001", "Custom", "Operations", "OperationalExcellence",
            "Regional placement required",
            "Resources without a regional assignment cannot be governed for residency, DR, or cost optimization.",
            "Medium",
            "Region metadata is required for governance and resilience planning.",
            ["*"],
            "location != null and location != 'global'",
            "Assign resources to explicit Azure regions and update IaC definitions accordingly.",
            ["https://learn.microsoft.com/azure/well-architected/operational-excellence/design"]),
        new(
            "ATLAS-TAG-001", "Custom", "Operations", "OperationalExcellence",
            "Governance tags required",
            "Resources must include environment, owner, and cost-center tags.",
            "Low",
            "Required tags enable ownership, accountability, and chargeback.",
            ["*"],
            "tags contains environment, owner, costCentre",
            "Apply standard governance tags and enforce with Azure Policy.",
            ["https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-tagging"]),
        new(
            "WAF-SEC-001", "WAF", "Security", "Security",
            "Disable Public Network Access",
            "Public network access should be disabled where not explicitly required.",
            "High",
            "Reducing public surface area lowers exposure risk.",
            ["Microsoft.Storage/storageAccounts", "Microsoft.Sql/servers", "Microsoft.KeyVault/vaults", "Microsoft.DBforPostgreSQL/flexibleServers", "Microsoft.DocumentDB/databaseAccounts"],
            "publicNetworkAccess != 'Enabled'",
            "Disable public network access and use private endpoints.",
            ["https://learn.microsoft.com/azure/well-architected/security/networking"]),
        new(
            "WAF-SEC-002", "WAF", "Security", "Security",
            "Enforce HTTPS / TLS 1.2+",
            "Internet-facing and data resources should enforce HTTPS and modern TLS.",
            "Critical",
            "TLS prevents data-in-transit compromise and aligns with baseline security controls.",
            ["Microsoft.Web/sites", "Microsoft.Storage/storageAccounts", "Microsoft.Sql/servers", "Microsoft.Cache/redis"],
            "httpsOnly == true and minimumTlsVersion >= TLS1_2",
            "Enable HTTPS-only and set minimum TLS to 1.2 or higher.",
            ["https://learn.microsoft.com/azure/well-architected/security/design-network-encryption"]),
        new(
            "WAF-COST-002", "WAF", "Cost", "CostOptimization",
            "Tag Resources for Cost Allocation",
            "Resources should include owner and cost attribution tags.",
            "Medium",
            "Cost allocation is required for FinOps governance.",
            ["*"],
            "tags contains costCentre and owner and environment",
            "Apply and enforce cost tags via Azure Policy and IaC.",
            ["https://learn.microsoft.com/azure/well-architected/cost-optimization/align-usage-to-billing"]),
        new(
            "WAF-REL-001", "WAF", "Reliability", "Reliability",
            "Enable Availability Zone Redundancy",
            "Production workloads should use zone-redundant deployments where supported.",
            "High",
            "Zone redundancy protects against datacenter-level failures.",
            ["Microsoft.Storage/storageAccounts", "Microsoft.DBforPostgreSQL/flexibleServers", "Microsoft.Cache/redis", "Microsoft.Network/publicIPAddresses"],
            "zoneRedundant == true or zones.length > 1",
            "Adopt zone-redundant SKUs and deploy across multiple availability zones.",
            ["https://learn.microsoft.com/azure/well-architected/reliability/regions-availability-zones"]),
        new(
            "PSRULE-ST-003", "PSRule", "Security", "Security",
            "Storage: Enforce Minimum TLS 1.2",
            "Azure Storage accounts should enforce minimum TLS 1.2.",
            "High",
            "Older TLS versions are deprecated and insecure.",
            ["Microsoft.Storage/storageAccounts"],
            "properties.minimumTlsVersion == 'TLS1_2'",
            "Set storage minimumTlsVersion to TLS1_2.",
            ["https://azure.github.io/PSRule.Rules.Azure/en/rules/Azure.Storage.MinTLS/"]),
        new(
            "PSRULE-APP-003", "PSRule", "Security", "Security",
            "App Service: Enforce HTTPS",
            "App Service should redirect HTTP to HTTPS.",
            "Critical",
            "HTTPS prevents interception of application traffic.",
            ["Microsoft.Web/sites"],
            "properties.httpsOnly == true",
            "Set httpsOnly to true on App Service resources.",
            ["https://azure.github.io/PSRule.Rules.Azure/en/rules/Azure.AppService.UseHTTPS/"]),
        new(
            "PSRULE-ACR-001", "PSRule", "Security", "Security",
            "Container Registry: Disable Admin User",
            "ACR admin user should be disabled.",
            "High",
            "Admin user credentials are broad and harder to govern than RBAC identities.",
            ["Microsoft.ContainerRegistry/registries"],
            "properties.adminUserEnabled == false",
            "Disable ACR admin user and use managed identities/service principals with scoped roles.",
            ["https://azure.github.io/PSRule.Rules.Azure/en/rules/Azure.ACR.AdminUser/"]),
        new(
            "PSRULE-ACR-002", "PSRule", "Security", "Security",
            "Container Registry: Disable Anonymous Pull",
            "ACR should not allow anonymous image pull.",
            "High",
            "Anonymous pull exposes images without identity-backed authorization controls.",
            ["Microsoft.ContainerRegistry/registries"],
            "properties.anonymousPullEnabled == false",
            "Disable anonymous pull and enforce authenticated pull via managed identity or workload identity.",
            ["https://azure.github.io/PSRule.Rules.Azure/"]),
        new(
            "PSRULE-ST-004", "PSRule", "Security", "Security",
            "Storage: Require Secure Transfer",
            "Storage accounts should require secure transfer.",
            "High",
            "Secure transfer enforces encrypted transport for blob, queue, table, and file operations.",
            ["Microsoft.Storage/storageAccounts"],
            "properties.supportsHttpsTrafficOnly == true",
            "Set supportsHttpsTrafficOnly to true for all storage accounts.",
            ["https://azure.github.io/PSRule.Rules.Azure/"]),
        new(
            "PSRULE-KV-001", "PSRule", "Security", "Security",
            "Key Vault: Purge Protection Enabled",
            "Key Vault should have purge protection enabled.",
            "High",
            "Purge protection helps prevent permanent data loss during accidental or malicious deletion.",
            ["Microsoft.KeyVault/vaults"],
            "properties.enablePurgeProtection == true",
            "Enable purge protection on Key Vault and include it in IaC defaults.",
            ["https://azure.github.io/PSRule.Rules.Azure/"]),
        new(
            "PSRULE-KV-002", "PSRule", "Security", "Security",
            "Key Vault: Soft Delete Enabled",
            "Key Vault should keep soft delete enabled.",
            "Medium",
            "Soft delete provides recovery window for deleted vault data-plane assets.",
            ["Microsoft.KeyVault/vaults"],
            "properties.enableSoftDelete == true",
            "Enable soft delete and enforce recovery window configuration in policy.",
            ["https://azure.github.io/PSRule.Rules.Azure/"]),
        new(
            "PSRULE-AKS-001", "PSRule", "Security", "Security",
            "AKS: RBAC Enabled",
            "AKS clusters should have Kubernetes RBAC enabled.",
            "High",
            "RBAC limits cluster access and enforces least privilege for operators and workloads.",
            ["Microsoft.ContainerService/managedClusters"],
            "properties.enableRBAC == true",
            "Enable AKS RBAC and map identities to least-privilege roles.",
            ["https://azure.github.io/PSRule.Rules.Azure/"]),
        new(
            "PSRULE-AKS-002", "PSRule", "Security", "Security",
            "AKS: Disable Local Accounts",
            "AKS clusters should disable local accounts.",
            "High",
            "Disabling local accounts reduces static admin credential risk.",
            ["Microsoft.ContainerService/managedClusters"],
            "properties.disableLocalAccounts == true",
            "Set disableLocalAccounts to true and use Entra ID-backed authentication.",
            ["https://azure.github.io/PSRule.Rules.Azure/"]),
        new(
            "PSRULE-CA-001", "PSRule", "Security", "Security",
            "Container Apps: Disable Insecure Ingress",
            "Container Apps ingress should not allow insecure HTTP.",
            "High",
            "Allowing insecure ingress enables plaintext traffic paths and downgrade risk.",
            ["Microsoft.App/containerApps"],
            "properties.configuration.ingress.allowInsecure == false",
            "Set allowInsecure to false and require HTTPS ingress only.",
            ["https://learn.microsoft.com/azure/container-apps/ingress-overview"]),
        new(
            "PSRULE-CA-002", "PSRule", "Reliability", "Reliability",
            "Container Apps: Configure Minimum Replicas",
            "Production Container Apps should run with minimum replicas >= 1.",
            "Medium",
            "Zero baseline replicas can increase cold-start and recovery latency for production workloads.",
            ["Microsoft.App/containerApps"],
            "properties.template.scale.minReplicas >= 1 for production-tagged workloads",
            "Set minReplicas to at least 1 for production workloads and tune autoscale rules.",
            ["https://learn.microsoft.com/azure/container-apps/scale-app"]),
        new(
            "PSRULE-CAE-001", "PSRule", "Operations", "OperationalExcellence",
            "Container Apps Env: Send App Logs to Log Analytics",
            "Container Apps managed environments should export application logs to Log Analytics.",
            "High",
            "Centralized logs are required for troubleshooting, alerting, and incident response.",
            ["Microsoft.App/managedEnvironments"],
            "properties.appLogsConfiguration.destination == 'log-analytics'",
            "Configure appLogsConfiguration destination to log-analytics and bind a workspace.",
            ["https://learn.microsoft.com/azure/container-apps/log-monitoring"]),
        new(
            "FOUNDRY-SEC-001", "Custom", "Security", "Security",
            "AI Services: Disable Local Auth",
            "Azure AI service accounts used for Foundry/OpenAI should disable local key-based auth where supported.",
            "High",
            "Disabling local auth enforces Entra/managed identity paths and improves credential governance.",
            ["Microsoft.CognitiveServices/accounts"],
            "properties.disableLocalAuth == true",
            "Set disableLocalAuth=true and use managed identity with RBAC for callers.",
            ["https://learn.microsoft.com/azure/ai-foundry/"]),
        new(
            "FOUNDRY-NET-001", "Custom", "Security", "Security",
            "AI Services: Restrict Public Network Access",
            "Azure AI service accounts should avoid unrestricted public network access.",
            "High",
            "Network restriction lowers exposure and aligns with enterprise zero-trust controls.",
            ["Microsoft.CognitiveServices/accounts", "Microsoft.MachineLearningServices/workspaces"],
            "publicNetworkAccess != 'Enabled' or privateEndpointConnections.length > 0",
            "Disable public access where possible and route through private endpoints / managed network.",
            ["https://learn.microsoft.com/azure/ai-foundry/"]),
        new(
            "AMBA-MON-001", "AMBA", "Operations", "OperationalExcellence",
            "Subscription Baseline: Activity Log Alerts",
            "Subscriptions should include Azure Monitor activity log alerts as baseline controls.",
            "High",
            "Without activity log alerts, critical control-plane and platform events can be missed.",
            ["Microsoft.Resources/subscriptions"],
            "count(type == 'Microsoft.Insights/activityLogAlerts') > 0",
            "Deploy AMBA activity log alert templates for subscription-level platform events.",
            ["https://azure.github.io/azure-monitor-baseline-alerts/welcome/"]),
        new(
            "AMBA-MON-002", "AMBA", "Operations", "OperationalExcellence",
            "Subscription Baseline: Action Groups",
            "Subscriptions should include at least one Azure Monitor action group.",
            "High",
            "Alerts without action groups do not route notifications and incidents promptly.",
            ["Microsoft.Resources/subscriptions"],
            "count(type == 'Microsoft.Insights/actionGroups') > 0",
            "Deploy AMBA action groups and route to incident channels/on-call systems.",
            ["https://azure.github.io/azure-monitor-baseline-alerts/welcome/"]),
        new(
            "AMBA-MON-003", "AMBA", "Operations", "OperationalExcellence",
            "Subscription Baseline: Metric Alerts",
            "Subscriptions should include Azure Monitor metric alerts for workload baselines.",
            "Medium",
            "Metric alerts provide early signal for performance and reliability regression.",
            ["Microsoft.Resources/subscriptions"],
            "count(type == 'Microsoft.Insights/metricAlerts') > 0",
            "Deploy AMBA metric alert templates for core workload services.",
            ["https://azure.github.io/azure-monitor-baseline-alerts/welcome/"]),
        new(
            "AQR-PRIV-001", "AzureQuickReview", "Security", "Security",
            "Services Should Use Private Endpoints",
            "PaaS services should use private endpoints when supported.",
            "High",
            "Private endpoints remove internet exposure and improve segmentation.",
            ["Microsoft.Storage/storageAccounts", "Microsoft.KeyVault/vaults", "Microsoft.Sql/servers", "Microsoft.DocumentDB/databaseAccounts", "Microsoft.ContainerRegistry/registries"],
            "privateEndpointConnections.length > 0",
            "Create private endpoints and disable public access after validation.",
            ["https://github.com/Azure/azqr?tab=readme-ov-file#private-endpoints"]),
        new(
            "AAC-ARCH-004", "ArchitectureCenter", "Architecture", "OperationalExcellence",
            "Infrastructure Should Be Defined as Code",
            "Resources should be managed via IaC to reduce configuration drift.",
            "Medium",
            "IaC improves repeatability, auditability, and change safety.",
            ["*"],
            "tags.managedBy != null or tags.iacManaged == true",
            "Adopt Bicep/Terraform pipelines and mark IaC-managed assets consistently.",
            ["https://learn.microsoft.com/azure/architecture/framework/devops/automation-infrastructure"]),
    ];

    private sealed record RulePackDocument(string? Version, List<RulePackRule>? Rules);

    private sealed record RulePackRule(
        string? RuleId,
        string? Source,
        string? Category,
        string? Pillar,
        string? Name,
        string? Description,
        string? Severity,
        string? Rationale,
        string[]? ApplicabilityScope,
        string? EvaluationQuery,
        string? RemediationGuidance,
        string[]? References,
        bool? IsEnabled);

    private RuleSeed[] GetSeedRules()
    {
        if (_cachedSeedRules is not null)
            return _cachedSeedRules;

        lock (RulePackSync)
        {
            if (_cachedSeedRules is not null)
                return _cachedSeedRules;

            var envPath = Environment.GetEnvironmentVariable("ATLAS_RULE_PACK_PATH");
            var candidatePaths = new List<string>();

            if (!string.IsNullOrWhiteSpace(envPath))
                candidatePaths.Add(envPath);

            candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "Rules", "atlas-recommendation-rule-pack.v1.json"));
            candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "atlas-recommendation-rule-pack.v1.json"));

            foreach (var path in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path))
                    continue;

                try
                {
                    var json = File.ReadAllText(path);
                    var doc = JsonSerializer.Deserialize<RulePackDocument>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (doc?.Rules is null || doc.Rules.Count == 0)
                    {
                        _logger.LogWarning("Rule pack file {Path} is empty; continuing with fallback", path);
                        continue;
                    }

                    var loaded = doc.Rules
                        .Where(r => r.IsEnabled != false)
                        .Select(ToRuleSeed)
                        .Where(r => r is not null)
                        .Select(r => r!)
                        .ToArray();

                    var duplicateRuleIds = loaded
                        .GroupBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() > 1)
                        .Select(g => g.Key)
                        .ToList();

                    if (duplicateRuleIds.Count > 0)
                    {
                        _logger.LogWarning(
                            "Rule pack {Path} contains duplicate rule IDs ({RuleIds}); continuing with fallback",
                            path, string.Join(", ", duplicateRuleIds));
                        continue;
                    }

                    if (loaded.Length == 0)
                    {
                        _logger.LogWarning("Rule pack {Path} produced no valid enabled rules; continuing with fallback", path);
                        continue;
                    }

                    _cachedSeedRules = loaded;
                    _loadedRulePackVersion = string.IsNullOrWhiteSpace(doc.Version) ? "unspecified" : doc.Version;
                    _loadedRulePackSource = path;

                    _logger.LogInformation(
                        "Loaded recommendation rule pack v{Version} from {Path} with {Count} rule(s)",
                        _loadedRulePackVersion, path, loaded.Length);

                    return _cachedSeedRules;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load rule pack from {Path}; trying next fallback", path);
                }
            }

            _cachedSeedRules = DefaultSeedRules;
            _loadedRulePackVersion = "embedded-default";
            _loadedRulePackSource = "embedded";
            _logger.LogInformation(
                "Using embedded recommendation rule pack with {Count} rule(s)",
                _cachedSeedRules.Length);
            return _cachedSeedRules;
        }
    }

    private static RuleSeed? ToRuleSeed(RulePackRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.RuleId) ||
            string.IsNullOrWhiteSpace(rule.Source) ||
            string.IsNullOrWhiteSpace(rule.Category) ||
            string.IsNullOrWhiteSpace(rule.Pillar) ||
            string.IsNullOrWhiteSpace(rule.Name) ||
            string.IsNullOrWhiteSpace(rule.Description) ||
            string.IsNullOrWhiteSpace(rule.Severity))
            return null;

        return new RuleSeed(
            RuleId: rule.RuleId.Trim(),
            Source: rule.Source.Trim(),
            Category: rule.Category.Trim(),
            Pillar: rule.Pillar.Trim(),
            Name: rule.Name.Trim(),
            Description: rule.Description.Trim(),
            Severity: rule.Severity.Trim(),
            Rationale: string.IsNullOrWhiteSpace(rule.Rationale) ? rule.Description.Trim() : rule.Rationale.Trim(),
            ApplicabilityScope: rule.ApplicabilityScope is { Length: > 0 } ? rule.ApplicabilityScope : ["*"],
            EvaluationQuery: string.IsNullOrWhiteSpace(rule.EvaluationQuery) ? "custom-eval" : rule.EvaluationQuery.Trim(),
            RemediationGuidance: string.IsNullOrWhiteSpace(rule.RemediationGuidance)
                ? "Apply remediation aligned with source guidance."
                : rule.RemediationGuidance.Trim(),
            References: rule.References ?? []);
    }

    /// <summary>
    /// Upserts seeded multi-source best-practice rules and evaluates discovered resources
    /// against rule predicates derived from available metadata/properties.
    /// </summary>
    private async Task<IReadOnlyList<BestPracticeViolation>> PersistBestPracticeViolationsAsync(
        AtlasDbContext db,
        AnalysisRun run,
        IReadOnlyList<DiscoveredAzureResource> resources,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var seedRules = GetSeedRules();

        // --- Step B: Upsert seeded rule rows ---
        var ruleIdStrings = seedRules.Select(r => r.RuleId).ToArray();
        var existingRules = await db.BestPracticeRules
            .Where(r => ruleIdStrings.Contains(r.RuleId))
            .ToDictionaryAsync(r => r.RuleId, ct);

        var ruleGuids = new Dictionary<string, Guid>();

        foreach (var seed in seedRules)
        {
            if (existingRules.TryGetValue(seed.RuleId, out var existing))
            {
                existing.Source = seed.Source;
                existing.Category = seed.Category;
                existing.Pillar = seed.Pillar;
                existing.Name = seed.Name;
                existing.Description = seed.Description;
                existing.Rationale = seed.Rationale;
                existing.Severity = seed.Severity;
                existing.ApplicabilityScope = JsonSerializer.Serialize(seed.ApplicabilityScope);
                existing.EvaluationQuery = seed.EvaluationQuery;
                existing.RemediationGuidance = seed.RemediationGuidance;
                existing.References = JsonSerializer.Serialize(seed.References);
                existing.ApplicabilityCriteria = JsonSerializer.Serialize(new
                {
                    rulePackVersion = _loadedRulePackVersion,
                    rulePackSource = _loadedRulePackSource
                });
                existing.IsEnabled = true;
                existing.UpdatedAt = now;
                ruleGuids[seed.RuleId] = existing.Id;
            }
            else
            {
                var newRule = new BestPracticeRule
                {
                    Id = Guid.NewGuid(),
                    RuleId = seed.RuleId,
                    Source = seed.Source,
                    Category = seed.Category,
                    Pillar = seed.Pillar,
                    Name = seed.Name,
                    Description = seed.Description,
                    Rationale = seed.Rationale,
                    Severity = seed.Severity,
                    ApplicabilityScope = JsonSerializer.Serialize(seed.ApplicabilityScope),
                    ApplicabilityCriteria = JsonSerializer.Serialize(new
                    {
                        rulePackVersion = _loadedRulePackVersion,
                        rulePackSource = _loadedRulePackSource
                    }),
                    EvaluationQuery = seed.EvaluationQuery,
                    RemediationGuidance = seed.RemediationGuidance,
                    References = JsonSerializer.Serialize(seed.References),
                    IsEnabled = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.BestPracticeRules.Add(newRule);
                ruleGuids[seed.RuleId] = newRule.Id;
            }
        }

        // Rule lifecycle management: mark missing seeded rules as deprecated and clear active violations.
        var seededSources = seedRules.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var knownSeededRules = await db.BestPracticeRules
            .Where(r => seededSources.Contains(r.Source))
            .ToListAsync(ct);
        var activeSeedRuleIds = ruleIdStrings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retiredSeededRuleEntityIds = knownSeededRules
            .Where(r => IsEngineManagedRuleId(r.RuleId) && !activeSeedRuleIds.Contains(r.RuleId))
            .Select(r => r.Id)
            .ToArray();

        if (retiredSeededRuleEntityIds.Length > 0)
        {
            await db.BestPracticeViolations
                .Where(v => v.ServiceGroupId == run.ServiceGroupId
                            && retiredSeededRuleEntityIds.Contains(v.RuleId)
                            && v.Status == "active")
                .ExecuteDeleteAsync(ct);

            foreach (var retired in knownSeededRules.Where(r => retiredSeededRuleEntityIds.Contains(r.Id)))
            {
                retired.IsEnabled = false;
                retired.DeprecatedAt ??= now;
                retired.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);

        // Build a Guid→BestPracticeRule lookup so violations can reference their rule object
        // (used by CreateDriftSnapshot to read Rule.Category without requiring a DB round-trip).
        var ruleObjects = await db.BestPracticeRules
            .Where(r => ruleIdStrings.Contains(r.RuleId))
            .ToDictionaryAsync(r => r.Id, ct);

        // --- Step A: Delete old active violations and persist new ones ---
        var ruleGuidValues = ruleGuids.Values.ToArray();
        await db.BestPracticeViolations
            .Where(v => v.ServiceGroupId == run.ServiceGroupId
                        && ruleGuidValues.Contains(v.RuleId)
                        && v.Status == "active")
            .ExecuteDeleteAsync(ct);

        var violations = new List<BestPracticeViolation>();

        foreach (var resource in resources)
        {
            var tags = ParseTags(resource.Tags);
            var properties = ParseFlatProperties(resource.Properties);

            foreach (var seed in seedRules)
            {
                if (!IsApplicableToResourceType(resource.ResourceType, seed.ApplicabilityScope))
                    continue;

                if (!IsRuleViolation(seed.RuleId, resource, tags, properties))
                    continue;

                var ruleGuid = ruleGuids[seed.RuleId];
                violations.Add(new BestPracticeViolation
                {
                    Id = Guid.NewGuid(),
                    RuleId = ruleGuid,
                    Rule = ruleObjects.GetValueOrDefault(ruleGuid)!,
                    ServiceGroupId = run.ServiceGroupId,
                    AnalysisRunId = run.Id,
                    ResourceId = resource.ArmId,
                    ResourceType = resource.ResourceType,
                    ViolationType = "non_compliance",
                    Severity = seed.Severity,
                    DriftCategory = ClassifyDriftCategory(seed.Category),
                    CurrentState = JsonSerializer.Serialize(new
                    {
                        resource.Sku,
                        resource.Location,
                        tags,
                        resource.Properties
                    }),
                    ExpectedState = JsonSerializer.Serialize(new
                    {
                        seed.EvaluationQuery,
                        seed.RemediationGuidance
                    }),
                    DriftDetails = JsonSerializer.Serialize(new { seed.RuleId, seed.Source, seed.Pillar }),
                    DetectedAt = now,
                    Status = "active",
                    CreatedAt = now,
                });
            }
        }

        // Subscription-level checks (AMBA baseline coverage) that cannot be inferred
        // from a single resource payload are evaluated across each discovered subscription.
        foreach (var subscriptionGroup in resources
            .Where(r => !string.IsNullOrWhiteSpace(r.SubscriptionId))
            .GroupBy(r => r.SubscriptionId!, StringComparer.OrdinalIgnoreCase))
        {
            var subscriptionId = subscriptionGroup.Key;
            var subscriptionResources = subscriptionGroup.ToList();
            var targetResourceId = $"/subscriptions/{subscriptionId}";

            foreach (var seed in seedRules.Where(s => IsSubscriptionGlobalRule(s.RuleId)))
            {
                if (!IsSubscriptionGlobalViolation(seed.RuleId, subscriptionResources))
                    continue;

                var ruleGuid = ruleGuids[seed.RuleId];
                violations.Add(new BestPracticeViolation
                {
                    Id = Guid.NewGuid(),
                    RuleId = ruleGuid,
                    Rule = ruleObjects.GetValueOrDefault(ruleGuid)!,
                    ServiceGroupId = run.ServiceGroupId,
                    AnalysisRunId = run.Id,
                    ResourceId = targetResourceId,
                    ResourceType = "Microsoft.Resources/subscriptions",
                    ViolationType = "non_compliance",
                    Severity = seed.Severity,
                    CurrentState = JsonSerializer.Serialize(new
                    {
                        subscriptionId,
                        discoveredResourceCount = subscriptionResources.Count
                    }),
                    ExpectedState = JsonSerializer.Serialize(new
                    {
                        seed.EvaluationQuery,
                        seed.RemediationGuidance
                    }),
                    DriftCategory = ClassifyDriftCategory(seed.Category),
                    DriftDetails = JsonSerializer.Serialize(new { seed.RuleId, seed.Source, seed.Pillar }),
                    DetectedAt = now,
                    Status = "active",
                    CreatedAt = now,
                });
            }
        }

        if (violations.Count > 0)
        {
            db.BestPracticeViolations.AddRange(violations);
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Persisted {ViolationCount} best-practice violation(s) for service group {ServiceGroupId} [correlation={CorrelationId}]",
            violations.Count, run.ServiceGroupId, run.CorrelationId);

        return violations;
    }

    private static bool IsApplicableToResourceType(string resourceType, string[] scope)
    {
        if (scope.Length == 0 || scope.Any(s => s == "*"))
            return true;

        return scope.Any(s =>
            resourceType.Equals(s, StringComparison.OrdinalIgnoreCase) ||
            resourceType.StartsWith($"{s}/", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string?> ParseTags(string? tagsJson)
    {
        var tags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tagsJson))
            return tags;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string?>>(tagsJson);
            if (parsed == null)
                return tags;

            foreach (var kv in parsed)
                tags[kv.Key] = kv.Value;
        }
        catch (JsonException)
        {
            // Best effort only — tag parsing is not fatal; swallow malformed JSON and return empty tags.
        }

        return tags;
    }

    private static Dictionary<string, string?> ParseFlatProperties(string? propertiesJson)
    {
        var output = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(propertiesJson))
            return output;

        try
        {
            using var doc = JsonDocument.Parse(propertiesJson);
            FlattenJson(doc.RootElement, string.Empty, output);
        }
        catch (JsonException)
        {
            // Best effort only — property parsing is not fatal; swallow malformed JSON and return empty properties.
        }

        return output;
    }

    private static void FlattenJson(JsonElement element, string prefix, Dictionary<string, string?> output)
    {
        static string Join(string left, string right) =>
            string.IsNullOrEmpty(left) ? right : $"{left}.{right}";

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    FlattenJson(prop.Value, Join(prefix, prop.Name), output);
                break;
            case JsonValueKind.Array:
                output[$"{prefix}.length"] = element.GetArrayLength().ToString();
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                output[prefix] = element.GetBoolean().ToString();
                break;
            case JsonValueKind.String:
                output[prefix] = element.GetString();
                break;
            case JsonValueKind.Number:
                output[prefix] = element.ToString();
                break;
            default:
                output[prefix] = element.ToString();
                break;
        }
    }

    private static string? Prop(Dictionary<string, string?> props, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (props.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool HasTag(Dictionary<string, string?> tags, params string[] tagNames) =>
        tags.Keys.Any(k => tagNames.Any(t => k.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                                             k.Contains(t, StringComparison.OrdinalIgnoreCase)));

    private static bool IsRuleViolation(
        string ruleId,
        DiscoveredAzureResource resource,
        Dictionary<string, string?> tags,
        Dictionary<string, string?> props)
    {
        var sku = resource.Sku ?? string.Empty;
        var location = resource.Location ?? string.Empty;
        var resourceType = resource.ResourceType ?? string.Empty;
        var kind = resource.Kind ?? string.Empty;
        var publicAccess = Prop(props, "publicNetworkAccess", "properties.publicNetworkAccess");
        var httpsOnly = Prop(props, "httpsOnly", "properties.httpsOnly", "properties.supportsHttpsTrafficOnly", "supportsHttpsTrafficOnly");
        var secureTransfer = Prop(props, "supportsHttpsTrafficOnly", "properties.supportsHttpsTrafficOnly");
        var minTls = Prop(props, "minimumTlsVersion", "properties.minimumTlsVersion", "minimalTlsVersion", "properties.minimalTlsVersion");
        var privateEndpointCount = Prop(props, "privateEndpointConnections.length", "properties.privateEndpointConnections.length");
        var adminUserEnabled = Prop(props, "adminUserEnabled", "properties.adminUserEnabled");
        var anonymousPullEnabled = Prop(props, "anonymousPullEnabled", "properties.anonymousPullEnabled");
        var zoneRedundant = Prop(props, "zoneRedundant", "properties.zoneRedundant", "sku.tier");
        var iacManaged = Prop(props, "iacManaged", "tags.iacManaged", "managedBy", "tags.managedBy");
        var purgeProtection = Prop(props, "enablePurgeProtection", "properties.enablePurgeProtection");
        var softDelete = Prop(props, "enableSoftDelete", "properties.enableSoftDelete");
        var aksRbac = Prop(props, "enableRBAC", "properties.enableRBAC");
        var aksDisableLocalAccounts = Prop(props, "disableLocalAccounts", "properties.disableLocalAccounts");
        var allowInsecureIngress = Prop(props, "configuration.ingress.allowInsecure", "properties.configuration.ingress.allowInsecure");
        var minReplicas = Prop(props, "template.scale.minReplicas", "properties.template.scale.minReplicas");
        var appLogsDestination = Prop(props, "appLogsConfiguration.destination", "properties.appLogsConfiguration.destination");
        var logAnalyticsWorkspace = Prop(
            props,
            "appLogsConfiguration.logAnalyticsConfiguration.customerId",
            "properties.appLogsConfiguration.logAnalyticsConfiguration.customerId",
            "appLogsConfiguration.logAnalyticsConfiguration.workspaceId",
            "properties.appLogsConfiguration.logAnalyticsConfiguration.workspaceId");
        var disableLocalAuth = Prop(props, "disableLocalAuth", "properties.disableLocalAuth");

        var hasEnvironmentTag = HasTag(tags, "environment", "env");
        var hasOwnerTag = HasTag(tags, "owner");
        var hasCostTag = HasTag(tags, "cost", "costCentre", "costCenter");

        return ruleId switch
        {
            "ATLAS-SKU-001" => sku.Contains("Basic", StringComparison.OrdinalIgnoreCase) ||
                               sku.Contains("Free", StringComparison.OrdinalIgnoreCase) ||
                               sku.Contains("Developer", StringComparison.OrdinalIgnoreCase),
            "ATLAS-LOC-001" => string.IsNullOrWhiteSpace(location) ||
                               location.Equals("global", StringComparison.OrdinalIgnoreCase),
            "ATLAS-TAG-001" => !hasEnvironmentTag || !hasOwnerTag || !hasCostTag,
            "WAF-COST-002" => !hasEnvironmentTag || !hasOwnerTag || !hasCostTag,
            "WAF-SEC-001" => string.Equals(publicAccess, "Enabled", StringComparison.OrdinalIgnoreCase),
            "WAF-SEC-002" => !IsTlsAndHttpsCompliant(httpsOnly, minTls),
            "WAF-REL-001" => IsLikelyProduction(tags) && !IsZoneRedundant(zoneRedundant, sku, props),
            "PSRULE-ST-003" => !IsTls12OrHigher(minTls),
            "PSRULE-ST-004" => !IsTrue(secureTransfer),
            "PSRULE-APP-003" => !IsTrue(httpsOnly),
            "PSRULE-ACR-001" => string.Equals(adminUserEnabled, "true", StringComparison.OrdinalIgnoreCase),
            "PSRULE-ACR-002" => IsTrue(anonymousPullEnabled),
            "PSRULE-KV-001" => !IsTrue(purgeProtection),
            "PSRULE-KV-002" => IsFalse(softDelete),
            "PSRULE-AKS-001" => !IsTrue(aksRbac),
            "PSRULE-AKS-002" => !IsTrue(aksDisableLocalAccounts),
            "PSRULE-CA-001" => IsTrue(allowInsecureIngress),
            "PSRULE-CA-002" => IsLikelyProduction(tags) && !HasMinimumReplicas(minReplicas),
            "PSRULE-CAE-001" => !HasLogAnalyticsConfigured(appLogsDestination, logAnalyticsWorkspace),
            "FOUNDRY-SEC-001" => IsFoundryCandidate(resourceType, kind) && !IsTrue(disableLocalAuth),
            "FOUNDRY-NET-001" => IsFoundryCandidate(resourceType, kind) &&
                                string.Equals(publicAccess, "Enabled", StringComparison.OrdinalIgnoreCase) &&
                                !HasPrivateEndpoints(privateEndpointCount),
            "AMBA-MON-001" => false,
            "AMBA-MON-002" => false,
            "AMBA-MON-003" => false,
            "AQR-PRIV-001" => !HasPrivateEndpoints(privateEndpointCount) &&
                              !string.Equals(publicAccess, "Disabled", StringComparison.OrdinalIgnoreCase),
            "AAC-ARCH-004" => string.IsNullOrWhiteSpace(iacManaged) ||
                              !iacManaged.Contains("true", StringComparison.OrdinalIgnoreCase) &&
                              !iacManaged.Contains("bicep", StringComparison.OrdinalIgnoreCase) &&
                              !iacManaged.Contains("terraform", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsTlsAndHttpsCompliant(string? httpsOnly, string? minTls)
    {
        var https = string.Equals(httpsOnly, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(httpsOnly, "enabled", StringComparison.OrdinalIgnoreCase);
        var tls = IsTls12OrHigher(minTls);
        return https || tls;
    }

    private static bool IsTls12OrHigher(string? minTls) =>
        string.Equals(minTls, "TLS1_2", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(minTls, "1.2", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(minTls, "TLS1_3", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(minTls, "1.3", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(string? value) =>
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase);

    private static bool HasMinimumReplicas(string? minReplicas) =>
        int.TryParse(minReplicas, out var replicas) && replicas >= 1;

    private static bool HasLogAnalyticsConfigured(string? destination, string? workspaceId) =>
        destination != null &&
        destination.Contains("log-analytics", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(workspaceId);

    private static bool IsFoundryCandidate(string resourceType, string kind) =>
        resourceType.Contains("microsoft.cognitiveservices/accounts", StringComparison.OrdinalIgnoreCase) ||
        resourceType.Contains("microsoft.machinelearningservices/workspaces", StringComparison.OrdinalIgnoreCase) ||
        kind.Contains("openai", StringComparison.OrdinalIgnoreCase);

    private static bool IsSubscriptionGlobalRule(string ruleId) =>
        ruleId is "AMBA-MON-001" or "AMBA-MON-002" or "AMBA-MON-003";

    private static bool IsEngineManagedRuleId(string ruleId) =>
        ruleId.StartsWith("ATLAS-", StringComparison.OrdinalIgnoreCase) ||
        ruleId.StartsWith("WAF-", StringComparison.OrdinalIgnoreCase) ||
        ruleId.StartsWith("PSRULE-", StringComparison.OrdinalIgnoreCase) ||
        ruleId.StartsWith("AQR-", StringComparison.OrdinalIgnoreCase) ||
        ruleId.StartsWith("AAC-", StringComparison.OrdinalIgnoreCase) ||
        ruleId.StartsWith("AMBA-", StringComparison.OrdinalIgnoreCase) ||
        ruleId.StartsWith("FOUNDRY-", StringComparison.OrdinalIgnoreCase);

    private static bool IsSubscriptionGlobalViolation(string ruleId, IReadOnlyList<DiscoveredAzureResource> subscriptionResources) =>
        ruleId switch
        {
            "AMBA-MON-001" => !HasResourceType(subscriptionResources, "Microsoft.Insights/activityLogAlerts"),
            "AMBA-MON-002" => !HasResourceType(subscriptionResources, "Microsoft.Insights/actionGroups"),
            "AMBA-MON-003" => !HasResourceType(subscriptionResources, "Microsoft.Insights/metricAlerts"),
            _ => false
        };

    private static bool HasResourceType(IReadOnlyList<DiscoveredAzureResource> resources, string targetType) =>
        resources.Any(r =>
            string.Equals(r.ResourceType, targetType, StringComparison.OrdinalIgnoreCase) ||
            r.ResourceType.StartsWith($"{targetType}/", StringComparison.OrdinalIgnoreCase));

    private static bool HasPrivateEndpoints(string? privateEndpointCount) =>
        int.TryParse(privateEndpointCount, out var count) && count > 0;

    private static bool IsLikelyProduction(Dictionary<string, string?> tags) =>
        tags.Any(kv =>
            kv.Key.Contains("environment", StringComparison.OrdinalIgnoreCase) &&
            (kv.Value?.Contains("prod", StringComparison.OrdinalIgnoreCase) == true ||
             kv.Value?.Contains("production", StringComparison.OrdinalIgnoreCase) == true));

    private static bool IsZoneRedundant(string? zoneRedundant, string sku, Dictionary<string, string?> props)
    {
        if (zoneRedundant?.Contains("true", StringComparison.OrdinalIgnoreCase) == true ||
            zoneRedundant?.Contains("Zone", StringComparison.OrdinalIgnoreCase) == true ||
            sku.Contains("Zone", StringComparison.OrdinalIgnoreCase))
            return true;

        if (props.TryGetValue("zones.length", out var zones) &&
            int.TryParse(zones, out var zoneCount) &&
            zoneCount > 1)
            return true;

        return false;
    }

    private static string ClassifyDriftCategory(string? ruleCategory) =>
        ruleCategory?.ToLowerInvariant() switch
        {
            "security" => "SecurityDrift",
            "cost" or "finops" => "CostDrift",
            "reliability" or "availability" => "ConfigurationDrift",
            "performance" or "scalability" => "PerformanceDrift",
            "operations" or "compliance" or "governance" => "ComplianceDrift",
            _ => "ConfigurationDrift"
        };

    private static string GetCurrentFieldLabel(string ruleId) => ruleId switch
    {
        "ATLAS-SKU-001" or "WAF-REL-001" => "SKU",
        "ATLAS-LOC-001" => "Location",
        "ATLAS-TAG-001" or "WAF-COST-002" => "Tags",
        "WAF-SEC-001" or "FOUNDRY-NET-001" => "Public Network Access",
        "WAF-SEC-002" or "PSRULE-APP-003" => "HTTPS / TLS",
        "PSRULE-ST-003" => "Minimum TLS Version",
        "PSRULE-ST-004" => "Secure Transfer",
        "PSRULE-ACR-001" => "Admin User",
        "PSRULE-ACR-002" => "Anonymous Pull",
        "PSRULE-KV-001" => "Purge Protection",
        "PSRULE-KV-002" => "Soft Delete",
        "PSRULE-AKS-001" => "RBAC",
        "PSRULE-AKS-002" => "Local Accounts",
        "PSRULE-CA-001" => "Insecure Ingress",
        "PSRULE-CA-002" => "Minimum Replicas",
        "PSRULE-CAE-001" => "Log Analytics",
        "FOUNDRY-SEC-001" => "Local Authentication",
        _ => "Property"
    };

    private static string GetRequiredValueForRule(string ruleId) => ruleId switch
    {
        "ATLAS-SKU-001" => "Standard or Premium (production-grade SKU)",
        "ATLAS-LOC-001" => "Explicit Azure region (not null or global)",
        "ATLAS-TAG-001" or "WAF-COST-002" => "environment, owner, and cost-center tags",
        "WAF-SEC-001" or "FOUNDRY-NET-001" => "Disabled",
        "WAF-SEC-002" or "PSRULE-APP-003" => "HTTPS only enabled, TLS 1.2+",
        "WAF-REL-001" => "Zone-redundant SKU or zone-redundancy enabled",
        "PSRULE-ST-003" => "TLS 1.2 or higher",
        "PSRULE-ST-004" => "Secure transfer (HTTPS) enabled",
        "PSRULE-ACR-001" => "Disabled (use managed identities or RBAC)",
        "PSRULE-ACR-002" => "Disabled",
        "PSRULE-KV-001" => "Enabled",
        "PSRULE-KV-002" => "Enabled",
        "PSRULE-AKS-001" => "Enabled",
        "PSRULE-AKS-002" => "Disabled (use Entra ID identities)",
        "PSRULE-CA-001" => "Disabled (HTTPS-only ingress)",
        "PSRULE-CA-002" => "Minimum 2 replicas for production",
        "PSRULE-CAE-001" => "Log Analytics workspace configured",
        "FOUNDRY-SEC-001" => "Disabled (use managed identity)",
        _ => "See remediation guidance"
    };

    private static string GetViolationCurrentValue(string ruleId, string currentStateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(currentStateJson);
            var root = doc.RootElement;

            switch (ruleId)
            {
                case "ATLAS-SKU-001":
                case "WAF-REL-001":
                    return root.TryGetProperty("sku", out var skuEl) && skuEl.ValueKind == JsonValueKind.String
                        ? skuEl.GetString() ?? "—"
                        : "—";

                case "ATLAS-LOC-001":
                    return root.TryGetProperty("location", out var locEl) && locEl.ValueKind == JsonValueKind.String
                        ? (locEl.GetString() is { Length: > 0 } loc ? loc : "unassigned")
                        : "unassigned";

                case "ATLAS-TAG-001":
                case "WAF-COST-002":
                    return root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object
                        ? GetMissingTagsSummary(tagsEl)
                        : "no tags";

                default:
                    return GetCurrentPropertyValue(ruleId, root);
            }
        }
        catch
        {
            return "—";
        }
    }

    private static string GetMissingTagsSummary(JsonElement tagsEl)
    {
        var missing = new List<string>();
        bool HasTagKey(string key) => tagsEl.EnumerateObject()
            .Any(p => p.Name.Contains(key, StringComparison.OrdinalIgnoreCase));

        if (!HasTagKey("environment")) missing.Add("environment");
        if (!HasTagKey("owner")) missing.Add("owner");
        if (!HasTagKey("cost")) missing.Add("cost-center");

        return missing.Count > 0 ? $"Missing: {string.Join(", ", missing)}" : "Required tags present";
    }

    private static string GetCurrentPropertyValue(string ruleId, JsonElement root)
    {
        if (!root.TryGetProperty("properties", out var propsEl))
            return "—";

        // Properties are stored as a raw JSON string inside CurrentState.
        string propsJson = propsEl.ValueKind == JsonValueKind.String
            ? propsEl.GetString() ?? "{}"
            : propsEl.GetRawText();

        var flatProps = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var propsDoc = JsonDocument.Parse(propsJson);
            FlattenJson(propsDoc.RootElement, string.Empty, flatProps);
        }
        catch
        {
            return "—";
        }

        string? value = ruleId switch
        {
            "WAF-SEC-001" or "FOUNDRY-NET-001" =>
                Prop(flatProps, "publicNetworkAccess", "properties.publicNetworkAccess"),
            "WAF-SEC-002" or "PSRULE-APP-003" =>
                Prop(flatProps, "httpsOnly", "properties.httpsOnly"),
            "PSRULE-ST-003" =>
                Prop(flatProps, "minimumTlsVersion", "properties.minimumTlsVersion", "minimalTlsVersion"),
            "PSRULE-ST-004" =>
                Prop(flatProps, "supportsHttpsTrafficOnly", "properties.supportsHttpsTrafficOnly"),
            "PSRULE-ACR-001" =>
                Prop(flatProps, "adminUserEnabled", "properties.adminUserEnabled"),
            "PSRULE-ACR-002" =>
                Prop(flatProps, "anonymousPullEnabled", "properties.anonymousPullEnabled"),
            "PSRULE-KV-001" =>
                Prop(flatProps, "enablePurgeProtection", "properties.enablePurgeProtection"),
            "PSRULE-KV-002" =>
                Prop(flatProps, "enableSoftDelete", "properties.enableSoftDelete"),
            "PSRULE-AKS-001" =>
                Prop(flatProps, "enableRBAC", "properties.enableRBAC"),
            "PSRULE-AKS-002" =>
                Prop(flatProps, "disableLocalAccounts", "properties.disableLocalAccounts"),
            "PSRULE-CA-001" =>
                Prop(flatProps, "configuration.ingress.allowInsecure", "properties.configuration.ingress.allowInsecure"),
            "PSRULE-CA-002" =>
                Prop(flatProps, "template.scale.minReplicas", "properties.template.scale.minReplicas"),
            "FOUNDRY-SEC-001" =>
                Prop(flatProps, "disableLocalAuth", "properties.disableLocalAuth"),
            _ => null
        };

        return value ?? "—";
    }
}
