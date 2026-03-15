using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;

namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Generates IaC change sets (Bicep / Terraform) for approved recommendations.
/// Artifacts are stored inline as a data URI since no external artifact store is
/// configured; the URI scheme is "nimbusiq-inline:base64" and consumers decode it
/// back to plaintext.
/// </summary>
public class IacGenerationService
{
    private const int LegacyArtifactUriMaxLength = 1900;
    private readonly AtlasDbContext _db;
    private readonly ILogger<IacGenerationService> _logger;
    private readonly AIChatService? _aiChatService;

    public IacGenerationService(AtlasDbContext db, ILogger<IacGenerationService> logger, AIChatService? aiChatService = null)
    {
        _db = db;
        _logger = logger;
        _aiChatService = aiChatService;
    }

    public async Task<IacChangeSet> GenerateAsync(
        Guid recommendationId,
        string? preferredFormat,
        CancellationToken cancellationToken = default)
    {
        var recommendation = await _db.Recommendations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == recommendationId, cancellationToken)
            ?? throw new KeyNotFoundException($"Recommendation {recommendationId} not found");

        // Validate recommendation has required fields for change set generation
        if (string.IsNullOrWhiteSpace(recommendation.ActionType))
        {
            throw new InvalidOperationException("Recommendation ActionType is required for change set generation");
        }

        if (string.IsNullOrWhiteSpace(recommendation.RecommendationType))
        {
            throw new InvalidOperationException("Recommendation RecommendationType is required for change set generation");
        }

        var format = ResolveFormat(preferredFormat, recommendation);

        string artifact;
        try
        {
            artifact = _aiChatService?.IsAIAvailable == true
                ? await GenerateWithAIAsync(recommendation, format, cancellationToken)
                : format == "bicep"
                    ? GenerateBicep(recommendation)
                    : GenerateTerraform(recommendation);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to generate {format} artifact: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(artifact))
        {
            throw new InvalidOperationException($"Generated {format} artifact is empty");
        }

        var confidenceSource = _aiChatService?.IsAIAvailable == true ? "ai_foundry" : "template";

        var resourceName = recommendation.ResourceId?.Split('/').LastOrDefault() ?? "resource";
        var prTitle = $"[NimbusIQ] {recommendation.ActionType}: {resourceName}";

        if (string.IsNullOrWhiteSpace(prTitle) || prTitle.Length > 100)
        {
            prTitle = $"[NimbusIQ] {recommendation.RecommendationType}";
        }

        var prDescription = BuildPrDescription(recommendation) ?? "Apply recommended infrastructure change";
        var encodedArtifact = IacArtifactStorageCodec.EncodeForStorage(
            artifact,
            LegacyArtifactUriMaxLength,
            prDescription);

        var changeSet = new IacChangeSet
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendationId,
            Format = format,
            ArtifactUri = encodedArtifact.ArtifactUri,
            PrTitle = prTitle,
            PrDescription = encodedArtifact.PrDescription,
            Status = "generated",
            CreatedAt = DateTime.UtcNow
        };

        _db.IacChangeSets.Add(changeSet);

        var rollbackSteps = BuildRollbackSteps(recommendation);
        var rollbackPlan = new RollbackPlan
        {
            Id = Guid.NewGuid(),
            IacChangeSetId = changeSet.Id,
            RollbackSteps = JsonSerializer.Serialize(rollbackSteps),
            Preconditions = JsonSerializer.Serialize(new[]
            {
                "Verify current resource state before reverting",
                "Capture backup or snapshot if applicable",
                "Confirm health checks are passing"
            }),
            ValidationSteps = JsonSerializer.Serialize(new[]
            {
                "Verify resource reverted to prior configuration",
                "Run application smoke tests",
                "Confirm monitoring metrics are within normal range"
            }),
            CreatedAt = DateTime.UtcNow
        };
        _db.RollbackPlans.Add(rollbackPlan);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while saving change set for recommendation {RecommendationId}", recommendationId);
            throw new InvalidOperationException($"Failed to save change set: {ex.InnerException?.Message ?? ex.Message}", ex);
        }

        _logger.LogInformation(
            "Generated {Format} change set {ChangeSetId} for recommendation {RecommendationId} using {StorageMode} artifact encoding",
            format, changeSet.Id, recommendationId, encodedArtifact.StorageMode);

        return changeSet;
    }

    public async Task<IacPreflightValidationResult> ValidateForPublishAsync(
        Guid changeSetId,
        CancellationToken cancellationToken = default)
    {
        var changeSet = await _db.IacChangeSets
            .FirstOrDefaultAsync(cs => cs.Id == changeSetId, cancellationToken)
            ?? throw new KeyNotFoundException($"Change set {changeSetId} not found");

        var errors = new List<string>();
        var warnings = new List<string>();

        var artifactContent = IacArtifactStorageCodec.TryDecode(changeSet, errors);
        if (string.IsNullOrWhiteSpace(artifactContent))
        {
            errors.Add("Change set artifact content is empty or unreadable.");
        }
        else
        {
            switch (changeSet.Format.Trim().ToLowerInvariant())
            {
                case "bicep":
                    await ValidateBicepContentAsync(artifactContent, errors, warnings, cancellationToken);
                    break;
                case "terraform":
                    await ValidateTerraformContentAsync(artifactContent, errors, warnings, cancellationToken);
                    break;
                default:
                    warnings.Add($"No strict preflight validator is configured for format '{changeSet.Format}'.");
                    if (artifactContent.Length < 32)
                    {
                        errors.Add("Generated artifact is too short to be considered a valid IaC template.");
                    }
                    break;
            }
        }

        var passed = errors.Count == 0;
        var validatedAt = DateTime.UtcNow;

        changeSet.ValidationResult = JsonSerializer.Serialize(new
        {
            validatedAt,
            passed,
            changeSet.Format,
            errors,
            warnings
        });
        changeSet.Status = passed ? "validated" : "failed";

        await _db.SaveChangesAsync(cancellationToken);

        return new IacPreflightValidationResult(passed, errors, warnings);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AI-powered IaC generation
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string> GenerateWithAIAsync(Recommendation recommendation, string format, CancellationToken cancellationToken)
    {
        var resourceName = recommendation.ResourceId?.Split('/').LastOrDefault() ?? "atlasResource";
        var prompt = $$"""
            Generate a production-ready {{format}} template for the following Azure infrastructure change.

            Resource: {{recommendation.ResourceId}}
            Resource Name: {{resourceName}}
            Recommendation Type: {{recommendation.RecommendationType}}
            Action: {{recommendation.ActionType}}
            Priority: {{recommendation.Priority}}
            Rationale: {{recommendation.Rationale ?? "Not specified"}}
            Proposed Changes: {{recommendation.ProposedChanges ?? "Not specified"}}
            Estimated Impact: {{recommendation.EstimatedImpact ?? "Not specified"}}

            Requirements:
            - Output ONLY the {{format}} code, no markdown fences or explanations
            - Include a header comment with recommendation metadata
            - Include capability mode marker in header comment: capabilityMode=generated_not_applied
            - Use parameterized values (no hardcoded resource names)
            - Include appropriate default values for parameters
            - Follow Azure Well-Architected Framework best practices
            - For Bicep: use latest stable API versions, add @description decorators
            - For Terraform: include required_providers block with azurerm ~> 4.0
            - Add safety comments where the change requires validation or downtime
            - Do not include credentials, secrets, access keys, or tokens in output
            - Do not include imperative execution commands (az, kubectl, bash, powershell)
            - Restrict output to declarative IaC only; no destructive operations unless explicitly present in recommendation intent
            - If required fields are missing, output a safe parameterized placeholder rather than guessing
            """;

        var context = new InfrastructureContext
        {
            ServiceGroupCount = 0,
            DetailedDataJson = JsonSerializer.Serialize(new
            {
                recommendation.RecommendationType,
                recommendation.ActionType,
                recommendation.ResourceId,
                recommendation.Priority,
                recommendation.Rationale,
                recommendation.ProposedChanges,
                Format = format
            })
        };

        try
        {
            var response = await _aiChatService!.GenerateResponseAsync(prompt, context, cancellationToken);
            if (response.ConfidenceSource == "ai_foundry" && !string.IsNullOrWhiteSpace(response.Text))
            {
                _logger.LogInformation("AI-generated {Format} template for recommendation {Id}", format, recommendation.Id);
                return response.Text.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI IaC generation failed for recommendation {Id}, falling back to templates", recommendation.Id);
        }

        // Fallback to template-based generation
        return format == "bicep" ? GenerateBicep(recommendation) : GenerateTerraform(recommendation);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Format resolution
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> BuildRollbackSteps(Recommendation recommendation) =>
        recommendation.ActionType?.ToLowerInvariant() switch
        {
            "scale_up" or "resize" => new[]
            {
                "Identify original VM size from change history",
                "Stop the virtual machine",
                "Resize VM back to original SKU",
                "Start the virtual machine and verify it is healthy"
            },
            "enable_zone_redundancy" => new[]
            {
                "Backup all data from current storage account",
                "Revert storage account SKU to original (LRS or GRS)",
                "Restore data from backup",
                "Verify data integrity and application connectivity"
            },
            "enable_https" or "enforce_tls" => new[]
            {
                "Disable HTTPS-only requirement",
                "Restore original TLS version setting",
                "Test HTTP access from known clients"
            },
            "disable_public_access" => new[]
            {
                "Re-enable public network access on the resource",
                "Restore original network ACLs and allowed IP ranges",
                "Test connectivity from expected network sources"
            },
            _ => new[]
            {
                "Review change set diff in version control",
                "Revert IaC changes to previous commit",
                "Re-deploy previous configuration with validation",
                "Verify application functionality and health checks"
            }
        };

    private static string ResolveFormat(string? preferred, Recommendation recommendation)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var lower = preferred.Trim().ToLowerInvariant();
            if (lower is "bicep" or "terraform") return lower;
        }

        // Default to bicep for Azure resources, terraform for everything else
        return recommendation.ResourceId?.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) == true
            ? "bicep"
            : "terraform";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Bicep generation
    // ─────────────────────────────────────────────────────────────────────────

    private static string GenerateBicep(Recommendation recommendation)
    {
        var resourceName = recommendation.ResourceId?.Split('/').LastOrDefault() ?? "atlasResource";
        var sb = new StringBuilder();

        sb.AppendLine("// Generated by Atlas - Autonomous Cloud Evolution Engine");
        sb.AppendLine($"// Recommendation: {recommendation.RecommendationType}");
        sb.AppendLine($"// Action:         {recommendation.ActionType}");
        sb.AppendLine($"// Resource:       {recommendation.ResourceId}");
        sb.AppendLine($"// Generated:      {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("@description('Deployment location')");
        sb.AppendLine("param location string = resourceGroup().location");
        sb.AppendLine();

        switch (recommendation.ActionType?.ToLowerInvariant())
        {
            case "scale_up":
            case "resize":
                sb.AppendLine($"// Recommendation: {recommendation.ProposedChanges}");
                sb.AppendLine($"@description('Target VM size after resize (e.g. Standard_D4s_v5). See https://learn.microsoft.com/azure/virtual-machines/sizes for available sizes.')");
                sb.AppendLine($"param newVmSize string");
                sb.AppendLine();
                sb.AppendLine($"resource targetResource 'Microsoft.Compute/virtualMachines@2024-07-01' existing = {{");
                sb.AppendLine($"  name: '{resourceName}'");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"// CAUTION: Resizing requires the VM to be deallocated.");
                sb.AppendLine($"// This template updates the hardware profile; ensure the VM is stopped first.");
                sb.AppendLine($"resource resizedVm 'Microsoft.Compute/virtualMachines@2024-07-01' = {{");
                sb.AppendLine($"  name: '{resourceName}'");
                sb.AppendLine("  location: location");
                sb.AppendLine("  properties: {");
                sb.AppendLine("    hardwareProfile: {");
                sb.AppendLine("      vmSize: newVmSize");
                sb.AppendLine("    }");
                sb.AppendLine("  }");
                sb.AppendLine("}");
                break;

            case "enable_zone_redundancy":
                sb.AppendLine($"// Migrate resource to Zone-Redundant SKU");
                sb.AppendLine($"// NOTE: This may require data migration. Test in a non-production environment first.");
                sb.AppendLine($"resource zoneRedundantResource 'Microsoft.Storage/storageAccounts@2023-05-01' = {{");
                sb.AppendLine($"  name: '{resourceName}'");
                sb.AppendLine("  location: location");
                sb.AppendLine("  sku: {");
                sb.AppendLine("    name: 'Standard_ZRS'");
                sb.AppendLine("  }");
                sb.AppendLine("  kind: 'StorageV2'");
                sb.AppendLine("  properties: {}");
                sb.AppendLine("}");
                break;

            case "enable_https":
            case "enforce_tls":
                sb.AppendLine($"resource webApp 'Microsoft.Web/sites@2023-12-01' existing = {{");
                sb.AppendLine($"  name: '{resourceName}'");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"resource httpsConfig 'Microsoft.Web/sites@2023-12-01' = {{");
                sb.AppendLine($"  name: '{resourceName}'");
                sb.AppendLine("  location: location");
                sb.AppendLine("  properties: {");
                sb.AppendLine("    httpsOnly: true");
                sb.AppendLine("    siteConfig: {");
                sb.AppendLine("      minTlsVersion: '1.2'");
                sb.AppendLine("    }");
                sb.AppendLine("  }");
                sb.AppendLine("}");
                break;

            case "disable_public_access":
                sb.AppendLine($"resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {{");
                sb.AppendLine($"  name: '{resourceName}'");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"resource networkRestriction 'Microsoft.Storage/storageAccounts@2023-05-01' = {{");
                sb.AppendLine($"  name: '{resourceName}'");
                sb.AppendLine("  location: location");
                sb.AppendLine("  properties: {");
                sb.AppendLine("    publicNetworkAccess: 'Disabled'");
                sb.AppendLine("    networkAcls: {");
                sb.AppendLine("      defaultAction: 'Deny'");
                sb.AppendLine("      bypass: 'AzureServices'");
                sb.AppendLine("    }");
                sb.AppendLine("  }");
                sb.AppendLine("}");
                break;

            default:
                sb.AppendLine($"// Atlas could not generate a specific template for action '{recommendation.ActionType}'.");
                sb.AppendLine("// Review the recommendation details and implement the required change manually,");
                sb.AppendLine("// then add this file to version control before merging.");
                sb.AppendLine();
                sb.AppendLine($"// Recommendation type: {recommendation.RecommendationType}");
                sb.AppendLine($"// Priority:            {recommendation.Priority}");
                sb.AppendLine($"// Confidence:          {recommendation.Confidence:P0}");
                break;
        }

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Terraform generation
    // ─────────────────────────────────────────────────────────────────────────

    private static string GenerateTerraform(Recommendation recommendation)
    {
        var resourceName = (recommendation.ResourceId?.Split('/').LastOrDefault() ?? "atlas_resource")
            .Replace('-', '_').ToLowerInvariant();

        var sb = new StringBuilder();
        sb.AppendLine("# Generated by Atlas - Autonomous Cloud Evolution Engine");
        sb.AppendLine($"# Recommendation: {recommendation.RecommendationType}");
        sb.AppendLine($"# Action:         {recommendation.ActionType}");
        sb.AppendLine($"# Resource:       {recommendation.ResourceId}");
        sb.AppendLine($"# Generated:      {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("terraform {");
        sb.AppendLine("  required_version = \">= 1.9.0\"");
        sb.AppendLine("  required_providers {");
        sb.AppendLine("    azurerm = {");
        sb.AppendLine("      source  = \"hashicorp/azurerm\"");
        sb.AppendLine("      version = \"~> 4.0\"");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();

        switch (recommendation.ActionType?.ToLowerInvariant())
        {
            case "scale_up":
            case "resize":
                sb.AppendLine("variable \"vm_size\" {");
                sb.AppendLine("  description = \"Target VM size after resize\"");
                sb.AppendLine("  type        = string");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"resource \"azurerm_virtual_machine\" \"{resourceName}\" {{");
                sb.AppendLine($"  name = \"{resourceName}\"");
                sb.AppendLine("  vm_size = var.vm_size");
                sb.AppendLine("  # ... remaining properties unchanged from current state");
                sb.AppendLine("}");
                break;

            default:
                sb.AppendLine($"# Atlas could not generate a specific template for action '{recommendation.ActionType}'.");
                sb.AppendLine("# Review the recommendation details and implement the required change manually.");
                break;
        }

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PR description
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildPrDescription(Recommendation recommendation) =>
        $"""
        ## Atlas Automated Change Set

        **Recommendation ID**: `{recommendation.Id}`
        **Type**: {recommendation.RecommendationType}
        **Action**: {recommendation.ActionType}
        **Priority**: {recommendation.Priority}
        **Confidence**: {recommendation.Confidence:P0}

        ### Rationale

        {recommendation.Rationale ?? "_No rationale provided_"}

        ### Impact

        {recommendation.EstimatedImpact ?? "_See recommendation details_"}

        ### Review Checklist

        - [ ] Template validated in a non-production environment
        - [ ] Rollback plan reviewed and documented
        - [ ] Required approvals obtained (see recommendation)
        - [ ] Change communicated to affected teams

        > _This PR was automatically generated by Atlas. Always review before merging._
        """;

    private async Task ValidateBicepContentAsync(
        string content,
        List<string> errors,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        // Basic syntax checks first
        if (!content.Contains("resource ", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Bicep template must declare at least one resource.");
        }

        if (!Regex.IsMatch(
                content,
                @"resource\s+\w+\s+'[^']+@\d{4}-\d{2}-\d{2}(-preview)?'",
                RegexOptions.IgnoreCase))
        {
            warnings.Add("Could not verify resource API version pinning in Bicep template.");
        }

        if (content.Contains("TODO", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Bicep template contains TODO markers that may require manual completion.");
        }

        ValidateBalancedBraces(content, '{', '}', errors, "Bicep");

        // Run az bicep build for compiler validation
        try
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"atlas-validate-{Guid.NewGuid()}.bicep");
            await File.WriteAllTextAsync(tempFile, content, cancellationToken);

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "az",
                        Arguments = $"bicep build --file \"{tempFile}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    errors.Add($"Bicep compilation failed: {stderr}");
                    _logger.LogWarning("Bicep validation failed: {Error}", stderr);
                }
                else
                {
                    _logger.LogInformation("Bicep template passed compiler validation");
                }
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);

                // Also clean up generated JSON file
                var jsonFile = Path.ChangeExtension(tempFile, ".json");
                if (File.Exists(jsonFile))
                    File.Delete(jsonFile);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not run az bicep build for validation: {ex.Message}. Ensure Azure CLI with Bicep extension is installed.");
            _logger.LogWarning(ex, "Failed to validate Bicep content with CLI");
        }
    }

    private async Task ValidateTerraformContentAsync(
        string content,
        List<string> errors,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        // Basic syntax checks first
        if (!content.Contains("terraform {", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Terraform template must include a terraform block.");
        }

        if (!content.Contains("required_providers", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Terraform template should declare required_providers for deterministic builds.");
        }

        if (!content.Contains("resource \"", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Terraform template must declare at least one resource block.");
        }

        if (content.Contains("TODO", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Terraform template contains TODO markers that may require manual completion.");
        }

        ValidateBalancedBraces(content, '{', '}', errors, "Terraform");

        // Run terraform validate for compiler validation
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"atlas-validate-{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, "main.tf");
            await File.WriteAllTextAsync(tempFile, content, cancellationToken);

            try
            {
                // Run terraform init first (required for validate)
                var initProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "terraform",
                        Arguments = "init -backend=false",
                        WorkingDirectory = tempDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                initProcess.Start();
                await initProcess.WaitForExitAsync(cancellationToken);

                if (initProcess.ExitCode != 0)
                {
                    var initErr = await initProcess.StandardError.ReadToEndAsync(cancellationToken);
                    warnings.Add($"Terraform init failed (validate skipped): {initErr}");
                }
                else
                {
                    // Run terraform validate
                    var validateProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "terraform",
                            Arguments = "validate -json",
                            WorkingDirectory = tempDir,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    validateProcess.Start();
                    var stdout = await validateProcess.StandardOutput.ReadToEndAsync(cancellationToken);
                    await validateProcess.WaitForExitAsync(cancellationToken);

                    if (validateProcess.ExitCode != 0)
                    {
                        try
                        {
                            var result = JsonDocument.Parse(stdout);
                            if (result.RootElement.TryGetProperty("diagnostics", out var diagnostics))
                            {
                                foreach (var diagnostic in diagnostics.EnumerateArray())
                                {
                                    var severity = diagnostic.GetProperty("severity").GetString();
                                    var summary = diagnostic.GetProperty("summary").GetString();
                                    var detail = diagnostic.TryGetProperty("detail", out var detailProp)
                                        ? detailProp.GetString()
                                        : null;

                                    var message = string.IsNullOrEmpty(detail) ? summary : $"{summary}: {detail}";

                                    if (severity == "error")
                                        errors.Add($"Terraform: {message}");
                                    else
                                        warnings.Add($"Terraform: {message}");
                                }
                            }
                        }
                        catch
                        {
                            errors.Add($"Terraform validation failed: {stdout}");
                        }

                        _logger.LogWarning("Terraform validation failed: {Output}", stdout);
                    }
                    else
                    {
                        _logger.LogInformation("Terraform template passed validation");
                    }
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not run terraform validate: {ex.Message}. Ensure Terraform CLI is installed.");
            _logger.LogWarning(ex, "Failed to validate Terraform content with CLI");
        }
    }

    private static void ValidateBalancedBraces(string content, char open, char close, List<string> errors, string format)
    {
        var openCount = content.Count(c => c == open);
        var closeCount = content.Count(c => c == close);

        if (openCount != closeCount)
        {
            errors.Add($"{format} template has unbalanced braces: {openCount} '{open}' and {closeCount} '{close}'.");
        }
    }
}

public sealed record IacPreflightValidationResult(
    bool Passed,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);
