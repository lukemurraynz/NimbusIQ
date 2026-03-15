using Atlas.AgentOrchestrator.Integrations.MCP;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Atlas.AgentOrchestrator.Tests.Unit.Integrations.MCP;

public class LearnMcpClientTests
{
  [Fact]
  public void LearnMcpOptions_DefaultValues_AreReasonable()
  {
    var options = new LearnMcpOptions();

    Assert.NotNull(options.ServerUrl);
    Assert.False(string.IsNullOrWhiteSpace(options.ServerUrl),
        "ServerUrl must have a non-empty default value");
    Assert.True(options.Enabled,
        "Enabled should default to true so the client is active out of the box");
    Assert.Equal(5, options.MaxResults);
    Assert.Equal(24, options.CacheTtlHours);
  }

  [Fact]
  public void LearnMcpOptions_SectionName_IsNonEmpty()
  {
    Assert.False(string.IsNullOrWhiteSpace(LearnMcpOptions.SectionName),
        "SectionName must be non-empty so config binding works");
  }

  [Fact]
  public void LearnDocResult_DefaultValues_AreEmpty()
  {
    var result = new LearnDocResult();

    Assert.Equal(string.Empty, result.Title);
    Assert.Equal(string.Empty, result.Url);
    Assert.Equal(string.Empty, result.Summary);
    Assert.Null(result.LastUpdated);
    Assert.Empty(result.RelevantExcerpts);
    Assert.Null(result.Scope);
    Assert.Null(result.ResourceType);
  }

  [Fact]
  public async Task SearchDocsAsync_ToolFailure_ReturnsEmptyList()
  {
    var client = CreateClient(new FakeToolInvoker(throwOnCall: true));

    var results = await client.SearchDocsAsync("Azure VM best practices");

    Assert.NotNull(results);
    Assert.Empty(results);
  }

  [Fact]
  public async Task GetResourceGuidanceAsync_DelegatesToSearchDocs()
  {
    var client = CreateClient(new FakeToolInvoker(throwOnCall: true));

    var results = await client.GetResourceGuidanceAsync("Microsoft.Compute/virtualMachines", "Reliability");

    Assert.NotNull(results);
    Assert.Empty(results);
  }

  [Fact]
  public async Task GetArchitectureGuidanceAsync_DelegatesToSearchDocs()
  {
    var client = CreateClient(new FakeToolInvoker(throwOnCall: true));

    var results = await client.GetArchitectureGuidanceAsync("microservices on AKS");

    Assert.NotNull(results);
    Assert.Empty(results);
  }

  [Fact]
  public async Task VerifyReferenceAsync_ToolFailure_ReturnsNull()
  {
    var client = CreateClient(new FakeToolInvoker(throwOnCall: true));

    var result = await client.VerifyReferenceAsync("https://learn.microsoft.com/azure/some-doc");

    Assert.Null(result);
  }

  [Fact]
  public async Task SearchDocsAsync_CachedResult_ReturnsCachedData()
  {
    var callCount = 0;
    var invoker = new FakeToolInvoker(onCall: () =>
    {
      callCount++;
      return new CallToolResult
      {
        Content =
        [
          new TextContentBlock
          {
            Text = """[{"title":"Cached","url":"https://learn.microsoft.com/test","summary":"Test"}]"""
          }
        ]
      };
    });

    var client = CreateClient(invoker);

    var first = await client.SearchDocsAsync("cache test query");
    var second = await client.SearchDocsAsync("cache test query");

    Assert.Single(first);
    Assert.Single(second);
    Assert.Equal(1, callCount);
  }

  private static LearnMcpClient CreateClient(IMcpToolInvoker toolInvoker)
  {
    var options = Options.Create(new LearnMcpOptions
    {
      ServerUrl = "https://learn.test/mcp",
      Enabled = true,
      EnableToolDiscovery = false
    });

    return new LearnMcpClient(
        options,
        NullLogger<LearnMcpClient>.Instance,
        NullLoggerFactory.Instance,
        toolInvoker: toolInvoker);
  }

  private sealed class FakeToolInvoker : IMcpToolInvoker
  {
    private readonly Func<CallToolResult?>? _onCall;
    private readonly bool _throwOnCall;

    public FakeToolInvoker(bool throwOnCall = false, Func<CallToolResult?>? onCall = null)
    {
      _throwOnCall = throwOnCall;
      _onCall = onCall;
    }

    public Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult((IReadOnlyList<McpClientTool>)Array.Empty<McpClientTool>());

    public Task<CallToolResult?> CallToolAsync(
      string toolName,
      IReadOnlyDictionary<string, object?> arguments,
      CancellationToken cancellationToken = default)
    {
      if (_throwOnCall)
      {
        throw new InvalidOperationException("Simulated MCP failure");
      }

      return Task.FromResult(_onCall?.Invoke());
    }
  }
}
