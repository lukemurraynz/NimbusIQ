using System.Text.Json;

namespace Atlas.ControlPlane.Tests.Unit.Application.Rules;

public class RulePackSchemaTests
{
    [Fact]
    public void SampleRulePack_ShouldFollowExpectedSchema()
    {
        var repoRoot = FindRepositoryRoot();
        var packPath = Path.Combine(
            repoRoot,
            "apps",
            "control-plane-api",
            "src",
            "Application",
            "Rules",
            "atlas-recommendation-rule-pack.v1.sample.json");

        Assert.True(File.Exists(packPath), $"Expected sample rule pack at {packPath}");

        var json = File.ReadAllText(packPath);
        var doc = JsonSerializer.Deserialize<RulePackDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(doc);
        Assert.False(string.IsNullOrWhiteSpace(doc!.Version));
        Assert.NotNull(doc.Rules);
        Assert.NotEmpty(doc.Rules!);

        var duplicateIds = doc.Rules!
            .Where(r => !string.IsNullOrWhiteSpace(r.RuleId))
            .GroupBy(r => r.RuleId!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicateIds);

        foreach (var rule in doc.Rules!)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.RuleId));
            Assert.False(string.IsNullOrWhiteSpace(rule.Source));
            Assert.False(string.IsNullOrWhiteSpace(rule.Category));
            Assert.False(string.IsNullOrWhiteSpace(rule.Pillar));
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));
            Assert.False(string.IsNullOrWhiteSpace(rule.Description));
            Assert.False(string.IsNullOrWhiteSpace(rule.Severity));
            Assert.NotNull(rule.ApplicabilityScope);
            Assert.NotEmpty(rule.ApplicabilityScope!);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var readmePath = Path.Combine(current.FullName, "README.md");
            var appsPath = Path.Combine(current.FullName, "apps");
            if (File.Exists(readmePath) && Directory.Exists(appsPath))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    private sealed record RulePackDocument(string? Version, List<RulePackRule>? Rules);

    private sealed record RulePackRule(
        string? RuleId,
        string? Source,
        string? Category,
        string? Pillar,
        string? Name,
        string? Description,
        string? Severity,
        string[]? ApplicabilityScope);
}
