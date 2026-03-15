using System.Net;
using System.Net.Http.Json;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.ControlPlane.Tests.Contract;

/// <summary>
/// Contract tests validating API endpoint signatures and response shapes.
/// Ensures API contracts remain stable across changes.
/// </summary>
public class AnalysisContractsTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;
    private readonly HttpClient _client;

    public AnalysisContractsTests(ContractTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListServiceGroups_ReturnsExpectedContract()
    {
        SeedServiceGroup(serviceGroupId: Guid.NewGuid(), subscriptionId: "test-subscription-id");

        var response = await _client.GetAsync("/api/v1/service-groups");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var serviceGroups = await response.Content.ReadFromJsonAsync<ListServiceGroupsResponse>();
        Assert.NotNull(serviceGroups);
        Assert.NotNull(serviceGroups.Value);

        if (serviceGroups.Value.Count > 0)
        {
            var firstGroup = serviceGroups.Value[0];
            Assert.NotEqual(Guid.Empty, firstGroup.Id);
            Assert.NotNull(firstGroup.Name);
            Assert.NotEqual(default, firstGroup.CreatedAt);
        }
    }

    [Fact]
    public async Task ListServiceGroups_WithSubscriptionFilter_ReturnsExpectedContract()
    {
        var subscriptionId = "test-subscription-id";
        SeedServiceGroup(serviceGroupId: Guid.NewGuid(), subscriptionId: subscriptionId);

        var response = await _client.GetAsync($"/api/v1/service-groups?subscriptionId={subscriptionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var serviceGroups = await response.Content.ReadFromJsonAsync<ListServiceGroupsResponse>();
        Assert.NotNull(serviceGroups);
        Assert.NotNull(serviceGroups.Value);
    }

    [Fact]
    public async Task StartAnalysis_ReturnsExpectedContract()
    {
        var serviceGroupId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId: serviceGroupId, subscriptionId: "test-subscription-id");

        var response = await _client.PostAsJsonAsync("/api/v1/analysis/start", new { ServiceGroupId = serviceGroupId });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var analysisRun = await response.Content.ReadFromJsonAsync<AnalysisRunContractDto>();
        Assert.NotNull(analysisRun);
        Assert.NotEqual(Guid.Empty, analysisRun.Id);
        Assert.Equal(serviceGroupId, analysisRun.ServiceGroupId);
        Assert.NotNull(analysisRun.Status);
        Assert.NotNull(analysisRun.CorrelationId);
        Assert.NotEqual(default, analysisRun.InitiatedAt);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task GetAnalysisStatus_ReturnsExpectedContract()
    {
        var serviceGroupId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId: serviceGroupId, subscriptionId: "test-subscription-id");
        SeedAnalysisRun(serviceGroupId: serviceGroupId, analysisRunId: analysisId, status: "queued");

        var response = await _client.GetAsync($"/api/v1/analysis/{analysisId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var analysisRun = await response.Content.ReadFromJsonAsync<AnalysisRunDetailContractDto>();
        Assert.NotNull(analysisRun);
        Assert.NotEqual(Guid.Empty, analysisRun.Id);
        Assert.NotEqual(Guid.Empty, analysisRun.ServiceGroupId);
        Assert.NotNull(analysisRun.Status);
        Assert.NotNull(analysisRun.CorrelationId);
        Assert.NotNull(analysisRun.InitiatedBy);
        Assert.NotEqual(default, analysisRun.InitiatedAt);
        Assert.True(analysisRun.ResourcesDiscovered >= 0);
    }

    [Fact]
    public async Task GetAnalysisStatus_WithNonExistentId_Returns404WithConsistentErrorContract()
    {
        var nonExistentId = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/v1/analysis/{nonExistentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ProblemDetailsContractDto>();
        Assert.NotNull(error);
        Assert.Equal(404, error.Status);
        Assert.Equal("AnalysisRunNotFound", error.ErrorCode);
        Assert.Contains(nonExistentId.ToString(), error.Detail);
    }

    private void SeedServiceGroup(Guid serviceGroupId, string subscriptionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        if (db.ServiceGroups.Any(sg => sg.Id == serviceGroupId))
        {
            return;
        }

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
            SubscriptionId = subscriptionId,
            ResourceGroup = "rg-contract-tests",
            ScopeFilter = null,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private void SeedAnalysisRun(Guid serviceGroupId, Guid analysisRunId, string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        if (db.AnalysisRuns.Any(ar => ar.Id == analysisRunId))
        {
            return;
        }

        db.AnalysisRuns.Add(new AnalysisRun
        {
            Id = analysisRunId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            TriggeredBy = "contract-test-user",
            Status = status,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}

// Contract DTOs - these define the expected API response shapes
public record ListServiceGroupsResponse
{
    public List<ServiceGroupContractDto> Value { get; init; } = new();
}

public record ServiceGroupContractDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record AnalysisRunContractDto
{
    public Guid Id { get; init; }
    public Guid ServiceGroupId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public DateTime InitiatedAt { get; init; }
}

public record AnalysisRunDetailContractDto
{
    public Guid Id { get; init; }
    public Guid ServiceGroupId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public DateTime InitiatedAt { get; init; }
    public string InitiatedBy { get; init; } = string.Empty;
    public DateTime? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public int ResourcesDiscovered { get; init; }
    public decimal? ConfidenceScore { get; init; }
    public string? ConfidenceLevel { get; init; }
    public List<string> DegradationFactors { get; init; } = new();
}

public record ProblemDetailsContractDto
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public int? Status { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string? Instance { get; init; }
    public string? ErrorCode { get; init; }
    public string? TraceId { get; init; }
}
