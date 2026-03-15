using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Atlas.ControlPlane.Api.Endpoints;

/// <summary>
/// Surfaces sustainability intelligence: carbon emissions data sourced from the Azure Carbon
/// Optimization API (preview) and evaluated by the Sustainability Agent during analysis runs.
/// Data is written by the agent orchestrator as agent_messages with MessageType = 'sustainability.carbonEmissions'.
/// </summary>
[ApiController]
[Route("api/v1/sustainability")]
[Authorize(Policy = "AnalysisRead")]
public class SustainabilityController : ControllerBase
{
    private readonly AtlasDbContext _db;
    private readonly ILogger<SustainabilityController> _logger;
    private static readonly JsonSerializerOptions JsonReadOptions = new(JsonSerializerDefaults.Web);

    public SustainabilityController(AtlasDbContext db, ILogger<SustainabilityController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Get carbon emissions and sustainability assessment for a service group from its latest (or specified) analysis run.
    /// Carbon data is sourced from the Azure Carbon Optimization API when the managed identity has the
    /// Carbon Optimization Reader role. Falls back to estimates when the preview API is unavailable.
    /// </summary>
    /// <param name="serviceGroupId">The service group to query carbon data for.</param>
    /// <param name="analysisRunId">Optional: target a specific run. Defaults to the latest completed run.</param>
    /// <param name="apiVersion">Required API version parameter (YYYY-MM-DD format).</param>
    /// <param name="cancellationToken"></param>
    [HttpGet("carbon/{serviceGroupId}")]
    [ProducesResponseType(typeof(CarbonEmissionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCarbonEmissions(
        [FromRoute] Guid serviceGroupId,
        [FromQuery] Guid? analysisRunId = null,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        Guid runId;
        DateTime? assessedAt;
        string? payloadJson;

        if (analysisRunId.HasValue)
        {
            runId = analysisRunId.Value;
            var requestedRunMessage = await _db.AgentMessages
                .AsNoTracking()
                .Where(m => m.AnalysisRunId == runId && m.MessageType == "sustainability.carbonEmissions")
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new { m.Payload, m.CreatedAt })
                .FirstOrDefaultAsync(cancellationToken);

            payloadJson = requestedRunMessage?.Payload;
            assessedAt = requestedRunMessage?.CreatedAt;
        }
        else
        {
            var latestMessage = await _db.AnalysisRuns
                .AsNoTracking()
                .Where(r => r.ServiceGroupId == serviceGroupId &&
                            (r.Status == AnalysisRunStatus.Completed || r.Status == AnalysisRunStatus.Partial))
                .Join(
                    _db.AgentMessages.AsNoTracking().Where(m => m.MessageType == "sustainability.carbonEmissions"),
                    run => run.Id,
                    message => message.AnalysisRunId,
                    (run, message) => new
                    {
                        RunId = run.Id,
                        message.Payload,
                        message.CreatedAt
                    })
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (latestMessage is not null)
            {
                runId = latestMessage.RunId;
                payloadJson = latestMessage.Payload;
                assessedAt = latestMessage.CreatedAt;
            }
            else
            {
                var latestRun = await _db.AnalysisRuns
                    .AsNoTracking()
                    .Where(r => r.ServiceGroupId == serviceGroupId &&
                                (r.Status == AnalysisRunStatus.Completed || r.Status == AnalysisRunStatus.Partial))
                    .OrderByDescending(r => r.CompletedAt ?? r.CreatedAt)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (latestRun is null)
                {
                    return this.ProblemNotFound(
                        "NoCompletedAnalysisRun",
                        $"No completed or partial analysis run found for service group {serviceGroupId}. Trigger an analysis first.");
                }

                runId = latestRun.Value;
                payloadJson = null;
                assessedAt = null;
            }
        }

        if (payloadJson is null)
        {
            // No assessment yet — return an empty-state response so the UI can render gracefully
            return Ok(new CarbonEmissionsResponse
            {
                ServiceGroupId = serviceGroupId,
                AnalysisRunId = runId,
                AssessedAt = null,
                MonthlyKgCO2e = 0,
                HasRealData = false,
                DataStatus = "no_carbon_telemetry",
                DataAvailabilityReason = "No sustainability carbon emissions telemetry was produced for the latest completed or partial run.",
                SustainabilityScore = null,
                Confidence = null,
                TopFindings = [],
                TopRecommendations = [],
                AINarrative = null
            });
        }

        CarbonPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CarbonPayload>(payloadJson, JsonReadOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse sustainability carbon payload for run {RunId}", runId);

            const string errorCode = "SustainabilityCarbonPayloadJsonParseFailed";

            Response.Headers["x-error-code"] = errorCode;

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Failed to parse sustainability assessment results",
                Detail = "The sustainability assessment results could not be parsed due to invalid JSON."
            };

            problem.Extensions["errorCode"] = errorCode;
            problem.Extensions["traceId"] = HttpContext.TraceIdentifier;

            return StatusCode(StatusCodes.Status500InternalServerError, problem);
        }

        if (payload is null)
            return Ok(new CarbonEmissionsResponse
            {
                ServiceGroupId = serviceGroupId,
                AnalysisRunId = runId,
                DataStatus = "empty_payload",
                DataAvailabilityReason = "Sustainability payload was empty for this analysis run."
            });

        return Ok(new CarbonEmissionsResponse
        {
            ServiceGroupId = serviceGroupId,
            AnalysisRunId = runId,
            AssessedAt = assessedAt,
            MonthlyKgCO2e = payload.MonthlyKgCO2e,
            HasRealData = payload.HasRealData,
            DataStatus = payload.HasRealData ? "real_data" : "estimated_data",
            DataAvailabilityReason = payload.HasRealData
                ? null
                : "Estimated sustainability score is available, but carbon emissions telemetry is not sourced from the Azure Carbon Optimization API.",
            SustainabilityScore = payload.SustainabilityScore,
            Confidence = payload.Confidence,
            RegionEmissions = payload.RegionEmissions ?? [],
            TopFindings = payload.TopFindings ?? [],
            TopRecommendations = payload.TopRecommendations ?? [],
            AINarrative = payload.AINarrative
        });
    }

    // ─── DTOs ──────────────────────────────────────────────────────────────────

    private sealed class CarbonPayload
    {
        public double MonthlyKgCO2e { get; set; }
        public bool HasRealData { get; set; }
        public double SustainabilityScore { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, double>? RegionEmissions { get; set; }
        public List<CarbonFindingDto>? TopFindings { get; set; }
        public List<CarbonRecommendationDto>? TopRecommendations { get; set; }
        public string? AINarrative { get; set; }
    }
}

// ─── Response model ────────────────────────────────────────────────────────────

public class CarbonEmissionsResponse
{
    public Guid ServiceGroupId { get; set; }
    public Guid AnalysisRunId { get; set; }
    public DateTime? AssessedAt { get; set; }

    /// <summary>Monthly carbon footprint in kg CO₂ equivalent. 0 when data is unavailable.</summary>
    public double MonthlyKgCO2e { get; set; }

    /// <summary>
    /// True when data was sourced from the Azure Carbon Optimization API.
    /// False when only estimate-based scoring was used (e.g., API not available or no permission).
    /// </summary>
    public bool HasRealData { get; set; }

    /// <summary>
    /// Data status for carbon telemetry: real_data | estimated_data | no_carbon_telemetry | empty_payload.
    /// </summary>
    public string DataStatus { get; set; } = "unknown";

    /// <summary>
    /// Human-readable explanation when carbon emissions telemetry is unavailable or estimated.
    /// </summary>
    public string? DataAvailabilityReason { get; set; }

    /// <summary>Overall sustainability score (0–100) from the Sustainability Agent.</summary>
    public double? SustainabilityScore { get; set; }

    /// <summary>Agent confidence in the assessment (0–1).</summary>
    public double? Confidence { get; set; }

    /// <summary>Carbon emissions broken down by Azure region (kg CO₂e/month).</summary>
    public Dictionary<string, double> RegionEmissions { get; set; } = [];

    /// <summary>Critical and high-severity sustainability findings.</summary>
    public IReadOnlyList<CarbonFindingDto> TopFindings { get; set; } = [];

    /// <summary>Top priority sustainability recommendations.</summary>
    public IReadOnlyList<CarbonRecommendationDto> TopRecommendations { get; set; } = [];

    /// <summary>AI-generated sustainability narrative (null when AI is not configured).</summary>
    public string? AINarrative { get; set; }
}

public class CarbonFindingDto
{
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Impact { get; set; } = "";
}

public class CarbonRecommendationDto
{
    public string Priority { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}
