using System.Net;
using System.Net.Http.Json;
using Atlas.ControlPlane.Api;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Atlas.ControlPlane.Tests.Contract;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.ControlPlane.Tests.Contract.ServiceGroups;

/// <summary>
/// Contract tests for GET /api/v1/service-groups/{id}/violations
/// Validates: api-version enforcement, response schema, x-error-code header (RFC 9457)
/// </summary>
public class GetViolationsContractTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;
    private readonly HttpClient _client;

    public GetViolationsContractTests(ContractTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GET_Violations_WithoutApiVersion_Returns400()
    {
        var id = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/v1/service-groups/{id}/violations");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>();
        Assert.NotNull(problem);
        Assert.Equal("MissingApiVersionParameter", problem.ErrorCode);

        Assert.True(response.Headers.TryGetValues("x-error-code", out var codes));
        Assert.Equal("MissingApiVersionParameter", codes.First());
    }

    [Fact]
    public async Task GET_Violations_ForNonExistentGroup_Returns404()
    {
        var id = Guid.NewGuid();
        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{id}/violations?api-version=2025-02-16");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>();
        Assert.NotNull(problem);
        Assert.Equal("ServiceGroupNotFound", problem.ErrorCode);

        Assert.True(response.Headers.TryGetValues("x-error-code", out var codes));
        Assert.Equal("ServiceGroupNotFound", codes.First());
    }

    [Fact]
    public async Task GET_Violations_ForGroupWithNoViolations_ReturnsEmptyList()
    {
        var sgId = Guid.NewGuid();
        SeedServiceGroup(sgId);

        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{sgId}/violations?api-version=2025-02-16");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<List<ViolationResponse>>();
        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GET_Violations_WithSeedData_ReturnsViolations()
    {
        var sgId = Guid.NewGuid();
        SeedServiceGroup(sgId);
        SeedViolation(sgId, severity: "High", status: "active");

        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{sgId}/violations?api-version=2025-02-16");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<List<ViolationResponse>>();
        Assert.NotNull(list);
        Assert.NotEmpty(list);
        var v = list[0];
        Assert.Equal("High", v.Severity);
        Assert.Equal("active", v.Status);
    }

    [Fact]
    public async Task GET_Violations_SeverityFilter_ReturnsOnlyMatchingSeverity()
    {
        var sgId = Guid.NewGuid();
        SeedServiceGroup(sgId);
        SeedViolation(sgId, severity: "High", status: "active");
        SeedViolation(sgId, severity: "Low", status: "active");

        var response = await _client.GetAsync(
            $"/api/v1/service-groups/{sgId}/violations?api-version=2025-02-16&severity=High");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<List<ViolationResponse>>();
        Assert.NotNull(list);
        Assert.All(list, v => Assert.Equal("High", v.Severity));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void SeedServiceGroup(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        if (db.ServiceGroups.Any(sg => sg.Id == id))
            return;

        db.ServiceGroups.Add(new ServiceGroup
        {
            Id = id,
            ExternalKey = $"sg-{Guid.NewGuid():N}",
            Name = "Violations Contract Test Group",
            Description = "Seeded for violations contract tests",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    private void SeedViolation(Guid serviceGroupId, string severity = "Medium", string status = "active")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

        // Ensure a rule exists
        var ruleId = Guid.NewGuid();
        db.BestPracticeRules.Add(new BestPracticeRule
        {
            Id = ruleId,
            RuleId = $"TEST-{Guid.NewGuid():N}"[..12],
            Source = "Custom",
            Category = "Reliability",
            Pillar = "Reliability",
            Name = "Contract Test Rule",
            Description = "Rule seeded for contract tests",
            Severity = severity,
            ApplicabilityScope = "[]",
            EvaluationQuery = "N/A",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        db.BestPracticeViolations.Add(new BestPracticeViolation
        {
            Id = Guid.NewGuid(),
            RuleId = ruleId,
            ServiceGroupId = serviceGroupId,
            ResourceId = "/subscriptions/test/resourceGroups/rg/providers/test/resource",
            ResourceType = "Microsoft.Test/resource",
            ViolationType = "non_compliance",
            Severity = severity,
            CurrentState = "{}",
            ExpectedState = "{}",
            Status = status,
            DetectedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Response DTOs
    // ─────────────────────────────────────────────────────────────────────────

    private record ViolationResponse
    {
        public Guid Id { get; init; }
        public Guid RuleId { get; init; }
        public string? RuleName { get; init; }
        public string? Category { get; init; }
        public string? ResourceId { get; init; }
        public string? ResourceType { get; init; }
        public string? ViolationType { get; init; }
        public required string Severity { get; init; }
        public string? CurrentState { get; init; }
        public string? ExpectedState { get; init; }
        public required string Status { get; init; }
        public DateTime DetectedAt { get; init; }
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
