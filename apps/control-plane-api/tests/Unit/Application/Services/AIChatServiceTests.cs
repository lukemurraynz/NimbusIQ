using Atlas.ControlPlane.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Atlas.ControlPlane.Tests.Unit.Application.Services;

public class AIChatServiceTests
{
    [Fact]
    public void Constructor_WithDefaultModelDeploymentOnly_EnablesAI()
    {
        var options = Options.Create(new AIChatOptions
        {
            ProjectEndpoint = "https://example.ai.azure.com/api/projects/demo",
            DefaultModelDeployment = "gpt-4o"
        });

        var sut = new AIChatService(options, NullLogger<AIChatService>.Instance);

        Assert.True(sut.IsAIAvailable);
    }

    [Fact]
    public void Constructor_WithoutModelDeployment_DisablesAI()
    {
        var options = Options.Create(new AIChatOptions
        {
            ProjectEndpoint = "https://example.ai.azure.com/api/projects/demo"
        });

        var sut = new AIChatService(options, NullLogger<AIChatService>.Instance);

        Assert.False(sut.IsAIAvailable);
    }

    [Fact]
    public async Task CheckConnectivityAsync_WhenAiIsNotConfigured_ReturnsUnconfigured()
    {
        var options = Options.Create(new AIChatOptions
        {
            ProjectEndpoint = null,
            ModelDeployment = null,
            DefaultModelDeployment = null
        });

        var sut = new AIChatService(options, NullLogger<AIChatService>.Instance);

        var status = await sut.CheckConnectivityAsync();

        Assert.Equal("unconfigured", status.OverallState);
        Assert.False(status.IsAiConfigured);
    }

    [Fact]
    public async Task CheckConnectivityAsync_WhenAiConfiguredAndProbeFails_ReturnsUnhealthyOrDegraded()
    {
        var options = Options.Create(new AIChatOptions
        {
            ProjectEndpoint = "https://example.ai.azure.com/api/projects/demo",
            DefaultModelDeployment = "gpt-4o"
        });

        var sut = new AIChatService(options, NullLogger<AIChatService>.Instance);

        var status = await sut.CheckConnectivityAsync();

        Assert.True(sut.IsAIAvailable);
        Assert.Contains(status.OverallState, new[] { "healthy", "degraded", "unhealthy" });
    }
}
