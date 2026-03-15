using System.Net;
using System.Net.Http.Json;
using Atlas.ControlPlane.Api;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Atlas.ControlPlane.Tests.Contract;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.ControlPlane.Tests.Contract.Drift;

/// <summary>
/// Contract tests for drift detection endpoints:
///   GET  /api/v1/drift/snapshots/{serviceGroupId}
///   GET  /api/v1/drift/trends/{serviceGroupId}
///   GET  /api/v1/drift/status/{serviceGroupId}
///   POST /api/v1/drift/snapshots
/// Validates: api-version enforcement, response schema, x-error-code header (RFC 9457)
/// </summary>
public class DriftContractTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;
    private readonly HttpClient _client;

    public DriftContractTests(ContractTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /drift/snapshots/{serviceGroupId}
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GET_DriftSnapshots_WithoutApiVersion_Returns400()
    {
        var id = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/v1/drift/snapshots/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>();
        Assert.NotNull(problem);
        Assert.Equal("MissingApiVersionParameter", problem.ErrorCode);

        Assert.True(response.Headers.TryGetValues("x-error-code", out var codes));
        Assert.Equal("MissingApiVersionParameter", codes.First());
    }

    [Fact]
    public async Task GET_DriftSnapshots_WithApiVersion_ReturnsEmptyListForUnknownGroup()
    {
        var id = Guid.NewGuid();
        var response = await _client.GetAsync(
            $"/api/v1/drift/snapshots/{id}?api-version=2025-02-16");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<DriftSnapshotResponse>>();
        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GET_DriftSnapshots_WithSeedData_ReturnsSnapshots()
    {
        var sgId = Guid.NewGuid();
        SeedServiceGroup(sgId);
        SeedDriftSnapshot(sgId);

        var response = await _client.GetAsync(
            $"/api/v1/drift/snapshots/{sgId}?api-version=2025-02-16");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<DriftSnapshotResponse>>();
        Assert.NotNull(list);
        Assert.NotEmpty(list);
        Assert.Equal(sgId, list[0].ServiceGroupId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /drift/trends/{serviceGroupId}
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GET_DriftTrends_WithoutApiVersion_Returns400()
    {
        var id = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/v1/drift/trends/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>();
        Assert.NotNull(problem);
        Assert.Equal("MissingApiVersionParameter", problem.ErrorCode);

        Assert.True(response.Headers.TryGetValues("x-error-code", out var codes));
        Assert.Equal("MissingApiVersionParameter", codes.First());
    }

    [Fact]
    public async Task GET_DriftTrends_WithApiVersion_ReturnsStableForEmptyData()
    {
        var id = Guid.NewGuid();
        var response = await _client.GetAsync(
            $"/api/v1/drift/trends/{id}?api-version=2025-02-16");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var trend = await response.Content.ReadFromJsonAsync<DriftTrendResponse>();
        Assert.NotNull(trend);
        Assert.Equal("stable", trend.TrendDirection);
        Assert.Empty(trend.Snapshots);
    }

    [Fact]
    public async Task GET_DriftTrends_WithSeedData_ReturnsTrendAnalysis()
    {
        var sgId = Guid.NewGuid();
        SeedServiceGroup(sgId);
        SeedDriftSnapshot(sgId, driftScore: 20m);
        SeedDriftSnapshot(sgId, driftScore: 30m, offsetDays: 1);

        var response = await _client.GetAsync(
            $"/api/v1/drift/trends/{sgId}?api-version=2025-02-16&days=30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var trend = await response.Content.ReadFromJsonAsync<DriftTrendResponse>();
        Assert.NotNull(trend);
        Assert.Equal(sgId, trend.ServiceGroupId);
        Assert.NotEmpty(trend.Snapshots);
        Assert.NotNull(trend.TrendDirection);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /drift/status/{serviceGroupId}
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GET_DriftStatus_WithoutApiVersion_Returns400()
    {
        var id = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/v1/drift/status/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>();
        Assert.NotNull(problem);
        Assert.Equal("MissingApiVersionParameter", problem.ErrorCode);

        Assert.True(response.Headers.TryGetValues("x-error-code", out var codes));
        Assert.Equal("MissingApiVersionParameter", codes.First());
    }

    [Fact]
    public async Task GET_DriftStatus_ForGroupWithNoSnapshots_Returns404()
    {
        var id = Guid.NewGuid();
        var response = await _client.GetAsync(
            $"/api/v1/drift/status/{id}?api-version=2025-02-16");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>();
        Assert.NotNull(problem);
        Assert.Equal("DriftSnapshotNotFound", problem.ErrorCode);

        Assert.True(response.Headers.TryGetValues("x-error-code", out var codes));
        Assert.Equal("DriftSnapshotNotFound", codes.First());
    }

    [Fact]
    public async Task GET_DriftStatus_WithSnapshot_ReturnsLatestSnapshot()
    {
        var sgId = Guid.NewGuid();
        SeedServiceGroup(sgId);
        SeedDriftSnapshot(sgId, driftScore: 15m);

        var response = await _client.GetAsync(
            $"/api/v1/drift/status/{sgId}?api-version=2025-02-16");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<DriftSnapshotResponse>();
        Assert.NotNull(snapshot);
        Assert.Equal(sgId, snapshot.ServiceGroupId);
        Assert.Equal(15m, snapshot.DriftScore);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // POST /drift/snapshots
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task POST_DriftSnapshot_WithoutApiVersion_Returns400()
    {
        var payload = new
        {
            serviceGroupId = Guid.NewGuid(),
            totalViolations = 5,
            criticalViolations = 1,
            highViolations = 2,
            mediumViolations = 1,
            lowViolations = 1,
            driftScore = 25.0m
        };

        var response = await _client.PostAsJsonAsync("/api/v1/drift/snapshots", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>();
        Assert.NotNull(problem);
        Assert.Equal("MissingApiVersionParameter", problem.ErrorCode);

        Assert.True(response.Headers.TryGetValues("x-error-code", out var codes));
        Assert.Equal("MissingApiVersionParameter", codes.First());
    }

    [Fact]
    public async Task POST_DriftSnapshot_WithApiVersion_Returns201()
    {
        var sgId = Guid.NewGuid();
        SeedServiceGroup(sgId);

        var payload = new
        {
            serviceGroupId = sgId,
            totalViolations = 5,
            criticalViolations = 1,
            highViolations = 2,
            mediumViolations = 1,
            lowViolations = 1,
            driftScore = 25.0m
        };

        var response = await _client.PostAsJsonAsync(
            "/api/v1/drift/snapshots?api-version=2025-02-16", payload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<DriftSnapshotResponse>();
        Assert.NotNull(created);
        Assert.Equal(sgId, created.ServiceGroupId);
        Assert.Equal(25.0m, created.DriftScore);
        Assert.Equal(5, created.TotalViolations);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void SeedServiceGroup(Guid serviceGroupId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        if (db.ServiceGroups.Any(sg => sg.Id == serviceGroupId))
            return;

        db.ServiceGroups.Add(new ServiceGroup
        {
            Id = serviceGroupId,
            ExternalKey = $"sg-{Guid.NewGuid():N}",
            Name = "Drift Contract Test Group",
            Description = "Seeded for drift contract tests",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    private void SeedDriftSnapshot(Guid serviceGroupId, decimal driftScore = 10m, int offsetDays = 0)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        db.DriftSnapshots.Add(new DriftSnapshot
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroupId,
            SnapshotTime = DateTime.UtcNow.AddDays(-offsetDays),
            TotalViolations = 3,
            CriticalViolations = 0,
            HighViolations = 1,
            MediumViolations = 1,
            LowViolations = 1,
            DriftScore = driftScore,
            CreatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Response DTOs
    // ─────────────────────────────────────────────────────────────────────────

    private record DriftSnapshotResponse
    {
        public Guid Id { get; init; }
        public Guid ServiceGroupId { get; init; }
        public DateTime SnapshotTime { get; init; }
        public int TotalViolations { get; init; }
        public int CriticalViolations { get; init; }
        public int HighViolations { get; init; }
        public int MediumViolations { get; init; }
        public int LowViolations { get; init; }
        public decimal DriftScore { get; init; }
        public string? CategoryBreakdown { get; init; }
        public string? TrendAnalysis { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private record DriftTrendResponse
    {
        public Guid ServiceGroupId { get; init; }
        public int PeriodDays { get; init; }
        public required List<DriftSnapshotResponse> Snapshots { get; init; }
        public required string TrendDirection { get; init; }
        public decimal AverageScore { get; init; }
        public decimal ScoreChange { get; init; }
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
