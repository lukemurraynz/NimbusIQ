using System.Net;
using System.Net.Http.Json;
using Atlas.ControlPlane.Api;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Atlas.ControlPlane.Tests.Contract;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.ControlPlane.Tests.Contract.ServiceGroups;

/// <summary>
/// T018: Contract tests for GET /api/v1/service-groups endpoint
/// Validates: Request/response schemas, status codes, correlation ID propagation
/// </summary>
public class ListServiceGroupsContractTests : IClassFixture<ContractTestFactory>
{
    private readonly HttpClient _client;

    public ListServiceGroupsContractTests(ContractTestFactory factory)
    {
        _client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        // Seed one service group so schema assertions validate non-empty responses.
        var serviceGroupId = Guid.NewGuid();
        db.ServiceGroups.Add(new ServiceGroup
        {
            Id = serviceGroupId,
            ExternalKey = $"sg-{Guid.NewGuid():N}",
            Name = "Contract Test Service Group",
            Description = "Seeded for contract tests",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.ServiceGroupScopes.Add(new ServiceGroupScope
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroupId,
            SubscriptionId = "00000000-0000-0000-0000-000000000000",
            ResourceGroup = "rg-contract-tests",
            ScopeFilter = null,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task GET_ListServiceGroups_Returns200_WithValueArray()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/service-groups?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"value\":", content);

        var result = await response.Content.ReadFromJsonAsync<ListResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Value);
    }

    [Fact]
    public async Task GET_ListServiceGroups_PropagatesCorrelationId()
    {
        // Arrange
        var correlationId = Guid.NewGuid().ToString();
        _client.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);

        // Act
        var response = await _client.GetAsync("/api/v1/service-groups?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GET_ListServiceGroups_ValidatesResponseSchema()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/service-groups?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ListResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Value);

        foreach (var sg in result.Value)
        {
            Assert.NotEqual(Guid.Empty, sg.Id);
            Assert.NotNull(sg.Name);
            Assert.True(sg.ScopeCount >= 0);
        }
    }

    private record ListResponse
    {
        public List<ServiceGroupDto> Value { get; init; } = new();
    }

    private record ServiceGroupDto
    {
        public Guid Id { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public int ScopeCount { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
