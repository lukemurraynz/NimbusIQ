using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.ValueTracking;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.ControlPlane.Api.Controllers;

/// <summary>
/// Feature #1: ROI & Value Tracking Dashboard
/// </summary>
[ApiController]
[Route("api/v1/value-tracking")]
public class ValueRealizationController : ControllerBase
{
    private readonly ValueRealizationService _valueTracking;
    private readonly ILogger<ValueRealizationController> _logger;

    public ValueRealizationController(
        ValueRealizationService valueTracking,
        ILogger<ValueRealizationController> logger)
    {
        _valueTracking = valueTracking;
        _logger = logger;
    }

    /// <summary>
    /// Initialize tracking for a recommendation
    /// </summary>
    [HttpPost("recommendations/{recommendationId}/initialize")]
    public async Task<IActionResult> InitializeTracking(
        Guid recommendationId,
        [FromBody] InitializeTrackingRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        try
        {
            var tracking = await _valueTracking.InitializeTrackingAsync(
                recommendationId,
                request.ChangeSetId,
                request.EstimatedMonthlySavings,
                request.EstimatedImplementationCost);
            return Ok(tracking);
        }
        catch (InvalidOperationException ex)
        {
            return this.ProblemBadRequest("ValueTrackingInitFailed", ex.Message);
        }
    }

    /// <summary>
    /// Record actual realized value
    /// </summary>
    [HttpPost("recommendations/{recommendationId}/record-value")]
    public async Task<IActionResult> RecordActualValue(
        Guid recommendationId,
        [FromBody] RecordValueRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        try
        {
            var tracking = await _valueTracking.RecordActualValueAsync(
                recommendationId,
                request.ActualMonthlySavings,
                request.ActualImplementationCost,
                request.Notes);

            return Ok(tracking);
        }
        catch (InvalidOperationException ex)
        {
            return this.ProblemNotFound("ValueTrackingNotFound", ex.Message);
        }
    }

    /// <summary>
    /// Get ROI dashboard data
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(
        [FromQuery] Guid? serviceGroupId = null,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var dashboard = await _valueTracking.GetDashboardDataAsync(serviceGroupId);
        return Ok(dashboard);
    }

    /// <summary>
    /// List all value tracking records
    /// </summary>
    [HttpGet("records")]
    public async Task<IActionResult> ListTrackingRecords(
        [FromQuery] string? status = null,
        [FromQuery] int limit = 50,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var records = await _valueTracking.GetTrackingRecordsAsync(status, limit, cancellationToken);
        return Ok(new { value = records });
    }
}

public record InitializeTrackingRequest(
    Guid? ChangeSetId,
    decimal EstimatedMonthlySavings,
    decimal EstimatedImplementationCost);

public record RecordValueRequest(
    decimal ActualMonthlySavings,
    decimal? ActualImplementationCost,
    string? Notes);
