using Atlas.AgentOrchestrator.Integrations.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atlas.AgentOrchestrator.Tests.Unit.Integrations.Azure;

public class AzureAIFoundryClientTests
{
    [Fact]
    public void Constructor_WithValidOptions_InitializesSuccessfully()
    {
        // Arrange
        var options = Options.Create(new AzureAIFoundryOptions
        {
            // Azure.AI.Projects expects a connection string in the format:
            // <endpoint>;<subscription_id>;<resource_group_name>;<project_name>
            ProjectEndpoint = "https://test-project.eastus.api.azureml.ms;00000000-0000-0000-0000-000000000000;rg-test;proj-test"
        });
        var logger = NullLogger<AzureAIFoundryClient>.Instance;

        // Act
        var client = new AzureAIFoundryClient(options, logger);

        // Assert
        Assert.NotNull(client);
        Assert.NotNull(client.GetProjectClient());
    }

    [Fact]
    public void Constructor_WithPlainUrlEndpoint_InitializesSuccessfully()
    {
        // Arrange — plain URL (no semicolons), exercises the else-branch in the URL extraction logic
        var options = Options.Create(new AzureAIFoundryOptions
        {
            ProjectEndpoint = "https://test-project.eastus.api.azureml.ms"
        });
        var logger = NullLogger<AzureAIFoundryClient>.Instance;

        // Act
        var client = new AzureAIFoundryClient(options, logger);

        // Assert
        Assert.NotNull(client);
        Assert.NotNull(client.GetProjectClient());
    }

    [Fact]
    public void Constructor_WithoutProjectEndpoint_ThrowsInvalidOperationException()
    {
        // Arrange
        var options = Options.Create(new AzureAIFoundryOptions());
        var logger = NullLogger<AzureAIFoundryClient>.Instance;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new AzureAIFoundryClient(options, logger);
        });
    }

    [Fact]
    public async Task InvokePromptFlowAsync_WithoutHostedCapability_ReturnsUnconfigured()
    {
        // Arrange
        var options = Options.Create(new AzureAIFoundryOptions
        {
            ProjectEndpoint = "https://test-project.eastus.api.azureml.ms;00000000-0000-0000-0000-000000000000;rg-test;proj-test"
        });
        var logger = NullLogger<AzureAIFoundryClient>.Instance;
        var client = new AzureAIFoundryClient(options, logger);

        var inputs = new Dictionary<string, object>
        {
            ["prompt"] = "test prompt"
        };

        // Act
        var result = await client.InvokePromptFlowAsync("test-flow", inputs, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("unconfigured", result["status"]);
    }

    [Fact]
    public async Task CreateAgentAsync_WithoutHostedCapability_ReturnsUnconfiguredHandle()
    {
        // Arrange
        var options = Options.Create(new AzureAIFoundryOptions
        {
            ProjectEndpoint = "https://test-project.eastus.api.azureml.ms;00000000-0000-0000-0000-000000000000;rg-test;proj-test"
        });
        var logger = NullLogger<AzureAIFoundryClient>.Instance;
        var client = new AzureAIFoundryClient(options, logger);

        // Act
        var agentHandle = await client.CreateAgentAsync(
            "test-agent",
            "gpt-4",
            "You are a helpful assistant",
            CancellationToken.None);

        // Assert
        Assert.NotNull(agentHandle);
        Assert.Equal("test-agent", agentHandle.AgentName);
        Assert.Equal("gpt-4", agentHandle.ModelDeployment);
        Assert.NotEmpty(agentHandle.AgentId);
        Assert.Equal("unconfigured", agentHandle.Status);
    }

    [Fact]
    public async Task SendPromptAsync_WithoutModelDeployment_ReturnsFallbackMessage()
    {
        // Arrange
        var options = Options.Create(new AzureAIFoundryOptions
        {
            ProjectEndpoint = "https://test-project.eastus.api.azureml.ms;00000000-0000-0000-0000-000000000000;rg-test;proj-test"
        });
        var logger = NullLogger<AzureAIFoundryClient>.Instance;
        var client = new AzureAIFoundryClient(options, logger);

        // Act
        var result = await client.SendPromptAsync("optimize this service group", CancellationToken.None);

        // Assert
        Assert.Contains("not configured", result, StringComparison.OrdinalIgnoreCase);
    }
}
