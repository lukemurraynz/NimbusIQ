using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.Automation;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Atlas.ControlPlane.Api.Controllers;

/// <summary>
/// Feature #2: Automated Remediation for Low-Risk Changes
/// </summary>
[ApiController]
[Route("api/v1/automation")]
public class AutomationController : ControllerBase
{
    private readonly AutomationService _automation;
    private readonly AtlasDbContext _db;
    private readonly ILogger<AutomationController> _logger;

    public AutomationController(
        AutomationService automation,
        AtlasDbContext db,
        ILogger<AutomationController> logger)
    {
        _automation = automation;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate a recommendation for automation eligibility
    /// </summary>
    [HttpPost("recommendations/{recommendationId}/evaluate")]
    public async Task<IActionResult> EvaluateForAutomation(
        Guid recommendationId,
        [FromBody] EvaluateAutomationRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var recommendation = await _db.Recommendations.FindAsync(recommendationId);
        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        var result = await _automation.EvaluateForAutomationAsync(
            recommendation,
            request.RiskScore,
            request.TrustScore);

        return Ok(new
        {
            shouldAutoApprove = result.ShouldAutoApprove,
            matchedRule = result.MatchedRule
        });
    }

    /// <summary>
    /// Evaluate a recommendation for automation with agent-driven reasoning.
    /// Returns agent-generated insights about rule applicability, conflicts, and predicted impact.
    /// </summary>
    [HttpPost("recommendations/{recommendationId}/evaluate-with-reasoning")]
    public async Task<IActionResult> EvaluateForAutomationWithReasoning(
        Guid recommendationId,
        [FromBody] EvaluateAutomationRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var recommendation = await _db.Recommendations.FindAsync(recommendationId);
        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        var reasoning = await _automation.EvaluateForAutomationWithReasoningAsync(
            recommendation,
            request.RiskScore,
            request.TrustScore);

        return Ok(new
        {
            shouldAutoApprove = reasoning.ShouldAutoApprove,
            matchedRule = reasoning.MatchedRule,
            agentConfidence = reasoning.AgentConfidence,
            reasoningSummary = reasoning.ReasoningSummary,
            contributingAgents = reasoning.ContributingAgents,
            predictedImpactByPillar = reasoning.PredictedImpactByPillar,
            conflictingRules = reasoning.ConflictingRules,
            source = reasoning.Source
        });
    }

    /// <summary>
    /// Execute automation for a recommendation
    /// </summary>
    [HttpPost("recommendations/{recommendationId}/execute")]
    public async Task<IActionResult> ExecuteAutomation(
        Guid recommendationId,
        [FromBody] ExecuteAutomationRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        try
        {
            var execution = await _automation.ExecuteAutomationAsync(
                recommendationId,
                request.AutomationRuleId);
            return Ok(execution);
        }
        catch (InvalidOperationException ex)
        {
            return this.ProblemBadRequest("AutomationFailed", ex.Message);
        }
    }

    /// <summary>
    /// Create an automation rule
    /// </summary>
    [HttpPost("rules")]
    public async Task<IActionResult> CreateRule(
        [FromBody] CreateAutomationRuleRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var rule = new AutomationRule
        {
            Id = Guid.NewGuid(),
            RuleName = request.RuleName,
            Trigger = request.Trigger,
            TriggerCriteria = request.TriggerCriteria,
            MaxRiskThreshold = request.MaxRiskThreshold,
            MinConfidenceThreshold = request.MinConfidenceThreshold,
            ActionType = request.ActionType,
            ImplementationSchedule = request.ImplementationSchedule,
            RequiresAttestation = request.RequiresAttestation,
            IsEnabled = true,
            ExecutionCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.AutomationRules.Add(rule);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetRule), new { ruleId = rule.Id }, rule);
    }

    /// <summary>
    /// Get automation rule by ID
    /// </summary>
    [HttpGet("rules/{ruleId}")]
    public async Task<IActionResult> GetRule(
        Guid ruleId,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var rule = await _db.AutomationRules.FindAsync(ruleId);
        if (rule == null)
        {
            return this.ProblemNotFound("AutomationRuleNotFound", "Automation rule not found");
        }

        return Ok(rule);
    }

    /// <summary>
    /// List automation executions
    /// </summary>
    [HttpGet("executions")]
    public async Task<IActionResult> ListExecutions(
        [FromQuery] Guid? recommendationId = null,
        [FromQuery] string? status = null,
        [FromQuery] int limit = 50,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var query = _db.AutomationExecutions.AsQueryable();

        if (recommendationId.HasValue)
        {
            query = query.Where(e => e.RecommendationId == recommendationId.Value);
        }

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(e => e.Status == status);
        }

        var executions = await query
            .OrderByDescending(e => e.TriggeredAt)
            .Take(limit)
            .ToListAsync();

        return Ok(new { value = executions });
    }
}

public record EvaluateAutomationRequest(
    decimal RiskScore,
    decimal TrustScore);

public record ExecuteAutomationRequest(
    Guid AutomationRuleId);

public record CreateAutomationRuleRequest(
    string RuleName,
    string Trigger,
    string? TriggerCriteria,
    decimal MaxRiskThreshold,
    decimal MinConfidenceThreshold,
    string ActionType,
    string? ImplementationSchedule,
    bool RequiresAttestation);
