using Atlas.ControlPlane.Application.Services;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.ControlPlane.Tests.Unit.Application.Services;

/// <summary>
/// Unit tests for <see cref="AnalysisOrchestrationService.ExecuteAsync"/>.
/// Covers the "FullWorkflow_CalculatesScores_PersistsAgentScores" scenario (T023-T025)
/// using a SQLite in-memory database (required because <c>ExecuteUpdateAsync</c> and
/// <c>ExecuteDeleteAsync</c> are not supported by the EF Core InMemory provider).
/// A single shared connection keeps the in-memory database alive across the test class lifetime.
/// No Azure connectivity is required — a null resource-graph client forces empty/partial discovery.
/// </summary>
public class AnalysisOrchestrationServiceTests : IDisposable
{
    // Keep the connection open so the SQLite in-memory database persists across contexts.
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AtlasDbContext> _dbOptions;
    private readonly Guid _serviceGroupId = Guid.NewGuid();

    public AnalysisOrchestrationServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Create the schema once per test class instance.
        using var db = new AtlasDbContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    // ── Full workflow ────────────────────────────────────────────────────────

    /// <summary>
    /// End-to-end assertion: the orchestration pipeline ingests an empty discovery result
    /// (no Azure connectivity required), writes four ScoreSnapshot rows, writes AgentMessage
    /// rows for every WAF category, and marks the analysis run as "partial" (because a null
    /// resource-graph client causes <see cref="AzureDiscoveryService"/> to return
    /// <c>IsPartial=true</c>).  "partial" is a terminal success state — the run completed,
    /// just with incomplete discovery.
    /// This is the unit-level coverage for the integration-test placeholder
    /// FullWorkflow_CalculatesScores_PersistsAgentScores (T023-T025).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithNullGraphClient_PersistsScoreSnapshotsAndCompletesRun()
    {
        // Arrange ─────────────────────────────────────────────────────────────
        var runId = await SeedQueuedRunAsync();

        var sut = BuildSut();

        // Act ─────────────────────────────────────────────────────────────────
        await sut.ExecuteAsync(runId);

        // Assert ──────────────────────────────────────────────────────────────
        await using var db = new AtlasDbContext(_dbOptions);

        // Run status must be a terminal state (not "failed" or "running").
        // A null graph client → IsPartial=true → status "partial" (terminal success with incomplete discovery).
        var run = await db.AnalysisRuns.FindAsync(runId);
        Assert.NotNull(run);
        Assert.Contains(run.Status, new[] { "completed", "partial" });
        Assert.NotNull(run.CompletedAt);

        // Five WAF categories must have ScoreSnapshot rows
        var snapshots = await db.ScoreSnapshots
            .Where(s => s.ServiceGroupId == _serviceGroupId)
            .ToListAsync();

        var expectedCategories = new[] { "Architecture", "FinOps", "Reliability", "Sustainability", "Security" };
        foreach (var cat in expectedCategories)
        {
            Assert.Contains(snapshots, s => s.Category == cat);
        }
        Assert.Equal(5, snapshots.Count(s => expectedCategories.Contains(s.Category)));

        // With an empty discovery result all four scores must be 0 (ScoreResult.Default)
        Assert.All(snapshots, s => Assert.Equal(0.0, s.Score));

        // Agent messages must have been persisted for each scoring category
        var messages = await db.AgentMessages
            .Where(m => m.AnalysisRunId == runId && m.MessageType == "score")
            .ToListAsync();

        Assert.Equal(5, messages.Count);
        foreach (var cat in expectedCategories)
        {
            Assert.Contains(messages, m => m.AgentName == cat);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithNullGraphClient_PersistsDiscoverySnapshotAsPartial()
    {
        var runId = await SeedQueuedRunAsync();
        var sut = BuildSut();

        await sut.ExecuteAsync(runId);

        await using var db = new AtlasDbContext(_dbOptions);
        var snapshot = await db.DiscoverySnapshots
            .FirstOrDefaultAsync(s => s.ServiceGroupId == _serviceGroupId);

        Assert.NotNull(snapshot);
        Assert.Equal("partial", snapshot.Status);  // null graph client → IsPartial=true
        Assert.Equal(0, snapshot.ResourceCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithNonQueuedRun_SkipsExecution()
    {
        var runId = await SeedRunWithStatusAsync("running");
        var sut = BuildSut();

        // Should return early — no exception and no status change
        await sut.ExecuteAsync(runId);

        await using var db = new AtlasDbContext(_dbOptions);
        var run = await db.AnalysisRuns.FindAsync(runId);
        Assert.NotNull(run);
        Assert.Equal("running", run.Status);  // unchanged

        // No score snapshots written
        var snapshotsCount = await db.ScoreSnapshots
            .CountAsync(s => s.ServiceGroupId == _serviceGroupId);
        Assert.Equal(0, snapshotsCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingRunId_SkipsGracefully()
    {
        var sut = BuildSut();

        // Should not throw when run doesn't exist
        await sut.ExecuteAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task ExecuteAsync_PersistsServiceGraphContextAgentMessage()
    {
        var runId = await SeedQueuedRunAsync();
        var sut = BuildSut();

        await sut.ExecuteAsync(runId);

        await using var db = new AtlasDbContext(_dbOptions);
        var graphMsg = await db.AgentMessages
            .FirstOrDefaultAsync(m =>
                m.AnalysisRunId == runId &&
                m.MessageType == "serviceGraphContext" &&
                m.AgentName == "ServiceGraphContextBuilder");

        Assert.NotNull(graphMsg);
        Assert.Equal("system", graphMsg.AgentRole);
    }

    // ── Delta tracking ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SecondRun_PopulatesDeltaFromPreviousOnScoreSnapshots()
    {
        // First run
        var run1Id = await SeedQueuedRunAsync(correlationId: Guid.NewGuid());
        await BuildSut().ExecuteAsync(run1Id);

        // Second run on the same service group
        var run2Id = await SeedQueuedRunAsync(correlationId: Guid.NewGuid());
        await BuildSut().ExecuteAsync(run2Id);

        await using var db = new AtlasDbContext(_dbOptions);
        var secondRunSnapshots = await db.ScoreSnapshots
            .Where(s => s.AnalysisRunId == run2Id)
            .ToListAsync();

        // All second-run snapshots must have DeltaFromPrevious populated
        Assert.All(secondRunSnapshots, s => Assert.NotNull(s.DeltaFromPrevious));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private AnalysisOrchestrationService BuildSut()
    {
        var factory = new SqliteDbContextFactory(_dbOptions);
        var discovery = new AzureDiscoveryService(
            resourceGraphClient: null,  // forces empty/partial discovery — no Azure connectivity
            NullLogger<AzureDiscoveryService>.Instance);
        var scoring = new ScoringService(NullLogger<ScoringService>.Instance);

        return new AnalysisOrchestrationService(
            factory,
            discovery,
            scoring,
            NullLogger<AnalysisOrchestrationService>.Instance);
    }

    private async Task<Guid> SeedQueuedRunAsync(Guid? correlationId = null)
        => await SeedRunWithStatusAsync("queued", correlationId);

    private async Task<Guid> SeedRunWithStatusAsync(string status, Guid? correlationId = null)
    {
        await using var db = new AtlasDbContext(_dbOptions);

        // Ensure the service group exists (idempotent – may already exist for second-run tests)
        if (!await db.ServiceGroups.AnyAsync(sg => sg.Id == _serviceGroupId))
        {
            db.ServiceGroups.Add(new ServiceGroup
            {
                Id = _serviceGroupId,
                ExternalKey = "unit-test-sg",
                Name = "Unit Test Service Group",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var runId = Guid.NewGuid();
        db.AnalysisRuns.Add(new AnalysisRun
        {
            Id = runId,
            ServiceGroupId = _serviceGroupId,
            CorrelationId = correlationId ?? Guid.NewGuid(),
            TriggeredBy = "unit-test",
            Status = status,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        return runId;
    }

    // ── IDbContextFactory<AtlasDbContext> adapter ────────────────────────────

    private sealed class SqliteDbContextFactory(DbContextOptions<AtlasDbContext> options)
        : IDbContextFactory<AtlasDbContext>
    {
        public AtlasDbContext CreateDbContext() => new AtlasDbContext(options);

        public Task<AtlasDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AtlasDbContext(options));
    }
}
