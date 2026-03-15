using System.Diagnostics;
using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Application.Recommendations;

/// <summary>
/// T032: Decision submission state machine with dual-control and idempotency
/// </summary>
public class DecisionService
{
    private readonly AtlasDbContext _context;
    private readonly ILogger<DecisionService> _logger;

    public DecisionService(AtlasDbContext context, ILogger<DecisionService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<DecisionResult> ApproveRecommendationAsync(
        Guid recommendationId,
        string approvedBy,
        string? comments,
        CancellationToken cancellationToken = default)
    {
        var recommendation = await _context.Recommendations
            .FirstOrDefaultAsync(r => r.Id == recommendationId, cancellationToken);

        if (recommendation == null)
        {
            return DecisionResult.NotFound();
        }

        // Idempotency: check if already approved by this user
        if (recommendation.ApprovedBy == approvedBy)
        {
            _logger.LogWarning("Duplicate approval attempt by {User} for {RecommendationId}",
                approvedBy, recommendationId);
            return DecisionResult.Success(recommendation, isIdempotent: true);
        }

        // State machine validation
        if (!RecommendationWorkflowStatus.CanApprove(recommendation.Status))
        {
            return DecisionResult.InvalidState($"Cannot approve recommendation in state {recommendation.Status}");
        }

        // Dual-control enforcement
        if (recommendation.ApprovalMode == "dual" && recommendation.RequiredApprovals == 2)
        {
            if (recommendation.ReceivedApprovals == 0)
            {
                // First approval
                recommendation.ReceivedApprovals = 1;
                recommendation.Status = RecommendationWorkflowStatus.PendingApproval;
                recommendation.ApprovedBy = approvedBy;
                recommendation.ApprovedAt = DateTime.UtcNow;
                recommendation.ApprovalComments = comments;

                _logger.LogInformation(
                    "First approval received for {RecommendationId} by {User}. Awaiting second approval.",
                    recommendationId, approvedBy);
            }
            else if (recommendation.ReceivedApprovals == 1)
            {
                // Second approval - complete
                recommendation.ReceivedApprovals = 2;
                recommendation.Status = RecommendationWorkflowStatus.Approved;
                recommendation.ApprovalComments = $"{recommendation.ApprovalComments}; {comments}";

                _logger.LogInformation(
                    "Second approval received for {RecommendationId} by {User}. Recommendation approved.",
                    recommendationId, approvedBy);
            }
        }
        else
        {
            // Single-control approval
            recommendation.Status = RecommendationWorkflowStatus.Approved;
            recommendation.ReceivedApprovals = 1;
            recommendation.ApprovedBy = approvedBy;
            recommendation.ApprovedAt = DateTime.UtcNow;
            recommendation.ApprovalComments = comments;

            _logger.LogInformation(
                "Single approval received for {RecommendationId} by {User}. Recommendation approved.",
                recommendationId, approvedBy);
        }

        // Audit event
        var auditEvent = new AuditEvent
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            ActorType = "user",
            ActorId = approvedBy,
            EventName = "recommendation_approved",
            EventPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                comments,
                approval_count = recommendation.ReceivedApprovals,
                recommendationId = recommendationId.ToString()
            }),
            TraceId = Activity.Current?.TraceId.ToString(),
            CreatedAt = DateTime.UtcNow,
            EventType = "recommendation_approved",
            EntityType = "recommendation",
            EntityId = recommendationId.ToString(),
            UserId = approvedBy,
            Timestamp = DateTime.UtcNow
        };

        _context.AuditEvents.Add(auditEvent);
        await _context.SaveChangesAsync(cancellationToken);

        return DecisionResult.Success(recommendation);
    }

    public async Task<DecisionResult> RejectRecommendationAsync(
        Guid recommendationId,
        string rejectedBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var recommendation = await _context.Recommendations
            .FirstOrDefaultAsync(r => r.Id == recommendationId, cancellationToken);

        if (recommendation == null)
        {
            return DecisionResult.NotFound();
        }

        // Idempotency: check if already rejected
        if (RecommendationWorkflowStatus.Normalize(recommendation.Status) == RecommendationWorkflowStatus.Rejected && recommendation.RejectedBy == rejectedBy)
        {
            _logger.LogWarning("Duplicate rejection attempt by {User} for {RecommendationId}",
                rejectedBy, recommendationId);
            return DecisionResult.Success(recommendation, isIdempotent: true);
        }

        recommendation.Status = RecommendationWorkflowStatus.Rejected;
        recommendation.RejectedBy = rejectedBy;
        recommendation.RejectedAt = DateTime.UtcNow;
        recommendation.RejectionReason = reason;

        var auditEvent = new AuditEvent
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            ActorType = "user",
            ActorId = rejectedBy,
            EventName = "recommendation_rejected",
            EventPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason,
                recommendationId = recommendationId.ToString()
            }),
            TraceId = Activity.Current?.TraceId.ToString(),
            CreatedAt = DateTime.UtcNow,
            EventType = "recommendation_rejected",
            EntityType = "recommendation",
            EntityId = recommendationId.ToString(),
            UserId = rejectedBy,
            Timestamp = DateTime.UtcNow
        };

        _context.AuditEvents.Add(auditEvent);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Recommendation {RecommendationId} rejected by {User}: {Reason}",
            recommendationId, rejectedBy, reason);

        return DecisionResult.Success(recommendation);
    }
}

public class DecisionResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public Domain.Entities.Recommendation? Recommendation { get; set; }
    public bool IsIdempotent { get; set; }

    public static DecisionResult Success(Domain.Entities.Recommendation recommendation, bool isIdempotent = false)
    {
        return new DecisionResult
        {
            IsSuccess = true,
            Recommendation = recommendation,
            IsIdempotent = isIdempotent
        };
    }

    public static DecisionResult NotFound()
    {
        return new DecisionResult
        {
            IsSuccess = false,
            ErrorMessage = "Recommendation not found"
        };
    }

    public static DecisionResult InvalidState(string message)
    {
        return new DecisionResult
        {
            IsSuccess = false,
            ErrorMessage = message
        };
    }
}
