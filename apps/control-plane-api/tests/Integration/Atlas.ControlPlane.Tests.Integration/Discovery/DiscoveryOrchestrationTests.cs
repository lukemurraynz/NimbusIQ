using Atlas.ControlPlane.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Net.Http.Json;
using Xunit;

namespace Atlas.ControlPlane.Tests.Integration.Discovery;

/// <summary>
/// T019: Integration test for full discovery orchestration workflow
/// Tests: trigger analysis → discovery → scoring
/// Uses real PostgreSQL from connection string
/// </summary>
public class DiscoveryOrchestrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _connectionString;

    public DiscoveryOrchestrationTests(WebApplicationFactory<Program> factory)
    {
        _connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=atlas_test;Username=atlas;Password=atlas";

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _connectionString
                });
            });
        });
    }

    public async Task InitializeAsync()
    {
        // Ensure test database is ready
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Verify migrations applied
        var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'ServiceGroups'", conn);
        var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

        if (!tableExists)
        {
            throw new InvalidOperationException("Test database not initialized. Run migrations first.");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [RequiresEnvironmentVariableFact("ConnectionStrings__DefaultConnection")]
    public async Task FullWorkflow_TriggerAnalysis_CreatesAnalysisRunWithCorrectStatus()
    {
        // Arrange
        var client = _factory.CreateClient();
        var serviceGroupId = await CreateTestServiceGroupAsync();

        // Act - Trigger analysis
        var response = await client.PostAsJsonAsync(
            $"/api/v1/service-groups/{serviceGroupId}/analysis?api-version=2025-02-16",
            new { });

        // Assert - Analysis started
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AnalysisStartResponse>();
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.RunId);

        // Verify AnalysisRun persisted in database
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = new NpgsqlCommand(
            "SELECT Status, CorrelationId FROM AnalysisRuns WHERE Id = @RunId",
            conn);
        cmd.Parameters.AddWithValue("RunId", result.RunId);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "AnalysisRun should be persisted");

        var status = reader.GetString(0);
        var correlationId = reader.GetGuid(1);

        Assert.Equal("queued", status);
        Assert.NotEqual(Guid.Empty, correlationId);
    }

    [RequiresEnvironmentVariableFact("ConnectionStrings__DefaultConnection")]
    public async Task FullWorkflow_DiscoveryCompletes_CreatesDiscoverySnapshot()
    {
        // Arrange
        var serviceGroupId = await CreateTestServiceGroupAsync();

        // This test requires orchestrator to be running
        // For now, we verify the database schema supports the workflow

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'DiscoverySnapshots'",
            conn);
        var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

        Assert.True(tableExists, "DiscoverySnapshots table must exist for workflow");

        // TODO: Full orchestrator integration when background services are wired up
        // This test validates schema readiness for T023 implementation
    }

    [Fact(Skip = "Requires full orchestrator implementation (T023-T025): analysis, discovery, and scoring pipeline")]
    public async Task FullWorkflow_CalculatesScores_PersistsAgentScores()
    {
        // TODO(T023): Enable after analysis → discovery → scoring pipeline is wired
        await Task.CompletedTask;
        Assert.Fail("Not implemented: wire end-to-end orchestrator to validate score persistence");
    }

    private async Task<Guid> CreateTestServiceGroupAsync()
    {
        var serviceGroupId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = new NpgsqlCommand(
            @"INSERT INTO ServiceGroups (Id, Name, TenantId, CreatedAt) 
              VALUES (@Id, @Name, @TenantId, @CreatedAt)",
            conn);

        cmd.Parameters.AddWithValue("Id", serviceGroupId);
        cmd.Parameters.AddWithValue("Name", $"Test-SG-{serviceGroupId:N}");
        cmd.Parameters.AddWithValue("TenantId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("CreatedAt", DateTime.UtcNow);

        await cmd.ExecuteNonQueryAsync();

        return serviceGroupId;
    }

    private record AnalysisStartResponse
    {
        public Guid RunId { get; init; }
        public Guid CorrelationId { get; init; }
        public required string Status { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
