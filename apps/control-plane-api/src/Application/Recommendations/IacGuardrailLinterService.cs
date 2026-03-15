using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Application.Services;

namespace Atlas.ControlPlane.Application.Recommendations;

public class IacGuardrailLinterService
{
    public GuardrailLintResult Lint(IacChangeSet changeSet)
    {
        var content = IacArtifactStorageCodec.TryDecode(changeSet);
        var findings = new List<GuardrailFinding>();

        if (string.IsNullOrWhiteSpace(content))
        {
            findings.Add(new GuardrailFinding(
                Id: "artifact.missing",
                Severity: "error",
                Message: "IaC artifact content is not available for linting.",
                Suggestion: "Ensure ArtifactUri contains inline content or retrievable URI."));

            return new GuardrailLintResult(false, findings);
        }

        var normalized = content.ToLowerInvariant();

        if (!normalized.Contains("tags"))
        {
            findings.Add(new GuardrailFinding(
                "metadata.tags",
                "warning",
                "No tags block detected in the template.",
                "Add standard ownership/cost-center tags for governance."));
        }

        if (normalized.Contains("publicnetworkaccess") && normalized.Contains("enabled"))
        {
            findings.Add(new GuardrailFinding(
                "network.public_access",
                "error",
                "Public network access appears to be enabled.",
                "Use private endpoints or explicitly justify internet exposure."));
        }

        if (normalized.Contains("microsoft.authorization/roleassignments"))
        {
            findings.Add(new GuardrailFinding(
                "identity.role_assignment",
                "warning",
                "Role assignment changes detected.",
                "Validate least-privilege scope and approver attestation."));
        }

        if ((changeSet.Format.Equals("bicep", StringComparison.OrdinalIgnoreCase) ||
             changeSet.Format.Equals("terraform", StringComparison.OrdinalIgnoreCase)) &&
            !normalized.Contains("rollback") &&
            !normalized.Contains("previous"))
        {
            findings.Add(new GuardrailFinding(
                "rollback.hint",
                "warning",
                "No rollback hint detected in change artifact.",
                "Document a rollback strategy in the PR description or template comments."));
        }

        var hasError = findings.Any(f => f.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
        return new GuardrailLintResult(!hasError, findings);
    }

}

public sealed record GuardrailLintResult(bool Passed, IReadOnlyList<GuardrailFinding> Findings);

public sealed record GuardrailFinding(string Id, string Severity, string Message, string Suggestion);
