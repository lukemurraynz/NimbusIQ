using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.Templates;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Atlas.ControlPlane.Api.Controllers;

/// <summary>
/// Feature #4: Recommendation Templates & Playbooks
/// </summary>
[ApiController]
[Route("api/v1/templates")]
public class TemplatesController : ControllerBase
{
    private readonly TemplateService _templates;
    private readonly AtlasDbContext _db;
    private readonly ILogger<TemplatesController> _logger;

    public TemplatesController(
        TemplateService templates,
        AtlasDbContext db,
        ILogger<TemplatesController> logger)
    {
        _templates = templates;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Find applicable templates for a recommendation
    /// </summary>
    [HttpGet("recommendations/{recommendationId}/applicable")]
    public async Task<IActionResult> FindApplicableTemplates(
        Guid recommendationId,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var recommendation = await _db.Recommendations.FindAsync(recommendationId);
        if (recommendation == null)
        {
            return this.ProblemNotFound("RecommendationNotFound", "Recommendation not found");
        }

        var templates = await _templates.FindApplicableTemplatesAsync(recommendation);
        return Ok(new { value = templates });
    }

    /// <summary>
    /// Apply a template to a recommendation
    /// </summary>
    [HttpPost("recommendations/{recommendationId}/apply")]
    public async Task<IActionResult> ApplyTemplate(
        Guid recommendationId,
        [FromBody] ApplyTemplateRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        try
        {
            var result = await _templates.ApplyTemplateAsync(
                request.TemplateId,
                recommendationId,
                request.Parameters,
                request.AppliedBy ?? "system");

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return this.ProblemBadRequest("TemplateApplyFailed", ex.Message);
        }
    }

    /// <summary>
    /// Create a template from an existing recommendation
    /// </summary>
    [HttpPost("recommendations/{recommendationId}/create-template")]
    public async Task<IActionResult> CreateTemplateFromRecommendation(
        Guid recommendationId,
        [FromBody] CreateTemplateRequest request,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        try
        {
            var template = await _templates.CreateTemplateFromRecommendationAsync(
                recommendationId,
                request.TemplateName,
                request.CreatedBy ?? "system",
                request.ParameterMappings);

            return CreatedAtAction(
                nameof(GetTemplate),
                new { templateId = template.Id },
                template);
        }
        catch (InvalidOperationException ex)
        {
            return this.ProblemBadRequest("TemplateCreationFailed", ex.Message);
        }
    }

    /// <summary>
    /// Get template by ID
    /// </summary>
    [HttpGet("{templateId}")]
    public async Task<IActionResult> GetTemplate(
        Guid templateId,
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var template = await _db.RecommendationTemplates.FindAsync(templateId);
        if (template == null)
        {
            return this.ProblemNotFound("TemplateNotFound", "Template not found");
        }

        return Ok(template);
    }

    /// <summary>
    /// Browse template library
    /// </summary>
    [HttpGet("library")]
    public async Task<IActionResult> GetTemplateLibrary(
        [FromQuery] string? category = null,
        [FromQuery] string? sortBy = "usage",
        [FromQuery(Name = "api-version")] string? apiVersion = null)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");
        var library = await _templates.GetTemplateLibraryAsync(category, sortBy);
        return Ok(new { value = library });
    }
}

public record ApplyTemplateRequest(
    Guid TemplateId,
    Dictionary<string, string> Parameters,
    string? AppliedBy);

public record CreateTemplateRequest(
    string TemplateName,
    string? CreatedBy,
    Dictionary<string, string>? ParameterMappings);
