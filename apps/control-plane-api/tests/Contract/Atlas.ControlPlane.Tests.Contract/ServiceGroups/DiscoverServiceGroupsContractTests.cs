using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.ControlPlane.Api;
using Atlas.ControlPlane.Infrastructure.Auth;
using Atlas.ControlPlane.Infrastructure.Azure;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Atlas.ControlPlane.Tests.Contract.ServiceGroups;

// ---------------------------------------------------------------------------
// Stub — returns a deterministic two-node hierarchy (parent + child)
// ---------------------------------------------------------------------------

/// <summary>
/// Test stub that overrides Resource Graph discovery to return a fixed two-node hierarchy
/// without requiring any Azure connectivity.
/// </summary>
internal sealed class StubAzureResourceGraphClient : AzureResourceGraphClient
{
    private readonly IReadOnlyList<DiscoveredAzureServiceGroup> _fixedResults;
    private readonly Dictionary<string, HashSet<string>> _membershipByServiceGroup;

    public StubAzureResourceGraphClient(
        ManagedIdentityCredentialProvider credentialProvider,
        ILogger<AzureResourceGraphClient> logger,
        IReadOnlyList<DiscoveredAzureServiceGroup> fixedResults,
        Dictionary<string, HashSet<string>>? membershipByServiceGroup = null)
        : base(credentialProvider, logger)
    {
        _fixedResults = fixedResults;
        _membershipByServiceGroup = membershipByServiceGroup
            ?? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    }

    public override Task<IReadOnlyList<DiscoveredAzureServiceGroup>> DiscoverAzureServiceGroupsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(_fixedResults);

    public override Task<Dictionary<string, HashSet<string>>> DiscoverServiceGroupSubscriptionMembersAsync(
        IEnumerable<string> serviceGroupArmIds,
        CancellationToken cancellationToken = default)
    {
        var requested = serviceGroupArmIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = _membershipByServiceGroup
            .Where(kvp => requested.Contains(kvp.Key))
            .ToDictionary(
                kvp => kvp.Key,
                kvp => new HashSet<string>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(result);
    }
}

// ---------------------------------------------------------------------------
// Factory: 503 scenario – Resource Graph client not registered
// ---------------------------------------------------------------------------

/// <summary>
/// Factory variant where <see cref="AzureResourceGraphClient"/> is not registered,
/// so the controller receives null and returns 503.
/// </summary>
internal sealed class DiscoverWithoutGraphFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"atlas_discover_no_graph_{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            // In-memory DB
            services.RemoveAll<AtlasDbContext>();
            services.RemoveAll<DbContextOptions<AtlasDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<IDbContextOptionsConfiguration<AtlasDbContext>>();
            services.RemoveAll<IDbContextFactory<AtlasDbContext>>();
            services.AddDbContext<AtlasDbContext>(o => o.UseInMemoryDatabase(_databaseName));
            services.AddDbContextFactory<AtlasDbContext>(o => o.UseInMemoryDatabase(_databaseName), ServiceLifetime.Scoped);

            // Remove the Resource Graph client so the controller receives null → 503
            services.RemoveAll<AzureResourceGraphClient>();

            // Test auth
            services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }
}

// ---------------------------------------------------------------------------
// Factory: happy-path – stub returning a 2-node hierarchy
// ---------------------------------------------------------------------------

/// <summary>
/// Factory variant where <see cref="AzureResourceGraphClient"/> is replaced with a stub
/// returning a fixed parent + child Azure Service Group hierarchy.
/// </summary>
internal sealed class DiscoverWithStubGraphFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"atlas_discover_stub_{Guid.NewGuid():N}";

    /// <summary>The two fixed service groups returned by the stub.</summary>
    public static readonly DiscoveredAzureServiceGroup ParentGroup = new(
        ArmId: "/providers/Microsoft.Management/serviceGroups/sg-parent",
        Name: "sg-parent",
        DisplayName: "Parent Service Group",
        ParentArmId: null);

    public static readonly DiscoveredAzureServiceGroup ChildGroup = new(
        ArmId: "/providers/Microsoft.Management/serviceGroups/sg-child",
        Name: "sg-child",
        DisplayName: "Child Service Group",
        ParentArmId: "/providers/Microsoft.Management/serviceGroups/sg-parent");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            // In-memory DB
            services.RemoveAll<AtlasDbContext>();
            services.RemoveAll<DbContextOptions<AtlasDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<IDbContextOptionsConfiguration<AtlasDbContext>>();
            services.RemoveAll<IDbContextFactory<AtlasDbContext>>();
            services.AddDbContext<AtlasDbContext>(o => o.UseInMemoryDatabase(_databaseName));
            services.AddDbContextFactory<AtlasDbContext>(o => o.UseInMemoryDatabase(_databaseName), ServiceLifetime.Scoped);

            // Replace with stub
            services.RemoveAll<AzureResourceGraphClient>();
            services.AddSingleton<AzureResourceGraphClient>(sp =>
            {
                var credProvider = sp.GetRequiredService<ManagedIdentityCredentialProvider>();
                var logger = sp.GetRequiredService<ILogger<AzureResourceGraphClient>>();
                return new StubAzureResourceGraphClient(
                    credProvider,
                    logger,
                    new List<DiscoveredAzureServiceGroup> { ParentGroup, ChildGroup });
            });

            // Test auth
            services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }
}

internal sealed class DiscoverWithStubGraphAndMembershipFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"atlas_discover_stub_membership_{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AtlasDbContext>();
            services.RemoveAll<DbContextOptions<AtlasDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<IDbContextOptionsConfiguration<AtlasDbContext>>();
            services.RemoveAll<IDbContextFactory<AtlasDbContext>>();
            services.AddDbContext<AtlasDbContext>(o => o.UseInMemoryDatabase(_databaseName));
            services.AddDbContextFactory<AtlasDbContext>(o => o.UseInMemoryDatabase(_databaseName), ServiceLifetime.Scoped);

            services.RemoveAll<AzureResourceGraphClient>();
            services.AddSingleton<AzureResourceGraphClient>(sp =>
            {
                var credProvider = sp.GetRequiredService<ManagedIdentityCredentialProvider>();
                var logger = sp.GetRequiredService<ILogger<AzureResourceGraphClient>>();
                return new StubAzureResourceGraphClient(
                    credProvider,
                    logger,
                    new List<DiscoveredAzureServiceGroup>
                    {
                        DiscoverWithStubGraphFactory.ParentGroup,
                        DiscoverWithStubGraphFactory.ChildGroup
                    },
                    new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [DiscoverWithStubGraphFactory.ParentGroup.ArmId] =
                            new(StringComparer.OrdinalIgnoreCase) { "00000000-0000-0000-0000-000000000001" },
                        [DiscoverWithStubGraphFactory.ChildGroup.ArmId] =
                            new(StringComparer.OrdinalIgnoreCase) { "00000000-0000-0000-0000-000000000002" }
                    });
            });

            services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }
}

// ---------------------------------------------------------------------------
// Contract tests
// ---------------------------------------------------------------------------

/// <summary>
/// T019: Contract tests for POST /api/v1/service-groups/discover endpoint.
/// Validates: 503 when Resource Graph is not configured, 200 happy-path with hierarchy upsert.
/// </summary>
public class DiscoverServiceGroupsContractTests
{
    // -----------------------------------------------------------------------
    // 503 — Resource Graph client not configured
    // -----------------------------------------------------------------------

    [Fact]
    public async Task POST_Discover_Returns503_WhenResourceGraphClientNotConfigured()
    {
        // Arrange
        await using var factory = new DiscoverWithoutGraphFactory();
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsync(
            "/api/v1/service-groups/discover?api-version=2025-01-01",
            content: null);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // Verify RFC 9457 Problem Details shape
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("status", out var statusEl), "Problem Details must include 'status'");
        Assert.Equal(503, statusEl.GetInt32());

        // x-error-code header must match the errorCode extension in the body
        Assert.True(response.Headers.TryGetValues("x-error-code", out var codes));
        Assert.Equal("ResourceGraphUnavailable", codes.First());
    }

    // -----------------------------------------------------------------------
    // 200 — happy-path with stub returning a 2-node hierarchy
    // -----------------------------------------------------------------------

    [Fact]
    public async Task POST_Discover_Returns200_WithDiscoveredAndCreatedCounts()
    {
        // Arrange
        await using var factory = new DiscoverWithStubGraphFactory();
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsync(
            "/api/v1/service-groups/discover?api-version=2025-01-01",
            content: null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DiscoverResponse>();
        Assert.NotNull(result);
        Assert.Equal(2, result.Discovered);
        Assert.Equal(2, result.Created);
        Assert.Equal(0, result.Updated); // first run — nothing was updated
        Assert.Equal(2, result.Value.Count);
    }

    [Fact]
    public async Task POST_Discover_IdempotentSecondRun_ReturnsZeroCreatedAndUpdated()
    {
        // Arrange
        await using var factory = new DiscoverWithStubGraphFactory();
        var client = factory.CreateClient();

        // Act — first run to seed the DB, second run to verify idempotency
        await client.PostAsync("/api/v1/service-groups/discover?api-version=2025-01-01", content: null);
        var response = await client.PostAsync(
            "/api/v1/service-groups/discover?api-version=2025-01-01",
            content: null);

        // Assert — second run: 2 discovered, 0 created, 0 updated (nothing changed)
        var result = await response.Content.ReadFromJsonAsync<DiscoverResponse>();
        Assert.NotNull(result);
        Assert.Equal(2, result.Discovered);
        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Updated);

        var names = result.Value.Select(v => v.Name).ToHashSet();
        Assert.Contains("Parent Service Group", names);
        Assert.Contains("Child Service Group", names);
    }

    [Fact]
    public async Task POST_Discover_CreatesGroupsWithParentChildHierarchy()
    {
        // Arrange — first discovery creates both groups
        await using var factory = new DiscoverWithStubGraphFactory();
        var client = factory.CreateClient();
        await client.PostAsync("/api/v1/service-groups/discover?api-version=2025-01-01", content: null);

        // Verify both groups appear in the GET list endpoint, confirming hierarchy was established
        var listResp = await client.GetAsync("/api/v1/service-groups?api-version=2025-01-01");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listJson = await listResp.Content.ReadAsStringAsync();
        Assert.Contains("Parent Service Group", listJson);
        Assert.Contains("Child Service Group", listJson);
    }

    [Fact]
    public async Task POST_Discover_PopulatesScopeCounts_WhenMembershipDataExists()
    {
        await using var factory = new DiscoverWithStubGraphAndMembershipFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/v1/service-groups/discover?api-version=2025-01-01",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DiscoverResponse>();
        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Count);
        Assert.All(result.Value, sg => Assert.True(sg.ScopeCount > 0));
    }

    // Response DTOs (kept local to avoid coupling to application types)
    private sealed record DiscoverResponse(
        List<ServiceGroupItem> Value,
        int Discovered,
        int Created,
        int Updated);

    private sealed record ServiceGroupItem(
        Guid Id,
        string Name,
        string? Description,
        DateTime CreatedAt,
        int ScopeCount);
}
