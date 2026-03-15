using Atlas.ControlPlane.Domain.Entities;

namespace Atlas.ControlPlane.Application.Recommendations;

public sealed record GroundedCitation(
    string Url,
    string Title,
    string SnippetHash,
    DateTime RetrievedAtUtc,
    string Source,
    string Query,
    string? ToolRunId = null,
    DateTime? SourceLastUpdatedUtc = null);

public sealed record GroundingProvenance(
    string GroundingSource,
    string GroundingQuery,
    DateTime GroundingTimestampUtc,
    string? GroundingToolRunId,
    double GroundingQuality,
    double GroundingRecencyScore);

public sealed record GroundingEnrichmentResult(
    IReadOnlyList<GroundedCitation> Citations,
    GroundingProvenance Provenance,
    IReadOnlyList<string> EvidenceUrls);

public interface IRecommendationGroundingClient
{
    Task<GroundingEnrichmentResult?> TryGroundAsync(
    Atlas.ControlPlane.Domain.Entities.Recommendation recommendation,
        CancellationToken cancellationToken = default);
}
