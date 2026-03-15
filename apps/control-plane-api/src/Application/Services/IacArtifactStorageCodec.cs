using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Atlas.ControlPlane.Domain.Entities;

namespace Atlas.ControlPlane.Application.Services;

public static class IacArtifactStorageCodec
{
    public const string InlinePrefix = "nimbusiq-inline:";
    public const string GzipInlinePrefix = "nimbusiq-gzip-inline:";
    public const string DetachedPrefix = "nimbusiq-detached:";
    public const string DetachedMarkerPrefix = "<!-- NIMBUSIQ_ARTIFACT_GZIP_BASE64:";
    public const string DetachedMarkerSuffix = " -->";

    public static EncodedArtifact EncodeForStorage(
        string artifactContent,
        int maxArtifactUriLength,
        string prDescription)
    {
        var rawBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(artifactContent));
        var inlineUri = InlinePrefix + rawBase64;
        if (inlineUri.Length <= maxArtifactUriLength)
        {
            return new EncodedArtifact(inlineUri, prDescription, "inline");
        }

        var gzipBase64 = ToGzipBase64(artifactContent);
        var gzipUri = GzipInlinePrefix + gzipBase64;
        if (gzipUri.Length <= maxArtifactUriLength)
        {
            return new EncodedArtifact(gzipUri, prDescription, "gzip_inline");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artifactContent))).ToLowerInvariant();
        var detachedUri = DetachedPrefix + hash;
        var marker = DetachedMarkerPrefix + gzipBase64 + DetachedMarkerSuffix;
        var updatedPrDescription = prDescription + Environment.NewLine + Environment.NewLine + marker;

        return new EncodedArtifact(detachedUri, updatedPrDescription, "detached");
    }

    public static string? TryDecode(IacChangeSet changeSet, List<string>? errors = null)
    {
        var artifactUri = changeSet.ArtifactUri;

        if (artifactUri.StartsWith(InlinePrefix, StringComparison.Ordinal))
        {
            return DecodeBase64Payload(artifactUri[InlinePrefix.Length..], errors, isGzip: false);
        }

        if (artifactUri.StartsWith(GzipInlinePrefix, StringComparison.Ordinal))
        {
            return DecodeBase64Payload(artifactUri[GzipInlinePrefix.Length..], errors, isGzip: true);
        }

        if (artifactUri.StartsWith(DetachedPrefix, StringComparison.Ordinal))
        {
            var markerPayload = TryExtractDetachedPayload(changeSet.PrDescription);
            if (markerPayload is null)
            {
                errors?.Add("Detached artifact payload marker was not found in PrDescription.");
                return null;
            }

            return DecodeBase64Payload(markerPayload, errors, isGzip: true);
        }

        errors?.Add("ArtifactUri uses an unsupported format.");
        return null;
    }

    private static string? TryExtractDetachedPayload(string? prDescription)
    {
        if (string.IsNullOrWhiteSpace(prDescription))
        {
            return null;
        }

        var pattern = Regex.Escape(DetachedMarkerPrefix) + "(?<payload>[A-Za-z0-9+/=]+)" + Regex.Escape(DetachedMarkerSuffix);
        var match = Regex.Match(prDescription, pattern, RegexOptions.Singleline);
        return match.Success ? match.Groups["payload"].Value : null;
    }

    private static string? DecodeBase64Payload(string encodedPayload, List<string>? errors, bool isGzip)
    {
        if (string.IsNullOrWhiteSpace(encodedPayload))
        {
            errors?.Add("Artifact payload is empty.");
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(encodedPayload);
            return isGzip ? FromGzip(bytes) : Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            errors?.Add("Artifact payload is not valid Base64.");
            return null;
        }
        catch (InvalidDataException)
        {
            errors?.Add("Artifact payload is not valid GZip data.");
            return null;
        }
    }

    private static string ToGzipBase64(string content)
    {
        var inputBytes = Encoding.UTF8.GetBytes(content);
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(inputBytes, 0, inputBytes.Length);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    private static string FromGzip(byte[] compressedBytes)
    {
        using var input = new MemoryStream(compressedBytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}

public sealed record EncodedArtifact(string ArtifactUri, string PrDescription, string StorageMode);
