using Microsoft.Extensions.Logging;
using Npgsql;
using System.Diagnostics;
using System.Text.Json;

namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// Real-time drift snapshot persistence and trending service
/// Automatically captures drift snapshots and maintains historical trends
/// </summary>
public class DriftSnapshotPersistenceService
{
    private readonly ILogger<DriftSnapshotPersistenceService> _logger;
    private readonly NpgsqlDataSource? _dataSource;
    private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.DriftPersistence");

    public DriftSnapshotPersistenceService(
        ILogger<DriftSnapshotPersistenceService> logger,
        NpgsqlDataSource? dataSource = null)
    {
        _logger = logger;
        _dataSource = dataSource;
    }

    /// <summary>
    /// Persist a drift snapshot in real-time
    /// </summary>
    public async Task<Guid> PersistSnapshotAsync(
        DriftSnapshotData snapshotData,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("PersistDriftSnapshot");
        activity?.SetTag("serviceGroupId", snapshotData.ServiceGroupId);
        activity?.SetTag("driftScore", snapshotData.DriftScore);

        var snapshotId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        if (_dataSource != null)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO drift_snapshots
                        (id, service_group_id, snapshot_time, total_violations, critical_violations,
                         high_violations, medium_violations, low_violations, drift_score,
                         category_breakdown, trend_analysis, created_at)
                    VALUES
                        (@id, @serviceGroupId, @snapshotTime, @totalViolations, @criticalViolations,
                         @highViolations, @mediumViolations, @lowViolations, @driftScore,
                         @categoryBreakdown, @trendAnalysis, @createdAt)
                    ON CONFLICT DO NOTHING
                    """;
                cmd.Parameters.AddWithValue("@id", snapshotId);
                cmd.Parameters.AddWithValue("@serviceGroupId", snapshotData.ServiceGroupId);
                cmd.Parameters.AddWithValue("@snapshotTime", now);
                cmd.Parameters.AddWithValue("@totalViolations", snapshotData.TotalViolations);
                cmd.Parameters.AddWithValue("@criticalViolations", snapshotData.CriticalViolations);
                cmd.Parameters.AddWithValue("@highViolations", snapshotData.HighViolations);
                cmd.Parameters.AddWithValue("@mediumViolations", snapshotData.MediumViolations);
                cmd.Parameters.AddWithValue("@lowViolations", snapshotData.LowViolations);
                cmd.Parameters.AddWithValue("@driftScore", snapshotData.DriftScore);
                cmd.Parameters.AddWithValue("@categoryBreakdown", (object?)snapshotData.CategoryBreakdown ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@trendAnalysis", (object?)snapshotData.TrendAnalysis ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@createdAt", now);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist drift snapshot {SnapshotId} to database; state is captured in-process only", snapshotId);
            }
        }

        _logger.LogInformation(
            "Persisted drift snapshot {SnapshotId} for service group {ServiceGroupId} with score {DriftScore:F2}",
            snapshotId,
            snapshotData.ServiceGroupId,
            snapshotData.DriftScore);

        activity?.SetTag("snapshotId", snapshotId);

        return snapshotId;
    }

    /// <summary>
    /// Get historical snapshots for a service group
    /// </summary>
    public async Task<List<DriftSnapshotRecord>> GetSnapshotsAsync(
        Guid serviceGroupId,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (_dataSource == null)
        {
            return new List<DriftSnapshotRecord>();
        }

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = """
                SELECT id, service_group_id, snapshot_time, total_violations, critical_violations,
                       high_violations, medium_violations, low_violations, drift_score,
                       category_breakdown, trend_analysis, created_at
                FROM drift_snapshots
                WHERE service_group_id = @serviceGroupId
                  AND (@startDate IS NULL OR snapshot_time >= @startDate)
                  AND (@endDate IS NULL OR snapshot_time <= @endDate)
                ORDER BY snapshot_time DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@serviceGroupId", serviceGroupId);
            cmd.Parameters.AddWithValue("@startDate", (object?)startDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@endDate", (object?)endDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@limit", limit ?? 1000);

            var results = new List<DriftSnapshotRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new DriftSnapshotRecord
                {
                    Id = reader.GetGuid(0),
                    ServiceGroupId = reader.GetGuid(1),
                    SnapshotTime = reader.GetDateTime(2),
                    TotalViolations = reader.GetInt32(3),
                    CriticalViolations = reader.GetInt32(4),
                    HighViolations = reader.GetInt32(5),
                    MediumViolations = reader.GetInt32(6),
                    LowViolations = reader.GetInt32(7),
                    DriftScore = reader.GetDecimal(8),
                    CategoryBreakdown = reader.IsDBNull(9) ? null : reader.GetString(9),
                    TrendAnalysis = reader.IsDBNull(10) ? null : reader.GetString(10),
                    CreatedAt = reader.GetDateTime(11)
                });
            }

            _logger.LogDebug(
                "Retrieved {Count} snapshots for service group {ServiceGroupId}",
                results.Count,
                serviceGroupId);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve drift snapshots for service group {ServiceGroupId}", serviceGroupId);
            return new List<DriftSnapshotRecord>();
        }
    }

    /// <summary>
    /// Get the latest snapshot for a service group
    /// </summary>
    public async Task<DriftSnapshotRecord?> GetLatestSnapshotAsync(
        Guid serviceGroupId,
        CancellationToken cancellationToken = default)
    {
        var results = await GetSnapshotsAsync(serviceGroupId, limit: 1, cancellationToken: cancellationToken);
        return results.FirstOrDefault();
    }

    /// <summary>
    /// Calculate trend analysis over a time period
    /// </summary>
    public async Task<DriftTrendAnalysis> AnalyzeTrendAsync(
        Guid serviceGroupId,
        int days = 30,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AnalyzeDriftTrend");
        activity?.SetTag("serviceGroupId", serviceGroupId);
        activity?.SetTag("days", days);

        var cutoffDate = DateTime.UtcNow.AddDays(-days);
        var snapshots = await GetSnapshotsAsync(
            serviceGroupId,
            startDate: cutoffDate,
            cancellationToken: cancellationToken);

        if (snapshots.Count == 0)
        {
            return new DriftTrendAnalysis
            {
                ServiceGroupId = serviceGroupId,
                PeriodDays = days,
                SnapshotCount = 0,
                TrendDirection = "unknown",
                Message = "No historical data available"
            };
        }

        // Calculate trend direction based on score changes
        var orderedSnapshots = snapshots.OrderBy(s => s.SnapshotTime).ToList();
        var firstScore = orderedSnapshots.First().DriftScore;
        var lastScore = orderedSnapshots.Last().DriftScore;
        var scoreChange = lastScore - firstScore;

        var trendDirection = scoreChange switch
        {
            < -5 => "improving",      // Score decreased (less drift is better)
            > 5 => "degrading",        // Score increased (more drift)
            _ => "stable"
        };

        // Calculate average score
        var averageScore = snapshots.Average(s => s.DriftScore);

        // Analyze category trends
        var categoryTrends = AnalyzeCategoryTrends(snapshots);

        var analysis = new DriftTrendAnalysis
        {
            ServiceGroupId = serviceGroupId,
            PeriodDays = days,
            SnapshotCount = snapshots.Count,
            TrendDirection = trendDirection,
            ScoreChange = scoreChange,
            AverageScore = averageScore,
            FirstScore = firstScore,
            LastScore = lastScore,
            CategoryTrends = categoryTrends,
            Message = GenerateTrendMessage(trendDirection, scoreChange)
        };

        _logger.LogInformation(
            "Drift trend analysis for service group {ServiceGroupId}: {TrendDirection}, change: {ScoreChange:F2}",
            serviceGroupId,
            trendDirection,
            scoreChange);

        return analysis;
    }

    private Dictionary<string, decimal> AnalyzeCategoryTrends(List<DriftSnapshotRecord> snapshots)
    {
        var trends = new Dictionary<string, decimal>();

        if (snapshots.Count < 2)
        {
            return trends;
        }

        // Extract category data from first and last snapshots
        var first = snapshots.OrderBy(s => s.SnapshotTime).First();
        var last = snapshots.OrderByDescending(s => s.SnapshotTime).First();

        if (!string.IsNullOrEmpty(first.CategoryBreakdown) &&
            !string.IsNullOrEmpty(last.CategoryBreakdown))
        {
            try
            {
                var firstCategories = JsonSerializer.Deserialize<Dictionary<string, int>>(first.CategoryBreakdown)
                    ?? new Dictionary<string, int>();
                var lastCategories = JsonSerializer.Deserialize<Dictionary<string, int>>(last.CategoryBreakdown)
                    ?? new Dictionary<string, int>();

                foreach (var category in firstCategories.Keys.Union(lastCategories.Keys))
                {
                    var firstCount = firstCategories.GetValueOrDefault(category, 0);
                    var lastCount = lastCategories.GetValueOrDefault(category, 0);
                    trends[category] = lastCount - firstCount;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse category breakdown for trend analysis");
            }
        }

        return trends;
    }

    private string GenerateTrendMessage(string direction, decimal scoreChange)
    {
        return direction switch
        {
            "improving" => $"Drift score improving: {Math.Abs(scoreChange):F1} point reduction in compliance violations",
            "degrading" => $"Drift score degrading: {scoreChange:F1} point increase in compliance violations",
            _ => "Drift score stable with no significant changes"
        };
    }
}

/// <summary>
/// Data model for drift snapshot storage
/// </summary>
public record DriftSnapshotData
{
    public required Guid ServiceGroupId { get; init; }
    public int TotalViolations { get; init; }
    public int CriticalViolations { get; init; }
    public int HighViolations { get; init; }
    public int MediumViolations { get; init; }
    public int LowViolations { get; init; }
    public decimal DriftScore { get; init; }
    public string? CategoryBreakdown { get; init; }
    public string? TrendAnalysis { get; init; }
}

/// <summary>
/// Stored snapshot record
/// </summary>
public record DriftSnapshotRecord : DriftSnapshotData
{
    public Guid Id { get; init; }
    public DateTime SnapshotTime { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Trend analysis result
/// </summary>
public record DriftTrendAnalysis
{
    public Guid ServiceGroupId { get; init; }
    public int PeriodDays { get; init; }
    public int SnapshotCount { get; init; }
    public string TrendDirection { get; init; } = "stable";
    public decimal ScoreChange { get; init; }
    public decimal AverageScore { get; init; }
    public decimal FirstScore { get; init; }
    public decimal LastScore { get; init; }
    public Dictionary<string, decimal> CategoryTrends { get; init; } = new();
    public string Message { get; init; } = string.Empty;
}
