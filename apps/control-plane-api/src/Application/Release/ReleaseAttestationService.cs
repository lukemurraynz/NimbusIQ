using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Application.Release;

/// <summary>
/// T044: Release attestation writes (validation_mode=real_only, attestation_type=real_dependency_validation, mock_detection_result)
/// </summary>
public class ReleaseAttestationService
{
    private readonly AtlasDbContext _context;
    private readonly ILogger<ReleaseAttestationService> _logger;

    public ReleaseAttestationService(
        AtlasDbContext context,
        ILogger<ReleaseAttestationService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ReleaseAttestation> CreateAttestationAsync(
        Guid iacChangeSetId,
        string releaseId,
        string componentName,
        string componentVersion,
        bool mockDetected,
        string? mockDetectionDetails,
        string? validationScopeId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            throw new ArgumentException("Release ID is required", nameof(releaseId));
        }

        if (string.IsNullOrWhiteSpace(componentName))
        {
            throw new ArgumentException("Component name is required", nameof(componentName));
        }

        if (string.IsNullOrWhiteSpace(componentVersion))
        {
            throw new ArgumentException("Component version is required", nameof(componentVersion));
        }

        var changeSetExists = await _context.IacChangeSets
            .AsNoTracking()
            .AnyAsync(cs => cs.Id == iacChangeSetId, cancellationToken);

        if (!changeSetExists)
        {
            throw new KeyNotFoundException($"Change set {iacChangeSetId} not found");
        }

        _logger.LogInformation(
            "Creating release attestation for change set {ChangeSetId}, component {Component} v{Version} in release {ReleaseId}",
            iacChangeSetId,
            componentName,
            componentVersion,
            releaseId);

        var attestedAt = DateTime.UtcNow;

        var attestation = new ReleaseAttestation
        {
            Id = Guid.NewGuid(),
            IacChangeSetId = iacChangeSetId,
            ReleaseId = releaseId,
            ComponentName = componentName,
            ComponentVersion = componentVersion,
            AttestationType = "real_dependency_validation",
            ValidationMode = "real_only",
            MockDetectionResult = mockDetected ? "failed" : "passed",
            MockDetectionDetails = mockDetectionDetails,
            ValidationPassed = !mockDetected,
            PromotionBlockReason = mockDetected
                ? "Mock dependencies detected during release validation."
                : null,
            ValidationScopeId = validationScopeId,
            AttestedAt = attestedAt,
            AttestedBy = "atlas-system",
            CreatedAt = attestedAt
        };

        _context.ReleaseAttestations.Add(attestation);
        await _context.SaveChangesAsync(cancellationToken);

        if (mockDetected)
        {
            _logger.LogWarning(
                "Release attestation FAILED for {Component} v{Version}: Mock dependencies detected. Details: {Details}",
                componentName,
                componentVersion,
                mockDetectionDetails);
        }
        else
        {
            _logger.LogInformation(
                "Release attestation PASSED for {Component} v{Version}: No mock dependencies",
                componentName,
                componentVersion);
        }

        return attestation;
    }

    public async Task<bool> ValidateReleaseAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var attestations = await _context.ReleaseAttestations
            .Where(a => a.ReleaseId == releaseId)
            .ToListAsync(cancellationToken);

        if (!attestations.Any())
        {
            _logger.LogWarning("No attestations found for release {ReleaseId}", releaseId);
            return false;
        }

        var allPassed = attestations.All(a => a.ValidationPassed);

        if (!allPassed)
        {
            var failedComponents = attestations
                .Where(a => !a.ValidationPassed)
                .Select(a => a.ComponentName)
                .ToList();

            _logger.LogError(
                "Release {ReleaseId} validation FAILED. Components with mock dependencies: {FailedComponents}",
                releaseId,
                string.Join(", ", failedComponents));
        }
        else
        {
            _logger.LogInformation(
                "Release {ReleaseId} validation PASSED. All {Count} components validated successfully",
                releaseId,
                attestations.Count);
        }

        return allPassed;
    }
}
