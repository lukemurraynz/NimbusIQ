using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Domain.Entities;

namespace Atlas.ControlPlane.Tests.Unit.Application.Services;

public class IacArtifactStorageCodecTests
{
    [Fact]
    public void EncodeForStorage_UsesInline_WhenPayloadFits()
    {
        var content = "resource test 'Microsoft.Storage/storageAccounts@2023-05-01' = {}";

        var encoded = IacArtifactStorageCodec.EncodeForStorage(content, maxArtifactUriLength: 4000, prDescription: "desc");

        Assert.StartsWith(IacArtifactStorageCodec.InlinePrefix, encoded.ArtifactUri, StringComparison.Ordinal);

        var changeSet = CreateChangeSet(encoded.ArtifactUri, encoded.PrDescription);
        var decoded = IacArtifactStorageCodec.TryDecode(changeSet);

        Assert.Equal(content, decoded);
    }

    [Fact]
    public void EncodeForStorage_UsesGzipInline_WhenInlineTooLong()
    {
        var content = string.Join("\n", Enumerable.Repeat("resource long 'Microsoft.Compute/virtualMachines@2024-07-01' = { name: 'vm' }", 200));

        var encoded = IacArtifactStorageCodec.EncodeForStorage(content, maxArtifactUriLength: 1200, prDescription: "desc");

        Assert.StartsWith(IacArtifactStorageCodec.GzipInlinePrefix, encoded.ArtifactUri, StringComparison.Ordinal);

        var changeSet = CreateChangeSet(encoded.ArtifactUri, encoded.PrDescription);
        var decoded = IacArtifactStorageCodec.TryDecode(changeSet);

        Assert.Equal(content, decoded);
    }

    [Fact]
    public void EncodeForStorage_UsesDetachedPayload_WhenUriLimitVerySmall()
    {
        var content = string.Join("\n", Enumerable.Repeat("resource detached 'Microsoft.Web/sites@2023-12-01' = { name: 'web' }", 200));

        var encoded = IacArtifactStorageCodec.EncodeForStorage(content, maxArtifactUriLength: 80, prDescription: "desc");

        Assert.StartsWith(IacArtifactStorageCodec.DetachedPrefix, encoded.ArtifactUri, StringComparison.Ordinal);
        Assert.Contains(IacArtifactStorageCodec.DetachedMarkerPrefix, encoded.PrDescription, StringComparison.Ordinal);

        var changeSet = CreateChangeSet(encoded.ArtifactUri, encoded.PrDescription);
        var decoded = IacArtifactStorageCodec.TryDecode(changeSet);

        Assert.Equal(content, decoded);
    }

    private static IacChangeSet CreateChangeSet(string artifactUri, string prDescription) => new()
    {
        Id = Guid.NewGuid(),
        RecommendationId = Guid.NewGuid(),
        Format = "bicep",
        ArtifactUri = artifactUri,
        PrTitle = "title",
        PrDescription = prDescription,
        Status = "generated",
        CreatedAt = DateTime.UtcNow
    };
}
