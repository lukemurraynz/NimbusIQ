using Atlas.ControlPlane.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Atlas.ControlPlane.Tests.Integration.Discovery;

/// <summary>
/// T020: Integration test for telemetry gap handling
/// Tests: Discovery with missing/degraded telemetry, graceful degradation, confidence scores
/// </summary>
public class TelemetryGapHandlingTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _connectionString;

    public TelemetryGapHandlingTests(WebApplicationFactory<Program> factory)
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
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'DiscoverySnapshots'", conn);
        var tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;

        if (!tableExists)
        {
            throw new InvalidOperationException("Test database not initialized. Run migrations first.");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [RequiresEnvironmentVariableFact("ConnectionStrings__DefaultConnection")]
    public async Task Discovery_WithMissingTelemetry_CreatesPartialSnapshot()
    {
        // Arrange
        var serviceGroupId = await CreateTestServiceGroupAsync();

        // This test validates graceful degradation when telemetry is unavailable
        // Full implementation requires orchestrator (T023)

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Verify schema supports partial snapshots
        var cmd = new NpgsqlCommand(
            @"SELECT column_name, data_type 
              FROM information_schema.columns 
              WHERE table_name = 'DiscoverySnapshots' AND column_name = 'Status'",
            conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Status column must exist for tracking partial snapshots");

        // TODO: Full telemetry gap test when orchestrator is implemented (T023)
    }

    [Fact(Skip = "Requires orchestrator implementation (T023-T025): confidence score calculation with partial telemetry")]
    public async Task Discovery_WithDegradedTelemetry_ReflectsLowerConfidenceScores()
    {
        // TODO(T025): Enable after score calculation with confidence weighting is implemented
        await Task.CompletedTask;
        Assert.Fail("Not implemented: validate confidence scores degrade gracefully with incomplete telemetry");
    }

    [RequiresEnvironmentVariableFact("ConnectionStrings__DefaultConnection")]
    public async Task Discovery_ValidatesGracefulDegradationSchema()
    {
        // Verify database schema supports confidence tracking
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = new NpgsqlCommand(
            @"SELECT COUNT(*) FROM information_schema.columns 
              WHERE table_name = 'AgentMessages' AND column_name = 'Content'",
            conn);

        var columnExists = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
        Assert.True(columnExists, "AgentMessages.Content column required for score + confidence storage");
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
        cmd.Parameters.AddWithValue("Name", $"Test-SG-TelemetryGap-{serviceGroupId:N}");
        cmd.Parameters.AddWithValue("TenantId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("CreatedAt", DateTime.UtcNow);

        await cmd.ExecuteNonQueryAsync();

        return serviceGroupId;
    }
}
