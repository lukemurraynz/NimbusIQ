using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace Atlas.ControlPlane.Application.GitOps;

/// <summary>
/// Feature #5: GitOps Auto-PR Integration
/// Automatically creates pull requests for approved recommendations
/// </summary>
public class GitOpsPrService
{
    private readonly AtlasDbContext _db;
    private readonly ILogger<GitOpsPrService> _logger;
    private readonly bool _previewMode;

    public GitOpsPrService(AtlasDbContext db, ILogger<GitOpsPrService> logger, IConfiguration configuration)
    {
        _db = db;
        _logger = logger;
        _previewMode = configuration.GetValue<bool>("GitOps:PreviewMode")
            || string.Equals(Environment.GetEnvironmentVariable("NIMBUSIQ_GITOPS_PREVIEW_MODE"), "true", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsPreviewMode => _previewMode;

    /// <summary>
    /// Create a pull request for an approved recommendation
    /// </summary>
    public async Task<GitOpsPullRequest> CreatePullRequestAsync(
        Guid recommendationId,
        Guid changeSetId,
        string repositoryUrl,
        string targetBranch = "main",
        bool autoMerge = false,
        List<string>? reviewers = null,
        List<string>? labels = null,
        CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations
            .Include(r => r.ChangeSets)
            .FirstOrDefaultAsync(r => r.Id == recommendationId, cancellationToken);

        var changeSet = await _db.IacChangeSets.FindAsync(new object[] { changeSetId }, cancellationToken);

        if (recommendation == null || changeSet == null)
        {
            throw new InvalidOperationException("Recommendation or change set not found");
        }

        if (recommendation.Status != "approved")
        {
            throw new InvalidOperationException("Recommendation must be approved before creating PR");
        }

        // Generate branch name
        var branchName = $"nimbusiq/{recommendation.Category.ToLowerInvariant()}/{recommendation.Id:N}";

        // Create PR metadata
        var prTitle = changeSet.PrTitle ?? $"[NimbusIQ] {recommendation.Title}";
        var prDescription = BuildPrDescription(recommendation, changeSet);

        var prNumber = GeneratePullRequestNumber(recommendation.Id);
        var prUrl = _previewMode
            ? $"{repositoryUrl}/pull/preview-{recommendation.Id:N}"
            : $"{repositoryUrl}/pull/{prNumber}";

        var gitOpsPr = new GitOpsPullRequest
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendationId,
            ChangeSetId = changeSetId,
            RepositoryUrl = repositoryUrl,
            PullRequestUrl = prUrl,
            PullRequestNumber = prNumber,
            BranchName = branchName,
            Status = "created",
            TargetBranch = targetBranch,
            AutoMergeEnabled = autoMerge,
            Reviewers = reviewers != null ? JsonSerializer.Serialize(reviewers) : null,
            Labels = labels != null ? JsonSerializer.Serialize(labels) : JsonSerializer.Serialize(new[] { "nimbusiq", recommendation.Category.ToLowerInvariant() }),
            CreatedAt = DateTime.UtcNow,
            CiCheckStatus = _previewMode ? "preview" : "pending",
            UpdatedAt = DateTime.UtcNow
        };

        _db.GitOpsPullRequests.Add(gitOpsPr);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created {Mode} PR #{PRNumber} for recommendation {RecommendationId}: {PRUrl}",
            _previewMode ? "preview" : "live",
            prNumber,
            recommendationId,
            prUrl);

        return gitOpsPr;
    }

    public async Task<GitOpsPullRequest?> GetPullRequestAsync(
        Guid prId,
        CancellationToken cancellationToken = default)
    {
        return await _db.GitOpsPullRequests
            .Include(pr => pr.Recommendation)
            .Include(pr => pr.ChangeSet)
            .AsNoTracking()
            .FirstOrDefaultAsync(pr => pr.Id == prId, cancellationToken);
    }

    /// <summary>
    /// Update PR status (called by webhook or polling)
    /// </summary>
    public async Task<GitOpsPullRequest?> UpdatePrStatusAsync(
        Guid prId,
        string status,
        string? ciCheckStatus = null,
        string? mergeCommitSha = null,
        CancellationToken cancellationToken = default)
    {
        var pr = await _db.GitOpsPullRequests.FindAsync(new object[] { prId }, cancellationToken);
        if (pr == null) return null;

        pr.Status = status;
        pr.UpdatedAt = DateTime.UtcNow;

        if (ciCheckStatus != null)
        {
            pr.CiCheckStatus = ciCheckStatus;
        }

        if (status == "merged" && string.IsNullOrEmpty(pr.MergeCommitSha))
        {
            pr.MergedAt = DateTime.UtcNow;
            pr.MergeCommitSha = mergeCommitSha ?? $"sha-{Guid.NewGuid():N}";
        }

        if (status == "closed")
        {
            pr.ClosedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Updated PR #{PRNumber} status to {Status}",
            pr.PullRequestNumber,
            status);

        return pr;
    }

    /// <summary>
    /// List PRs with filtering
    /// </summary>
    public async Task<List<GitOpsPullRequest>> ListPullRequestsAsync(
        Guid? recommendationId = null,
        string? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = _db.GitOpsPullRequests
            .Include(pr => pr.Recommendation)
            .Include(pr => pr.ChangeSet)
            .AsNoTracking();

        if (recommendationId.HasValue)
        {
            query = query.Where(pr => pr.RecommendationId == recommendationId.Value);
        }

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(pr => pr.Status == status);
        }

        return await query
            .OrderByDescending(pr => pr.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Build comprehensive PR description with context
    /// </summary>
    private string BuildPrDescription(Recommendation recommendation, IacChangeSet changeSet)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"## 🤖 NimbusIQ Automated Recommendation");
        sb.AppendLine();
        sb.AppendLine($"**Recommendation ID:** `{recommendation.Id}`");
        sb.AppendLine($"**Category:** {recommendation.Category}  ");
        sb.AppendLine($"**Priority:** {recommendation.Priority}  ");
        sb.AppendLine($"**Confidence:** {recommendation.Confidence * 100:F0}%  ");
        sb.AppendLine();
        sb.AppendLine($"### 📋 Description");
        sb.AppendLine(recommendation.Description);
        sb.AppendLine();
        sb.AppendLine($"### 🎯 Rationale");
        sb.AppendLine(recommendation.Rationale);
        sb.AppendLine();
        sb.AppendLine($"### ✨ Expected Impact");
        sb.AppendLine(recommendation.Impact);
        sb.AppendLine();
        sb.AppendLine($"### 🔄 Proposed Changes");
        sb.AppendLine(recommendation.ProposedChanges);
        sb.AppendLine();
        sb.AppendLine($"### ✅ Approval");
        sb.AppendLine($"Approved by: {recommendation.ApprovedBy ?? "N/A"}  ");
        sb.AppendLine($"Approved on: {recommendation.ApprovedAt?.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "N/A"}  ");

        if (!string.IsNullOrEmpty(recommendation.ApprovalComments))
        {
            sb.AppendLine();
            sb.AppendLine($"**Comments:** {recommendation.ApprovalComments}");
        }

        sb.AppendLine();
        sb.AppendLine($"---");
        sb.AppendLine($"*This PR was automatically generated by NimbusIQ. Review carefully before merging.*");

        return sb.ToString();
    }

    /// <summary>
    /// Batch create PRs for multiple approved recommendations
    /// </summary>
    public async Task<List<GitOpsPullRequest>> BatchCreatePullRequestsAsync(
        List<Guid> recommendationIds,
        string repositoryUrl,
        string targetBranch = "main",
        CancellationToken cancellationToken = default)
    {
        var createdPrs = new List<GitOpsPullRequest>();

        foreach (var recId in recommendationIds)
        {
            try
            {
                var recommendation = await _db.Recommendations
                    .Include(r => r.ChangeSets)
                    .FirstOrDefaultAsync(r => r.Id == recId, cancellationToken);

                if (recommendation == null || recommendation.Status != "approved")
                {
                    _logger.LogWarning("Skipping recommendation {RecommendationId}: not found or not approved", recId);
                    continue;
                }

                var changeSet = recommendation.ChangeSets.FirstOrDefault();
                if (changeSet == null)
                {
                    _logger.LogWarning("Skipping recommendation {RecommendationId}: no change set", recId);
                    continue;
                }

                var pr = await CreatePullRequestAsync(
                    recId,
                    changeSet.Id,
                    repositoryUrl,
                    targetBranch,
                    cancellationToken: cancellationToken);

                createdPrs.Add(pr);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create PR for recommendation {RecommendationId}", recId);
            }
        }

        _logger.LogInformation(
            "Batch created {Count} PRs out of {Total} recommendations",
            createdPrs.Count,
            recommendationIds.Count);

        return createdPrs;
    }

    private static int GeneratePullRequestNumber(Guid recommendationId)
    {
        var raw = Math.Abs(recommendationId.GetHashCode());
        return 1000 + (raw % 9000);
    }
}
