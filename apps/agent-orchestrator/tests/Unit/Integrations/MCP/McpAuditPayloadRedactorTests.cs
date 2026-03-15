using Atlas.AgentOrchestrator.Integrations.MCP;

namespace Atlas.AgentOrchestrator.Tests.Unit.Integrations.MCP;

public class McpAuditPayloadRedactorTests
{
  [Fact]
  public void Sanitize_RedactsSensitiveKeys()
  {
    var payload = new Dictionary<string, object?>
    {
      ["authorization"] = "Bearer abc.def.ghi",
      ["api_key"] = "super-secret-key",
      ["normal"] = "safe-value"
    };

    var sanitized = McpAuditPayloadRedactor.Sanitize(payload) as Dictionary<string, object?>;

    Assert.NotNull(sanitized);
    Assert.Equal(McpAuditPayloadRedactor.RedactedValue, sanitized!["authorization"]);
    Assert.Equal(McpAuditPayloadRedactor.RedactedValue, sanitized["api_key"]);
    Assert.Equal("safe-value", sanitized["normal"]);
  }

  [Fact]
  public void Sanitize_TruncatesLongStrings_AndBoundsCollections()
  {
    var longText = new string('x', 1024);
    var items = Enumerable.Range(0, 40).Select(i => (object?)i).ToList();

    var payload = new Dictionary<string, object?>
    {
      ["notes"] = longText,
      ["items"] = items
    };

    var sanitized = McpAuditPayloadRedactor.Sanitize(payload) as Dictionary<string, object?>;

    Assert.NotNull(sanitized);
    Assert.Contains("...(truncated)", sanitized!["notes"]?.ToString());

    var sanitizedItems = Assert.IsType<List<object?>>(sanitized["items"]);
    Assert.True(sanitizedItems.Count <= 26);
    Assert.Equal(McpAuditPayloadRedactor.TruncatedItemsValue, sanitizedItems[^1]);
  }
}
