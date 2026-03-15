using System.Net;
using System.Net.Http.Json;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Atlas.ControlPlane.Tests.Contract;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.ControlPlane.Tests.Contract.ServiceGroups;

/// <summary>
/// Contract tests for GET /api/v1/service-groups/{id}/analysis/{runId}/scores
/// Validates:
///   - Non-terminal states (queued / running) → 202 Accepted + Retry-After header
///   - Completed run → 200 OK with typed <see cref="AnalysisScoresContractDto"/> body
///   - Malformed score payload → 200 OK (doesn't crash; scores default to 0)
/// </summary>
public class GetAnalysisScoresContractTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;
    private readonly HttpClient _client;

    public GetAnalysisScoresContractTests(ContractTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── Non-terminal: queued ──────────────────────────────────────────────

    [Fact]
    public async Task GET_AnalysisScores_ForQueuedRun_Returns202WithRetryAfter()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        SeedAnalysisRun(serviceGroupId, runId, status: "queued");

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis/{runId}/scores?api-version=2025-02-16");

        // Assert – LRO in-progress semantics
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"),
            "Non-terminal state must include Retry-After header per LRO pattern");

        // Body must be typed AnalysisScoresResponse (not an opaque string)
        var body = await response.Content.ReadFromJsonAsync<AnalysisScoresContractDto>();
        Assert.NotNull(body);
        Assert.Equal(runId, body.RunId);
        Assert.Equal(serviceGroupId, body.ServiceGroupId);
        Assert.Equal("queued", body.Status);
        Assert.NotNull(body.Scores);
        Assert.Empty(body.Scores);
    }

    [Fact]
    public async Task GET_AnalysisScores_ForRunningRun_Returns202WithRetryAfter()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        SeedAnalysisRun(serviceGroupId, runId, status: "running");

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis/{runId}/scores?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"),
            "Running analysis must include Retry-After header");

        var body = await response.Content.ReadFromJsonAsync<AnalysisScoresContractDto>();
        Assert.NotNull(body);
        Assert.Equal("running", body.Status);
    }

    // ── Completed: 200 with scores ────────────────────────────────────────

    [Fact]
    public async Task GET_AnalysisScores_ForCompletedRun_Returns200WithParsedScores()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        SeedAnalysisRun(serviceGroupId, runId, status: "completed");
        SeedScoreMessage(runId, payload: """
            {"completeness":0.8,"cost_efficiency":0.7,"availability":0.9,"security":0.5}
            """);

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis/{runId}/scores?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<AnalysisScoresContractDto>();
        Assert.NotNull(body);
        Assert.Equal(runId, body.RunId);
        Assert.Equal("completed", body.Status);
        Assert.NotNull(body.Scores);
        Assert.NotEmpty(body.Scores);

        var scoreDetail = body.Scores.First();
        Assert.InRange(scoreDetail.Score, 0, 100);
        Assert.InRange(scoreDetail.Confidence, 0.0, 1.0);
    }

    [Fact]
    public async Task GET_AnalysisScores_ForPartialRun_Returns200()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        SeedAnalysisRun(serviceGroupId, runId, status: "partial");

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis/{runId}/scores?api-version=2025-02-16");

        // Assert – "partial" is terminal and should return 200
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Malformed payload ─────────────────────────────────────────────────

    [Fact]
    public async Task GET_AnalysisScores_WithMalformedPayload_Returns200WithZeroScores()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);
        SeedAnalysisRun(serviceGroupId, runId, status: "completed");
        SeedScoreMessage(runId, payload: "this-is-not-valid-json!!");

        // Act – must not throw or return 5xx
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis/{runId}/scores?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AnalysisScoresContractDto>();
        Assert.NotNull(body);
        Assert.NotNull(body.Scores);

        // Malformed payload → score and confidence gracefully default to 0
        foreach (var score in body.Scores)
        {
            Assert.Equal(0, score.Score);
            Assert.Equal(0.0, score.Confidence);
        }
    }

    // ── Not found ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GET_AnalysisScores_ForNonExistentRun_Returns404()
    {
        // Arrange
        var serviceGroupId = Guid.NewGuid();
        SeedServiceGroup(serviceGroupId);

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis/{Guid.NewGuid()}/scores?api-version=2025-02-16");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ScoresProblemDetailsDto>();
        Assert.NotNull(problem);
        Assert.Equal(404, problem.Status);
        Assert.True(response.Headers.TryGetValues("x-error-code", out var codes));
        Assert.Equal("AnalysisRunNotFound", codes.First());
    }

    // ── Seeding helpers ───────────────────────────────────────────────────

    private void SeedServiceGroup(Guid serviceGroupId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        if (db.ServiceGroups.Any(sg => sg.Id == serviceGroupId)) return;

        db.ServiceGroups.Add(new ServiceGroup
        {
            Id = serviceGroupId,
            ExternalKey = $"sg-{serviceGroupId:N}",
            Name = "Scores Contract Test Group",
            Description = "Seeded for GetAnalysisScores contract tests",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private void SeedAnalysisRun(Guid serviceGroupId, Guid runId, string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        if (db.AnalysisRuns.Any(ar => ar.Id == runId)) return;

        db.AnalysisRuns.Add(new AnalysisRun
        {
            Id = runId,
            ServiceGroupId = serviceGroupId,
            CorrelationId = Guid.NewGuid(),
            TriggeredBy = "contract-test-user",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            StartedAt = status is "running" or "completed" or "partial" ? DateTime.UtcNow : null,
            CompletedAt = status is "completed" or "partial" ? DateTime.UtcNow : null
        });
        db.SaveChanges();
    }

    private void SeedScoreMessage(Guid runId, string payload)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        db.AgentMessages.Add(new AgentMessage
        {
            Id = Guid.NewGuid(),
            AnalysisRunId = runId,
            MessageId = Guid.NewGuid(),
            AgentName = "ScoringService",
            AgentRole = "executor",
            MessageType = "score",
            Payload = payload,
            Confidence = 0.75m,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    // ── Contract DTOs ─────────────────────────────────────────────────────

    private record AnalysisScoresContractDto
    {
        public Guid RunId { get; init; }
        public Guid ServiceGroupId { get; init; }
        public required string Status { get; init; }
        public DateTime? CompletedAt { get; init; }
        public List<ScoreDetailDto> Scores { get; init; } = new();
    }

    private record ScoreDetailDto
    {
        public required string Category { get; init; }
        public int Score { get; init; }
        public double Confidence { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private record ScoresProblemDetailsDto
    {
        public string? Type { get; init; }
        public string? Title { get; init; }
        public int? Status { get; init; }
        public string? Detail { get; init; }
        public string? ErrorCode { get; init; }
    }
}
