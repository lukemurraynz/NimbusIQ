using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.Recommendations;
using Atlas.ControlPlane.Application.Release;
using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Atlas.ControlPlane.Api.Endpoints;

/// <summary>
/// IaC change sets generated for approved recommendations.
/// Routes nest under /api/v1/recommendations/{recommendationId}/change-sets for collection
/// operations and /api/v1/change-sets/{id} for individual resource access.
/// </summary>
[ApiController]
public class ChangeSetsController : ControllerBase
{
    private readonly AtlasDbContext _db;
    private readonly IacGenerationService _iacService;
    private readonly ReleaseAttestationService _releaseAttestationService;
    private readonly IacGuardrailLinterService _guardrailLinter;
    private const string ValueBaselineEventName = "value_realization_baseline_captured";

    public ChangeSetsController(
        AtlasDbContext db,
        IacGenerationService iacService,
        ReleaseAttestationService releaseAttestationService,
        IacGuardrailLinterService guardrailLinter)
    {
        _db = db;
        _iacService = iacService;
        _releaseAttestationService = releaseAttestationService;
        _guardrailLinter = guardrailLinter;
    }

    /// <summary>
    /// List change sets associated with a recommendation.
    /// </summary>
    [HttpGet("api/v1/recommendations/{recommendationId}/change-sets")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> ListChangeSets(
        Guid recommendationId,
        CancellationToken cancellationToken = default)
    {
        var exists = await _db.Recommendations
            .AsNoTracking()
            .AnyAsync(r => r.Id == recommendationId, cancellationToken);

        if (!exists)
        {
            return this.ProblemNotFound("RecommendationNotFound", $"Recommendation {recommendationId} not found");
        }

        var changeSets = await _db.IacChangeSets
            .AsNoTracking()
            .Where(cs => cs.RecommendationId == recommendationId)
            .OrderByDescending(cs => cs.CreatedAt)
            .Select(cs => new
            {
                cs.Id,
                cs.RecommendationId,
                cs.Format,
                cs.PrTitle,
                cs.Status,
                cs.CreatedAt,
                hasContent = cs.ArtifactUri != null
            })
            .ToListAsync(cancellationToken);

        return Ok(new { value = changeSets });
    }

    /// <summary>
    /// Trigger IaC generation for a recommendation.
    /// Returns 202 Accepted when generation completes (synchronous for now; the
    /// operation-location header points to the created change set).
    /// </summary>
    [HttpPost("api/v1/recommendations/{recommendationId}/change-sets")]
    [Authorize(Policy = "RecommendationApprove")]
    public async Task<IActionResult> GenerateChangeSet(
        Guid recommendationId,
        [FromBody] GenerateChangeSetRequest request,
        CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == recommendationId, cancellationToken);

        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", $"Recommendation {recommendationId} not found");
        }

        if (!RecommendationWorkflowStatus.IsChangeSetEligible(recommendation.Status))
        {
            return this.ProblemBadRequest(
                "InvalidRecommendationStatus",
            $"Change sets can only be generated for recommendations with status '{RecommendationWorkflowStatus.Pending}', '{RecommendationWorkflowStatus.PendingApproval}', '{RecommendationWorkflowStatus.ManualReview}', or '{RecommendationWorkflowStatus.Approved}'. Current status: '{recommendation.Status}'");
        }

        IacChangeSet changeSet;
        try
        {
            changeSet = await _iacService.GenerateAsync(recommendationId, request.Format, cancellationToken);
        }
        catch (Exception ex)
        {
            return this.ProblemBadRequest("GenerationFailed", $"IaC generation failed: {ex.Message}");
        }

        Response.Headers["operation-location"] =
            $"{Request.Scheme}://{Request.Host}/api/v1/change-sets/{changeSet.Id}";

        return Accepted(new
        {
            changeSet.Id,
            changeSet.RecommendationId,
            changeSet.Format,
            changeSet.PrTitle,
            changeSet.Status,
            changeSet.CreatedAt
        });
    }

    /// <summary>
    /// Get a specific change set, including its generated IaC content.
    /// </summary>
    [HttpGet("api/v1/change-sets/{id}")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> GetChangeSet(Guid id, CancellationToken cancellationToken = default)
    {
        var changeSet = await _db.IacChangeSets
            .AsNoTracking()
            .FirstOrDefaultAsync(cs => cs.Id == id, cancellationToken);

        if (changeSet == null)
        {
            return this.ProblemNotFound("ChangeSetNotFound", $"Change set {id} not found");
        }

        var decodeErrors = new List<string>();
        var content = IacArtifactStorageCodec.TryDecode(changeSet, decodeErrors);

        return Ok(new
        {
            changeSet.Id,
            changeSet.RecommendationId,
            changeSet.Format,
            changeSet.PrTitle,
            changeSet.PrDescription,
            changeSet.Status,
            changeSet.ValidationResult,
            changeSet.CreatedAt,
            content,
            decodeErrors
        });
    }

    /// <summary>
    /// Run IaC preflight validation for a change set.
    /// </summary>
    [HttpPost("api/v1/change-sets/{id}/validate")]
    [Authorize(Policy = "RecommendationApprove")]
    public async Task<IActionResult> ValidateChangeSet(Guid id, CancellationToken cancellationToken = default)
    {
        var changeSet = await _db.IacChangeSets
            .FirstOrDefaultAsync(cs => cs.Id == id, cancellationToken);

        if (changeSet == null)
        {
            return this.ProblemNotFound("ChangeSetNotFound", $"Change set {id} not found");
        }

        if (changeSet.Status == "published")
        {
            return this.ProblemConflict(
                "ChangeSetAlreadyPublished",
                "Published change sets cannot be revalidated.");
        }

        var result = await _iacService.ValidateForPublishAsync(id, cancellationToken);

        return Ok(new
        {
            changeSet.Id,
            changeSet.Status,
            result.Passed,
            result.Errors,
            result.Warnings
        });
    }

    /// <summary>
    /// Lints an IaC artifact against governance guardrails before publish.
    /// </summary>
    [HttpPost("api/v1/change-sets/{id}/guardrail-lint")]
    [Authorize(Policy = "RecommendationApprove")]
    public async Task<IActionResult> GuardrailLintChangeSet(Guid id, CancellationToken cancellationToken = default)
    {
        var changeSet = await _db.IacChangeSets
            .AsNoTracking()
            .FirstOrDefaultAsync(cs => cs.Id == id, cancellationToken);

        if (changeSet == null)
        {
            return this.ProblemNotFound("ChangeSetNotFound", $"Change set {id} not found");
        }

        var lint = _guardrailLinter.Lint(changeSet);
        return Ok(new
        {
            changeSetId = changeSet.Id,
            lint.Passed,
            findings = lint.Findings
        });
    }

    /// <summary>
    /// Get value-realization deltas for a change set.
    /// </summary>
    [HttpGet("api/v1/change-sets/{id}/value-realization")]
    [Authorize(Policy = "RecommendationRead")]
    public async Task<IActionResult> GetValueRealization(Guid id, CancellationToken cancellationToken = default)
    {
        var changeSet = await _db.IacChangeSets
            .Include(cs => cs.Recommendation)
            .AsNoTracking()
            .FirstOrDefaultAsync(cs => cs.Id == id, cancellationToken);

        if (changeSet == null)
        {
            return this.ProblemNotFound("ChangeSetNotFound", $"Change set {id} not found");
        }

        var baseline = await TryReadBaselineAsync(changeSet.Id, cancellationToken);
        var current = await GetLatestScoresAsync(changeSet.Recommendation.ServiceGroupId, cancellationToken);

        var status = baseline is not null && current.Scores.Count > 0
            ? "available"
            : "pending";

        var deltas = new Dictionary<string, ScoreDelta>();
        if (baseline is not null && current.Scores.Count > 0)
        {
            foreach (var (category, currentScore) in current.Scores)
            {
                if (baseline.Scores.TryGetValue(category, out var baseScore))
                {
                    deltas[category] = new ScoreDelta(
                        DeltaScore: currentScore.Score - baseScore.Score,
                        ConfidenceDelta: currentScore.Confidence - baseScore.Confidence);
                }
            }
        }

        return Ok(new
        {
            changeSetId = changeSet.Id,
            status,
            baseline,
            current,
            deltas,
            updatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Mark a change set as published after passing preflight validation and
    /// release attestation gates.
    /// </summary>
    [HttpPost("api/v1/change-sets/{id}/publish")]
    [Authorize(Policy = "RecommendationApprove")]
    public async Task<IActionResult> PublishChangeSet(
        Guid id,
        [FromBody] PublishChangeSetRequest? request,
        CancellationToken cancellationToken = default)
    {
        var changeSet = await _db.IacChangeSets
            .FirstOrDefaultAsync(cs => cs.Id == id, cancellationToken);

        if (changeSet == null)
        {
            return this.ProblemNotFound("ChangeSetNotFound", $"Change set {id} not found");
        }

        if (changeSet.Status == "published")
        {
            var updated = await UpdateTemplateUsageOutcomeAsync(
                changeSet.RecommendationId,
                "succeeded",
                cancellationToken);
            if (updated)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            return Ok(new { changeSet.Id, changeSet.Status, isIdempotent = true });
        }

        if (changeSet.Status == "generated")
        {
            var preflight = await _iacService.ValidateForPublishAsync(id, cancellationToken);
            if (!preflight.Passed)
            {
                await UpdateTemplateUsageOutcomeAsync(
                    changeSet.RecommendationId,
                    "failed",
                    cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                var errorSummary = string.Join(" ", preflight.Errors);
                return this.ProblemBadRequest(
                    "IacPreflightValidationFailed",
                    $"Change set {id} failed preflight validation. {errorSummary}");
            }
        }

        if (changeSet.Status is not "validated")
        {
            return this.ProblemBadRequest(
                "InvalidChangeSetStatus",
                $"Only change sets with status 'validated' can be published. Current status: '{changeSet.Status}'");
        }

        var releaseId = request?.ReleaseId?.Trim();
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            releaseId = Request.Headers["x-release-id"].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(releaseId))
        {
            return this.ProblemBadRequest(
                "MissingReleaseId",
                "Release attestation requires a releaseId in the request body or x-release-id header.");
        }

        var componentName = string.IsNullOrWhiteSpace(request?.ComponentName)
            ? "control-plane-api"
            : request!.ComponentName!.Trim();

        var componentVersion = request?.ComponentVersion?.Trim();
        if (string.IsNullOrWhiteSpace(componentVersion))
        {
            componentVersion = Request.Headers["x-component-version"].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(componentVersion))
        {
            return this.ProblemBadRequest(
                "MissingComponentVersion",
                "Release attestation requires componentVersion in the request body or x-component-version header.");
        }

        var attestation = await _releaseAttestationService.CreateAttestationAsync(
            iacChangeSetId: changeSet.Id,
            releaseId: releaseId,
            componentName: componentName,
            componentVersion: componentVersion,
            mockDetected: request?.MockDetected ?? false,
            mockDetectionDetails: request?.MockDetectionDetails,
            validationScopeId: request?.ValidationScopeId,
            cancellationToken: cancellationToken);

        if (!attestation.ValidationPassed)
        {
            changeSet.Status = "failed";
            await UpdateTemplateUsageOutcomeAsync(
                changeSet.RecommendationId,
                "failed",
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            return this.ProblemBadRequest(
                "ReleaseAttestationFailed",
                $"Release attestation failed for change set {id}: {attestation.PromotionBlockReason ?? "validation did not pass"}");
        }

        var releasePassed = await _releaseAttestationService.ValidateReleaseAsync(releaseId, cancellationToken);
        if (!releasePassed)
        {
            changeSet.Status = "failed";
            await UpdateTemplateUsageOutcomeAsync(
                changeSet.RecommendationId,
                "failed",
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            return this.ProblemBadRequest(
                "ReleaseValidationFailed",
                $"Release {releaseId} has one or more failed component attestations and cannot be promoted.");
        }

        var baselineCaptured = await CaptureValueBaselineAsync(changeSet, cancellationToken);

        changeSet.Status = "published";
        await UpdateTemplateUsageOutcomeAsync(
            changeSet.RecommendationId,
            "succeeded",
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            changeSet.Id,
            changeSet.Status,
            releaseId,
            attestationId = attestation.Id,
            baselineCaptured,
            isIdempotent = false
        });
    }

    private async Task<bool> CaptureValueBaselineAsync(IacChangeSet changeSet, CancellationToken cancellationToken)
    {
        try
        {
            var exists = await _db.AuditEvents
                .AsNoTracking()
                .AnyAsync(a =>
                    a.EntityType == "change_set" &&
                    a.EntityId == changeSet.Id.ToString() &&
                    a.EventName == ValueBaselineEventName,
                    cancellationToken);

            if (exists)
            {
                return true;
            }

            var recommendation = await _db.Recommendations
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == changeSet.RecommendationId, cancellationToken);

            if (recommendation == null)
            {
                return false;
            }

            var baselineScores = await GetLatestScoresAsync(recommendation.ServiceGroupId, cancellationToken);

            var payload = new ValueRealizationSnapshot(
                RecordedAt: DateTime.UtcNow,
                Scores: baselineScores.Scores);

            var auditEvent = new AuditEvent
            {
                Id = Guid.NewGuid(),
                CorrelationId = recommendation.CorrelationId,
                ActorType = "system",
                ActorId = "atlas-system",
                EventName = ValueBaselineEventName,
                EventType = ValueBaselineEventName,
                EntityType = "change_set",
                EntityId = changeSet.Id.ToString(),
                EventPayload = JsonSerializer.Serialize(payload),
                Timestamp = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };

            _db.AuditEvents.Add(auditEvent);
            await _db.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> UpdateTemplateUsageOutcomeAsync(
        Guid recommendationId,
        string outcome,
        CancellationToken cancellationToken)
    {
        var usages = await _db.TemplateUsages
            .Where(u => u.RecommendationId == recommendationId)
            .ToListAsync(cancellationToken);

        if (usages.Count == 0)
        {
            return false;
        }

        foreach (var usage in usages)
        {
            usage.Outcome = outcome;
        }

        return true;
    }

    private async Task<ValueRealizationSnapshot?> TryReadBaselineAsync(Guid changeSetId, CancellationToken cancellationToken)
    {
        var baseline = await _db.AuditEvents
            .AsNoTracking()
            .Where(a =>
                a.EntityType == "change_set" &&
                a.EntityId == changeSetId.ToString() &&
                a.EventName == ValueBaselineEventName)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (baseline?.EventPayload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ValueRealizationSnapshot>(baseline.EventPayload);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ValueRealizationSnapshot> GetLatestScoresAsync(Guid serviceGroupId, CancellationToken cancellationToken)
    {
        var snapshots = await _db.ScoreSnapshots
            .AsNoTracking()
            .Where(s => s.ServiceGroupId == serviceGroupId)
            .ToListAsync(cancellationToken);

        var latestByCategory = snapshots
            .GroupBy(s => s.Category)
            .Select(g => g.OrderByDescending(s => s.RecordedAt).First())
            .ToDictionary(
                s => s.Category,
                s => new ScoreSnapshotView(s.Score, s.Confidence, s.RecordedAt));

        return new ValueRealizationSnapshot(
            RecordedAt: DateTime.UtcNow,
            Scores: latestByCategory);
    }
}

public record GenerateChangeSetRequest(string? Format = null);

public record PublishChangeSetRequest(
    string? ReleaseId = null,
    string? ComponentName = null,
    string? ComponentVersion = null,
    bool MockDetected = false,
    string? MockDetectionDetails = null,
    string? ValidationScopeId = null);

public sealed record ValueRealizationSnapshot(
    DateTime RecordedAt,
    Dictionary<string, ScoreSnapshotView> Scores);

public sealed record ScoreSnapshotView(double Score, double Confidence, DateTime RecordedAt);

public sealed record ScoreDelta(double DeltaScore, double ConfidenceDelta);
