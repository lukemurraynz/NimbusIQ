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
/// T018: Contract tests for POST /api/v1/service-groups/{id}/analysis endpoint
/// Validates: LRO pattern (202 Accepted), operation-location header, correlation ID
/// </summary>
public class StartAnalysisContractTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;
    private readonly HttpClient _client;

    public StartAnalysisContractTests(ContractTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task POST_StartAnalysis_Returns202Accepted_WithOperationLocation()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        var request = new { };

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis?api-version=2025-02-16",
            request);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Verify operation-location header (LRO pattern)
        Assert.True(response.Headers.Contains("operation-location"),
            "LRO must return operation-location header");

        var operationLocation = response.Headers.GetValues("operation-location").FirstOrDefault();
        Assert.NotNull(operationLocation);
        Assert.Contains("/analysis/", operationLocation);
        Assert.Contains("api-version=", operationLocation); // Must include api-version in poll URL
    }

    [Fact]
    public async Task POST_StartAnalysis_ReturnsAnalysisRunId()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        var request = new { };

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis?api-version=2025-02-16",
            request);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<StartAnalysisResponse>();
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.RunId);
        Assert.Equal("queued", result.Status);
        Assert.NotEqual(Guid.Empty, result.CorrelationId);
    }

    [Fact]
    public async Task POST_StartAnalysis_ForNonExistentServiceGroup_Returns404()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        var request = new { };

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/service-groups/{nonExistentId}/analysis?api-version=2025-02-16",
            request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Verify RFC 9457 Problem Details
        var problemDetails = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>();
        Assert.NotNull(problemDetails);
        Assert.Equal(404, problemDetails.Status);
        Assert.NotNull(problemDetails.ErrorCode);

        // Verify x-error-code header
        Assert.True(response.Headers.TryGetValues("x-error-code", out var errorCodes));
        Assert.Equal("ServiceGroupNotFound", errorCodes.First());
    }

    [Fact]
    public async Task POST_StartAnalysis_PropagatesCorrelationId()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        var correlationId = Guid.NewGuid().ToString();
        _client.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);
        var request = new { };

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis?api-version=2025-02-16",
            request);

        // Assert - Correlation ID should be returned in response
        var result = await response.Content.ReadFromJsonAsync<StartAnalysisResponse>();
        Assert.NotNull(result);
        Assert.Equal(correlationId, result.CorrelationId.ToString());
    }

    private void SeedServiceGroup(Guid serviceGroupId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        db.ServiceGroups.Add(new ServiceGroup
        {
            Id = serviceGroupId,
            ExternalKey = $"sg-{Guid.NewGuid():N}",
            Name = "Contract Test Service Group",
            Description = "Seeded for contract tests",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    private record StartAnalysisResponse
    {
        public Guid RunId { get; init; }
        public Guid CorrelationId { get; init; }
        public required string Status { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private record ProblemDetailsResponse
    {
        public string? Type { get; init; }
        public string? Title { get; init; }
        public int? Status { get; init; }
        public string? Detail { get; init; }
        public string? Instance { get; init; }
        public string? ErrorCode { get; init; }
        public string? TraceId { get; init; }
    }
}
