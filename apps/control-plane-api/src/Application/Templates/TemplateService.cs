using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Atlas.ControlPlane.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas.ControlPlane.Application.Templates;

/// <summary>
/// Feature #4: Recommendation Templates & Playbooks
/// Reusable solution patterns for common problems
/// </summary>
public class TemplateService
{
    private readonly AtlasDbContext _db;
    private readonly ILogger<TemplateService> _logger;

    public TemplateService(AtlasDbContext db, ILogger<TemplateService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Find applicable templates for a recommendation
    /// </summary>
    public async Task<List<RecommendationTemplate>> FindApplicableTemplatesAsync(
        Recommendation recommendation,
        CancellationToken cancellationToken = default)
    {
        var templates = await _db.RecommendationTemplates
            .Where(t => t.Category == recommendation.Category)
            .ToListAsync(cancellationToken);

        var applicable = new List<RecommendationTemplate>();

        foreach (var template in templates)
        {
            if (string.IsNullOrEmpty(template.ApplicabilityCriteria))
            {
                applicable.Add(template);
                continue;
            }

            try
            {
                var criteria = JsonSerializer.Deserialize<Dictionary<string, object>>(template.ApplicabilityCriteria);
                if (criteria == null) continue;

                var matches = true;
                if (criteria.TryGetValue("recommendationType", out var recType) &&
                    recType.ToString() != recommendation.RecommendationType)
                {
                    matches = false;
                }

                if (criteria.TryGetValue("actionType", out var actType) &&
                    actType.ToString() != recommendation.ActionType)
                {
                    matches = false;
                }

                if (matches)
                {
                    applicable.Add(template);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evaluate applicability criteria for template {TemplateId}", template.Id);
            }
        }

        return applicable.OrderByDescending(t => t.AverageSuccessRate).ThenByDescending(t => t.UsageCount).ToList();
    }

    /// <summary>
    /// Apply a template to a recommendation
    /// </summary>
    public async Task<AppliedTemplate> ApplyTemplateAsync(
        Guid templateId,
        Guid recommendationId,
        Dictionary<string, string> parameters,
        string appliedBy,
        CancellationToken cancellationToken = default)
    {
        var template = await _db.RecommendationTemplates.FindAsync(new object[] { templateId }, cancellationToken);
        var recommendation = await _db.Recommendations.FindAsync(new object[] { recommendationId }, cancellationToken);

        if (template == null || recommendation == null)
        {
            throw new InvalidOperationException("Template or recommendation not found");
        }

        // Validate parameters against schema
        if (!string.IsNullOrEmpty(template.ParameterSchema))
        {
            var schema = JsonSerializer.Deserialize<Dictionary<string, object>>(template.ParameterSchema);
            if (schema != null && schema.ContainsKey("required"))
            {
                var required = JsonSerializer.Deserialize<List<string>>(schema["required"].ToString()!);
                if (required != null)
                {
                    foreach (var req in required)
                    {
                        if (!parameters.ContainsKey(req))
                        {
                            throw new ArgumentException($"Required parameter missing: {req}");
                        }
                    }
                }
            }
        }

        // Generate IaC by substituting placeholders
        var generatedIac = SubstitutePlaceholders(template.IacTemplate, parameters);

        // Record usage
        var usage = new TemplateUsage
        {
            Id = Guid.NewGuid(),
            TemplateId = templateId,
            RecommendationId = recommendationId,
            AppliedBy = appliedBy,
            AppliedAt = DateTime.UtcNow,
            ParameterValues = JsonSerializer.Serialize(parameters),
            Outcome = "pending",
            CreatedAt = DateTime.UtcNow
        };

        _db.TemplateUsages.Add(usage);

        template.UsageCount++;
        template.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Applied template {TemplateName} to recommendation {RecommendationId}",
            template.TemplateName,
            recommendationId);

        return new AppliedTemplate
        {
            UsageId = usage.Id,
            TemplateName = template.TemplateName,
            GeneratedIac = generatedIac,
            Parameters = parameters,
            PreConditions = template.PreConditions,
            PostConditions = template.PostConditions
        };
    }

    /// <summary>
    /// Create a new template from a successful recommendation
    /// </summary>
    public async Task<RecommendationTemplate> CreateTemplateFromRecommendationAsync(
        Guid recommendationId,
        string templateName,
        string createdBy,
        Dictionary<string, string>? parameterMappings = null,
        CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations
            .Include(r => r.ChangeSets)
            .FirstOrDefaultAsync(r => r.Id == recommendationId, cancellationToken);

        if (recommendation == null)
        {
            throw new InvalidOperationException("Recommendation not found");
        }

        var changeSet = recommendation.ChangeSets.FirstOrDefault();
        if (changeSet == null)
        {
            throw new InvalidOperationException("No change set found for recommendation");
        }

        // Extract IaC and parameterize it
        var iacContent = GetChangeSetContent(changeSet);
        var (parameterizedIac, schema) = ParameterizeIac(iacContent, parameterMappings ?? new());
        var lens = Atlas.ControlPlane.Application.Recommendations.RecommendationLensCalculator.Calculate(recommendation, DateTime.UtcNow);
        var estimatedSavings = EstimateSavingsFromImpact(recommendation.EstimatedImpact);

        var template = new RecommendationTemplate
        {
            Id = Guid.NewGuid(),
            TemplateName = templateName,
            Category = recommendation.Category,
            ProblemPattern = recommendation.Description,
            SolutionPattern = recommendation.ProposedChanges,
            IacTemplate = parameterizedIac,
            ParameterSchema = JsonSerializer.Serialize(schema),
            EstimatedSavingsRange = estimatedSavings,
            TypicalRiskScore = lens.RiskScore,
            UsageCount = 0,
            AverageSuccessRate = 1.0m,
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.RecommendationTemplates.Add(template);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created template {TemplateName} from recommendation {RecommendationId}", templateName, recommendationId);

        return template;
    }

    /// <summary>
    /// Get template library with filtering and sorting
    /// </summary>
    public async Task<List<TemplateLibraryItem>> GetTemplateLibraryAsync(
        string? category = null,
        string? sortBy = "usage",
        CancellationToken cancellationToken = default)
    {
        var query = _db.RecommendationTemplates.AsQueryable();

        if (!string.IsNullOrEmpty(category))
        {
            query = query.Where(t => t.Category == category);
        }

        query = sortBy?.ToLowerInvariant() switch
        {
            "usage" => query.OrderByDescending(t => t.UsageCount),
            "success" => query.OrderByDescending(t => t.AverageSuccessRate),
            "recent" => query.OrderByDescending(t => t.CreatedAt),
            _ => query.OrderByDescending(t => t.UsageCount)
        };

        var templates = await query.ToListAsync(cancellationToken);

        return templates.Select(t => new TemplateLibraryItem
        {
            Id = t.Id,
            Name = t.TemplateName,
            Category = t.Category,
            ProblemPattern = t.ProblemPattern,
            SolutionSummary = t.SolutionPattern.Length > 200 ? t.SolutionPattern[..200] + "..." : t.SolutionPattern,
            UsageCount = t.UsageCount,
            SuccessRate = t.AverageSuccessRate,
            EstimatedSavings = t.EstimatedSavingsRange,
            RiskScore = t.TypicalRiskScore,
            CreatedBy = t.CreatedBy,
            CreatedAt = t.CreatedAt
        }).ToList();
    }

    private string SubstitutePlaceholders(string template, Dictionary<string, string> parameters)
    {
        var result = template;
        foreach (var (key, value) in parameters)
        {
            result = result.Replace($"{{{{{key}}}}}", value); // Replace {{key}} with value
        }
        return result;
    }

    private (string parameterizedIac, object schema) ParameterizeIac(string iac, Dictionary<string, string> mappings)
    {
        // Simple parameterization: replace specific values with placeholders
        var parameterized = iac;
        var parameters = new Dictionary<string, object>();

        foreach (var (key, pattern) in mappings)
        {
            var regex = new Regex(pattern);
            parameterized = regex.Replace(parameterized, $"{{{{{key}}}}}");
            parameters[key] = new { type = "string", description = $"Value for {key}" };
        }

        var schema = new { required = mappings.Keys.ToArray(), properties = parameters };
        return (parameterized, schema);
    }

    private string GetChangeSetContent(IacChangeSet changeSet)
    {
        var decodeErrors = new List<string>();
        var decoded = IacArtifactStorageCodec.TryDecode(changeSet, decodeErrors);
        if (!string.IsNullOrWhiteSpace(decoded))
        {
            return decoded;
        }

        _logger.LogWarning(
            "Artifact retrieval failed for change set {ChangeSetId} with uri {ArtifactUri}. Errors: {DecodeErrors}",
            changeSet.Id,
            changeSet.ArtifactUri,
            string.Join("; ", decodeErrors));

        throw new InvalidOperationException(
            "Change set artifact is unavailable for templating.");
    }

    private static decimal EstimateSavingsFromImpact(string? estimatedImpact)
    {
        if (string.IsNullOrWhiteSpace(estimatedImpact))
        {
            return 0m;
        }

        try
        {
            using var doc = JsonDocument.Parse(estimatedImpact);
            var root = doc.RootElement;

            if (root.TryGetProperty("monthlySavings", out var monthlySavings) &&
                monthlySavings.TryGetDecimal(out var savings))
            {
                return Math.Max(0m, savings);
            }

            if (root.TryGetProperty("annualSavings", out var annualSavings) &&
                annualSavings.TryGetDecimal(out var annual))
            {
                return Math.Max(0m, annual / 12m);
            }

            if (root.TryGetProperty("costDelta", out var costDelta) &&
                costDelta.TryGetDecimal(out var delta))
            {
                return delta < 0m ? Math.Abs(delta) : 0m;
            }
        }
        catch (JsonException)
        {
            return 0m;
        }

        return 0m;
    }
}

public record AppliedTemplate
{
    public Guid UsageId { get; init; }
    public string TemplateName { get; init; } = string.Empty;
    public string GeneratedIac { get; init; } = string.Empty;
    public Dictionary<string, string> Parameters { get; init; } = new();
    public string? PreConditions { get; init; }
    public string? PostConditions { get; init; }
}

public record TemplateLibraryItem
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string ProblemPattern { get; init; } = string.Empty;
    public string SolutionSummary { get; init; } = string.Empty;
    public int UsageCount { get; init; }
    public decimal SuccessRate { get; init; }
    public decimal EstimatedSavings { get; init; }
    public decimal RiskScore { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
