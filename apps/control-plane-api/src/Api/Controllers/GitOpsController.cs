using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.GitOps;
using Atlas.ControlPlane.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.ControlPlane.Api.Controllers;

/// <summary>
/// Feature #5: GitOps Auto-PR Integration
/// </summary>
[ApiController]
[Route("api/v1/gitops")]
public class GitOpsController : ControllerBase
{
    private readonly GitOpsPrService _gitOps;
    private readonly ILogger<GitOpsController> _logger;

    public GitOpsController(
        GitOpsPrService gitOps,
        ILogger<GitOpsController> logger)
    {
        _gitOps = gitOps;
        _logger = logger;
    }

    /// <summary>
    /// Create a pull request for an approved recommendation
    /// </summary>
    [HttpPost("recommendations/{recommendationId}/create-pr")]
    public async Task<IActionResult> CreatePullRequest(
        Guid recommendationId,
        [FromBody] CreatePrRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        try
        {
            var pr = await _gitOps.CreatePullRequestAsync(
                recommendationId,
                request.ChangeSetId,
                request.RepositoryUrl,
                request.TargetBranch ?? "main",
                request.AutoMerge,
                request.Reviewers,
                request.Labels);

            return CreatedAtAction(
                nameof(GetPullRequest),
                new { prId = pr.Id },
                new
                {
                    id = pr.Id,
                    recommendationId = pr.RecommendationId,
                    changeSetId = pr.ChangeSetId,
                    repositoryUrl = pr.RepositoryUrl,
                    branchName = pr.BranchName,
                    prNumber = pr.PullRequestNumber,
                    prUrl = pr.PullRequestUrl,
                    status = pr.Status,
                    createdAt = pr.CreatedAt,
                    previewMode = _gitOps.IsPreviewMode
                });
        }
        catch (InvalidOperationException ex)
        {
            return this.ProblemBadRequest("GitOpsFailed", ex.Message);
        }
    }

    /// <summary>
    /// Get pull request by ID
    /// </summary>
    [HttpGet("pull-requests/{prId}")]
    public async Task<IActionResult> GetPullRequest(
        Guid prId,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var pr = await _gitOps.GetPullRequestAsync(prId);
        if (pr == null)
        {
            return this.ProblemNotFound("PullRequestNotFound", "PR not found");
        }

        return Ok(new
        {
            id = pr.Id,
            recommendationId = pr.RecommendationId,
            changeSetId = pr.ChangeSetId,
            repositoryUrl = pr.RepositoryUrl,
            branchName = pr.BranchName,
            prNumber = pr.PullRequestNumber,
            prUrl = pr.PullRequestUrl,
            status = pr.Status,
            ciCheckStatus = pr.CiCheckStatus,
            mergeCommitSha = pr.MergeCommitSha,
            createdAt = pr.CreatedAt,
            updatedAt = pr.UpdatedAt,
            mergedAt = pr.MergedAt,
            closedAt = pr.ClosedAt,
            previewMode = _gitOps.IsPreviewMode
        });
    }

    /// <summary>
    /// Update PR status (webhook endpoint)
    /// </summary>
    [HttpPatch("pull-requests/{prId}/status")]
    public async Task<IActionResult> UpdatePrStatus(
        Guid prId,
        [FromBody] UpdatePrStatusRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var pr = await _gitOps.UpdatePrStatusAsync(
            prId,
            request.Status,
            request.CiCheckStatus,
            request.MergeCommitSha);

        if (pr == null)
        {
            return this.ProblemNotFound("PullRequestNotFound", "PR not found");
        }

        return Ok(pr);
    }

    /// <summary>
    /// List pull requests
    /// </summary>
    [HttpGet("pull-requests")]
    public async Task<IActionResult> ListPullRequests(
        [FromQuery] Guid? recommendationId = null,
        [FromQuery] string? status = null,
        [FromQuery] int limit = 50,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var prs = await _gitOps.ListPullRequestsAsync(recommendationId, status, limit);
        return Ok(new
        {
            value = prs,
            previewMode = _gitOps.IsPreviewMode
        });
    }

    /// <summary>
    /// Batch create PRs for multiple recommendations
    /// </summary>
    [HttpPost("batch-create-prs")]
    public async Task<IActionResult> BatchCreatePullRequests(
        [FromBody] BatchCreatePrsRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var prs = await _gitOps.BatchCreatePullRequestsAsync(
            request.RecommendationIds,
            request.RepositoryUrl,
            request.TargetBranch ?? "main");

        return Ok(new
        {
            value = prs,
            total = prs.Count,
            requested = request.RecommendationIds.Count
        });
    }
}

public record CreatePrRequest(
    Guid ChangeSetId,
    string RepositoryUrl,
    string? TargetBranch,
    bool AutoMerge,
    List<string>? Reviewers,
    List<string>? Labels);

public record UpdatePrStatusRequest(
    string Status,
    string? CiCheckStatus,
    string? MergeCommitSha);

public record BatchCreatePrsRequest(
    List<Guid> RecommendationIds,
    string RepositoryUrl,
    string? TargetBranch);
