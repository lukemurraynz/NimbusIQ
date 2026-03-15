using Atlas.ControlPlane.Infrastructure.Auth;
using Atlas.ControlPlane.Infrastructure.Azure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atlas.ControlPlane.Tests.Unit.Infrastructure.Azure;

public class AzureResourceGraphClientTests
{
    [Fact]
    public void Constructor_WithValidCredentialProvider_InitializesSuccessfully()
    {
        // Arrange
        var options = Options.Create(new ManagedIdentityOptions());
        var credentialProvider = new ManagedIdentityCredentialProvider(options, new TestHostEnvironment());
        var logger = NullLogger<AzureResourceGraphClient>.Instance;

        // Act
        var client = new AzureResourceGraphClient(credentialProvider, logger);

        // Assert
        Assert.NotNull(client);
    }

    [Fact(Skip = "Requires live Azure Resource Graph access; run as an integration test instead.")]
    public async Task QueryAsync_WithoutAzureCredentials_ThrowsException()
    {
        // Arrange
        var options = Options.Create(new ManagedIdentityOptions());
        var credentialProvider = new ManagedIdentityCredentialProvider(options, new TestHostEnvironment());
        var logger = NullLogger<AzureResourceGraphClient>.Instance;
        var client = new AzureResourceGraphClient(credentialProvider, logger);

        var query = "Resources | project name, type | limit 1";

        // Act & Assert
        // This test validates that the client fails appropriately without Azure credentials
        // In unit tests, we expect environment/credential failures (not a mock)
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await client.QueryAsync(query, null, CancellationToken.None);
        });
    }
}

public class AzureMonitorClientTests
{
    [Fact]
    public void Constructor_WithValidCredentialProvider_InitializesSuccessfully()
    {
        // Arrange
        var options = Options.Create(new ManagedIdentityOptions());
        var credentialProvider = new ManagedIdentityCredentialProvider(options, new TestHostEnvironment());
        var logger = NullLogger<AzureMonitorClient>.Instance;

        // Act
        var client = new AzureMonitorClient(credentialProvider, logger);

        // Assert
        Assert.NotNull(client);
    }
}

public class AzureCostManagementClientTests
{
    [Fact]
    public void Constructor_WithValidCredentialProvider_InitializesSuccessfully()
    {
        // Arrange
        var options = Options.Create(new ManagedIdentityOptions());
        var credentialProvider = new ManagedIdentityCredentialProvider(options, new TestHostEnvironment());
        var logger = NullLogger<AzureCostManagementClient>.Instance;
        var httpClientFactory = new NullHttpClientFactory();

        // Act
        var client = new AzureCostManagementClient(httpClientFactory, credentialProvider, logger);

        // Assert
        Assert.NotNull(client);
    }
}

/// <summary>
/// Minimal IHttpClientFactory that returns a plain HttpClient for unit tests.
/// </summary>
file sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

file sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "Atlas.ControlPlane.Tests";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Directory.GetCurrentDirectory());
}
