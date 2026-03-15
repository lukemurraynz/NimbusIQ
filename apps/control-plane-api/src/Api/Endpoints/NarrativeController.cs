using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace Atlas.ControlPlane.Api.Endpoints;

[ApiController]
[Authorize(Policy = "AnalysisRead")]
[Route("api/v1/narrative")]
public class NarrativeController : ControllerBase
{
    private readonly ExecutiveNarrativeService _narrativeService;

    public NarrativeController(ExecutiveNarrativeService narrativeService) => _narrativeService = narrativeService;

    /// <summary>
    /// Generate an executive narrative summarising the current posture
    /// of a service group. Uses AI Foundry when available, with rule-based fallback.
    /// </summary>
    [HttpGet("{serviceGroupId}")]
    [ProducesResponseType(typeof(ExecutiveNarrativeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ExecutiveNarrativeDto>> GetNarrativeAsync(
        [FromRoute, Required] Guid serviceGroupId,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        ExecutiveNarrativeResult result;
        try
        {
            result = await _narrativeService.GenerateAsync(serviceGroupId);
        }
        catch (Exception ex)
        {
            return this.ProblemServiceUnavailable("NarrativeGenerationFailed",
                $"Failed to generate narrative for service group {serviceGroupId}: {ex.Message}");
        }

        return Ok(new ExecutiveNarrativeDto
        {
            Summary = result.Summary,
            Highlights = result.Highlights.Select(h => new NarrativeHighlightDto
            {
                Category = h.Category,
                Trend = h.Trend,
                Message = h.Message,
                Severity = h.Severity
            }).ToList(),
            GeneratedAt = result.GeneratedAt.ToString("O"),
            ConfidenceSource = result.ConfidenceSource
        });
    }
}

public class ExecutiveNarrativeDto
{
    public string Summary { get; set; } = "";
    public List<NarrativeHighlightDto> Highlights { get; set; } = [];
    public string GeneratedAt { get; set; } = "";
    public string ConfidenceSource { get; set; } = "rule_engine";
}

public class NarrativeHighlightDto
{
    public string Category { get; set; } = "";
    public string Trend { get; set; } = "";
    public string Message { get; set; } = "";
    public string Severity { get; set; } = "";
}
