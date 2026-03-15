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
/// T018: Contract tests for GET /api/v1/service-groups/{id}/analysis/{runId} endpoint
/// Validates: Status response, Retry-After header for non-terminal operations
/// </summary>
public class GetAnalysisStatusContractTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;
    private readonly HttpClient _client;

    public GetAnalysisStatusContractTests(ContractTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GET_AnalysisStatus_Returns200_WithStatusDetails()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        SeedAnalysisRun(serviceGroupId, runId, status: "queued");

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis/{runId}?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AnalysisStatusResponse>();
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.RunId);
        Assert.NotNull(result.Status);
        Assert.Contains(result.Status, new[] { "queued", "running", "completed", "partial", "failed", "cancelled" });
    }

    [Fact]
    public async Task GET_AnalysisStatus_ForRunningAnalysis_IncludesRetryAfter()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        SeedAnalysisRun(serviceGroupId, runId, status: "running");

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis/{runId}?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AnalysisStatusResponse>();
        Assert.NotNull(result);
        Assert.Equal("running", result.Status);

        Assert.True(response.Headers.Contains("Retry-After"),
            "Non-terminal LRO status responses must include Retry-After header");
    }

    [Fact]
    public async Task GET_AnalysisStatus_ForNonExistent_Returns404()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var nonExistentRunId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis/{nonExistentRunId}?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Verify RFC 9457 Problem Details
        var problemDetails = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>();
        Assert.NotNull(problemDetails);
        Assert.Equal(404, problemDetails.Status);
        Assert.NotNull(problemDetails.ErrorCode);

        // Verify x-error-code header
        Assert.True(response.Headers.TryGetValues("x-error-code", out var errorCodes));
        Assert.NotEmpty(errorCodes);
    }

    [Fact]
    public async Task GET_AnalysisStatus_ValidatesResponseSchema()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        SeedAnalysisRun(serviceGroupId, runId, status: "completed", completedAt: DateTime.UtcNow);

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis/{runId}?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AnalysisStatusResponse>();
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.RunId);
        Assert.NotNull(result.Status);
        Assert.NotNull(result.TriggeredBy);
        Assert.NotNull(result.CompletedAt);
    }

    private void SeedServiceGroup(Guid serviceGroupId)
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

        db.SaveChanges();
    }

    private void SeedAnalysisRun(Guid serviceGroupId, Guid runId, string status, DateTime? completedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        if (db.AnalysisRuns.Any(ar => ar.Id == runId))
        {
            return;
        }

        db.AnalysisRuns.Add(new AnalysisRun
        {
            Id = runId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            TriggeredBy = "contract-test-user",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            StartedAt = status is "running" or "completed" ? DateTime.UtcNow : null,
            CompletedAt = completedAt
        });

        db.SaveChanges();
    }

    private record AnalysisStatusResponse
    {
        public Guid RunId { get; init; }
        public Guid CorrelationId { get; init; }
        public required string Status { get; init; }
        public required string TriggeredBy { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
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
