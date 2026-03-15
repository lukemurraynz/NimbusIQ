using Atlas.AgentOrchestrator.Integrations.Azure;
using Atlas.AgentOrchestrator.Integrations.MCP;
using Atlas.AgentOrchestrator.Integrations.Prompts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// T042: IaC generation workflow (Bicep/Terraform/mixed)
/// Uses Azure AI Foundry when configured for context-aware code generation;
/// falls back to rule-based templates otherwise.
/// </summary>
public class IacGenerationWorkflow
{
  private readonly ILogger<IacGenerationWorkflow> _logger;
  private readonly IAzureAIFoundryClient? _foundryClient;
  private readonly IPromptProvider? _promptProvider;
  private readonly AzureMcpToolClient? _azureMcpToolClient;

  public IacGenerationWorkflow(
      ILogger<IacGenerationWorkflow> logger,
      IAzureAIFoundryClient? foundryClient = null,
      IPromptProvider? promptProvider = null,
      AzureMcpToolClient? azureMcpToolClient = null)
  {
    _logger = logger;
    _foundryClient = foundryClient;
    _promptProvider = promptProvider;
    _azureMcpToolClient = azureMcpToolClient;
  }

  public async Task<IacPreview> GeneratePreviewAsync(
      Guid recommendationId,
      RecommendationDetails recommendation,
      CancellationToken cancellationToken = default)
  {
    var activity = Activity.Current;
    activity?.SetTag("recommendation.id", recommendationId);
    activity?.SetTag("recommendation.actionType", recommendation.ActionType);

    _logger.LogInformation(
        "Generating IaC preview for recommendation {RecommendationId} ({ActionType})",
        recommendationId,
        recommendation.ActionType);

    var preview = new IacPreview
    {
      RecommendationId = recommendationId,
      Format = DetermineFormat(recommendation),
      GeneratedAt = DateTime.UtcNow,
    };

    preview.ForwardChanges = await GenerateForwardChangesAsync(recommendation, preview.Format, cancellationToken);
    preview.RollbackPlan = await GenerateRollbackPlanAsync(recommendation, preview.Format, cancellationToken);
    preview.EstimatedImpact = CalculateImpact(recommendation);

    _logger.LogInformation(
        "Generated IaC preview for {RecommendationId}: {ResourceCount} resources, {Format} format",
        recommendationId,
        preview.EstimatedImpact.ResourceCount,
        preview.Format);

    return preview;
  }

  private string DetermineFormat(RecommendationDetails recommendation)
  {
    if (recommendation.TargetProvider == "azure")
    {
      return "bicep";
    }

    return "terraform";
  }

  private async Task<string> GenerateForwardChangesAsync(
      RecommendationDetails recommendation,
      string format,
      CancellationToken cancellationToken)
  {
    if (_foundryClient is not null)
    {
      try
      {
        return await GenerateViaFoundryAsync(recommendation, format, cancellationToken);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex,
            "Foundry IaC generation failed for {RecommendationId}; falling back to template",
            recommendation.RecommendationId);
      }
    }

    return format == "bicep"
        ? GenerateBicepCode(recommendation)
        : GenerateTerraformCode(recommendation);
  }

  private async Task<string> GenerateViaFoundryAsync(
      RecommendationDetails recommendation,
      string format,
      CancellationToken cancellationToken)
  {
    var prompt = BuildIacPromptFromTemplate(recommendation, format);
    var mcpGuidance = await BuildMcpGuidanceAsync(cancellationToken);
    if (!string.IsNullOrWhiteSpace(mcpGuidance))
    {
      prompt = $"{prompt}\n\n{mcpGuidance}";
    }

    _logger.LogInformation(
        "Calling Azure AI Foundry to generate {Format} for {ActionType} on {Resource}",
        format,
        recommendation.ActionType,
        recommendation.ResourceName);

    var response = await _foundryClient!.SendPromptAsync(prompt, cancellationToken);
    var codeBlock = ExtractCodeBlock(response, format);
    return string.IsNullOrWhiteSpace(codeBlock) ? response : codeBlock;
  }

  private async Task<string> BuildMcpGuidanceAsync(CancellationToken cancellationToken)
  {
    if (_azureMcpToolClient is null)
    {
      return string.Empty;
    }

    try
    {
      var tools = await _azureMcpToolClient.ListToolsAsync(cancellationToken);
      var relevantTools = tools
        .Select(static t => t.Name)
        .Where(static name => !string.IsNullOrWhiteSpace(name))
        .Where(name =>
          name.Contains("cost", StringComparison.OrdinalIgnoreCase)
          || name.Contains("resource", StringComparison.OrdinalIgnoreCase)
          || name.Contains("policy", StringComparison.OrdinalIgnoreCase)
          || name.Contains("security", StringComparison.OrdinalIgnoreCase))
        .Take(12)
        .ToList();

      if (relevantTools.Count == 0)
      {
        return "Azure MCP is available; generate code that remains compatible with MCP-validated Azure resource changes.";
      }

      return $"Azure MCP capabilities available for future-state planning: {string.Join(", ", relevantTools)}. Prefer changes that can be validated against these Azure capabilities.";
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Unable to enrich IaC prompt with Azure MCP capabilities");
      return "Azure MCP enrichment unavailable for this request; proceed with deterministic Azure best-practice IaC output.";
    }
  }

  private string BuildIacPromptFromTemplate(RecommendationDetails recommendation, string format)
  {
    if (_promptProvider is null)
    {
      throw new InvalidOperationException("Prompt provider is required for IaC forward prompt rendering.");
    }

    return _promptProvider.Render(
        "iac-forward",
        new Dictionary<string, string>
        {
          ["Format"] = format,
          ["FormatUpper"] = format.ToUpperInvariant(),
          ["ActionType"] = recommendation.ActionType,
          ["ResourceName"] = recommendation.ResourceName,
          ["CurrentSku"] = recommendation.CurrentSku,
          ["TargetSku"] = recommendation.TargetSku,
          ["TargetRegion"] = recommendation.TargetRegion,
          ["EstimatedMonthlyCost"] = recommendation.EstimatedMonthlyCost.ToString("F2"),
          ["ConfidenceScorePercent"] = (recommendation.ConfidenceScore * 100).ToString("F0")
        });
  }

  private static string ExtractCodeBlock(string response, string format)
  {
    var fence = $"```{format}";
    var start = response.IndexOf(fence, StringComparison.OrdinalIgnoreCase);
    if (start < 0)
    {
      start = response.IndexOf("```", StringComparison.Ordinal);
      if (start < 0)
      {
        return string.Empty;
      }
    }

    start = response.IndexOf('\n', start) + 1;
    var end = response.IndexOf("```", start, StringComparison.Ordinal);
    return end > start ? response[start..end].Trim() : string.Empty;
  }

  private string GenerateBicepCode(RecommendationDetails recommendation)
  {
    var sb = new StringBuilder();

    sb.AppendLine("// Generated by Atlas - Autonomous Cloud Evolution Engine");
    sb.AppendLine($"// Recommendation ID: {recommendation.RecommendationId}");
    sb.AppendLine($"// Action: {recommendation.ActionType}");
    sb.AppendLine();

    sb.AppendLine("@description('Location for resources')");
    sb.AppendLine("param location string = resourceGroup().location");
    sb.AppendLine();

    switch (recommendation.ActionType)
    {
      case "scale_up":
        sb.AppendLine("resource existingResource 'Microsoft.Compute/virtualMachines@2023-09-01' existing = {");
        sb.AppendLine($"  name: '{recommendation.ResourceName}'");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("resource scaledResource 'Microsoft.Compute/virtualMachines@2023-09-01' = {");
        sb.AppendLine($"  name: '{recommendation.ResourceName}'");
        sb.AppendLine("  location: location");
        sb.AppendLine("  properties: {");
        sb.AppendLine("    hardwareProfile: {");
        sb.AppendLine($"      vmSize: '{recommendation.TargetSku}'");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        break;

      case "add_resource":
        sb.AppendLine($"resource newResource 'Microsoft.Storage/storageAccounts@2023-05-01' = {{");
        sb.AppendLine($"  name: '{recommendation.ResourceName}'");
        sb.AppendLine("  location: location");
        sb.AppendLine("  sku: {");
        sb.AppendLine($"    name: '{recommendation.TargetSku}'");
        sb.AppendLine("  }");
        sb.AppendLine("  kind: 'StorageV2'");
        sb.AppendLine("}");
        break;
    }

    return sb.ToString();
  }

  private string GenerateTerraformCode(RecommendationDetails recommendation)
  {
    var sb = new StringBuilder();

    sb.AppendLine("# Generated by Atlas - Autonomous Cloud Evolution Engine");
    sb.AppendLine($"# Recommendation ID: {recommendation.RecommendationId}");
    sb.AppendLine($"# Action: {recommendation.ActionType}");
    sb.AppendLine();

    sb.AppendLine("terraform {");
    sb.AppendLine("  required_version = \">= 1.5.0\"");
    sb.AppendLine("}");
    sb.AppendLine();

    sb.AppendLine($"resource \"azurerm_resource\" \"{recommendation.ResourceName}\" {{");
    sb.AppendLine($"  name     = \"{recommendation.ResourceName}\"");
    sb.AppendLine($"  location = \"{recommendation.TargetRegion}\"");
    sb.AppendLine($"  sku      = \"{recommendation.TargetSku}\"");
    sb.AppendLine("}");

    return sb.ToString();
  }

  private async Task<string> GenerateRollbackPlanAsync(
      RecommendationDetails recommendation,
      string format,
      CancellationToken cancellationToken)
  {
    if (_foundryClient is not null)
    {
      try
      {
        var rollbackPrompt = BuildRollbackPromptFromTemplate(recommendation, format);
        var response = await _foundryClient.SendPromptAsync(rollbackPrompt, cancellationToken);
        var code = ExtractCodeBlock(response, format);
        if (!string.IsNullOrWhiteSpace(code))
        {
          return code;
        }
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex,
            "Foundry rollback generation failed for {RecommendationId}; using template",
            recommendation.RecommendationId);
      }
    }

    var sb = new StringBuilder();
    sb.AppendLine($"# Rollback plan for recommendation {recommendation.RecommendationId}");
    sb.AppendLine();
    sb.AppendLine("## Prerequisites");
    sb.AppendLine("- Backup of current state captured");
    sb.AppendLine("- Rollback window: 24 hours");
    sb.AppendLine();
    sb.AppendLine("## Rollback steps");
    sb.AppendLine("1. Restore previous configuration from state backup");
    sb.AppendLine($"2. Scale back to original SKU: {recommendation.CurrentSku}");
    sb.AppendLine("3. Verify health checks");
    sb.AppendLine("4. Monitor for 15 minutes");

    return sb.ToString();
  }

  private string BuildRollbackPromptFromTemplate(RecommendationDetails recommendation, string format)
  {
    if (_promptProvider is null)
    {
      throw new InvalidOperationException("Prompt provider is required for IaC rollback prompt rendering.");
    }

    return _promptProvider.Render(
        "iac-rollback",
        new Dictionary<string, string>
        {
          ["Format"] = format,
          ["FormatUpper"] = format.ToUpperInvariant(),
          ["ResourceName"] = recommendation.ResourceName,
          ["TargetSku"] = recommendation.TargetSku,
          ["CurrentSku"] = recommendation.CurrentSku
        });
  }

  private ImpactEstimate CalculateImpact(RecommendationDetails recommendation)
  {
    return new ImpactEstimate
    {
      ResourceCount = 1,
      EstimatedDuration = TimeSpan.FromMinutes(5),
      CostImpact = recommendation.EstimatedMonthlyCost,
      RiskLevel = recommendation.ConfidenceScore > 0.8m ? "low" : "medium"
    };
  }
}

public class RecommendationDetails
{
  public Guid RecommendationId { get; set; }
  public string ActionType { get; set; } = string.Empty;
  public string ResourceName { get; set; } = string.Empty;
  public string CurrentSku { get; set; } = string.Empty;
  public string TargetSku { get; set; } = string.Empty;
  public string TargetRegion { get; set; } = string.Empty;
  public string TargetProvider { get; set; } = string.Empty;
  public decimal EstimatedMonthlyCost { get; set; }
  public decimal ConfidenceScore { get; set; }
}

public class IacPreview
{
  public Guid RecommendationId { get; set; }
  public string Format { get; set; } = string.Empty;
  public string ForwardChanges { get; set; } = string.Empty;
  public string RollbackPlan { get; set; } = string.Empty;
  public DateTime GeneratedAt { get; set; }
  public ImpactEstimate EstimatedImpact { get; set; } = null!;
}

public class ImpactEstimate
{
  public int ResourceCount { get; set; }
  public TimeSpan EstimatedDuration { get; set; }
  public decimal CostImpact { get; set; }
  public string RiskLevel { get; set; } = string.Empty;
}
