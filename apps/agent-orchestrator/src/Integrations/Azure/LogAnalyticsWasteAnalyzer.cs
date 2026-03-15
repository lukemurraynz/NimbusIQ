using Azure.Core;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Atlas.AgentOrchestrator.Integrations.Azure;

/// <summary>
/// Analyzes Log Analytics workspaces for waste patterns:
/// never-queried tables, excessive retention, verbose logging, and duplicate ingestion.
///
/// Cost basis: $2.30/GB ingestion, $0.10/GB/month retention beyond 31 days.
/// Potential savings: 20-80% of Log Analytics spend (industry data).
/// </summary>
public class LogAnalyticsWasteAnalyzer
{
    private readonly LogsQueryClient _logsClient;
    private readonly ILogger<LogAnalyticsWasteAnalyzer> _logger;
    private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.LogAnalyticsWaste");

    // Azure Monitor pricing (East US, as of 2025)
    private const decimal IngestionCostPerGb = 2.30m;
    private const decimal RetentionCostPerGbPerMonth = 0.10m;
    private const int FreeRetentionDays = 31;

    public LogAnalyticsWasteAnalyzer(
        TokenCredential credential,
        ILogger<LogAnalyticsWasteAnalyzer> logger)
    {
        _logsClient = new LogsQueryClient(credential);
        _logger = logger;
    }

    /// <summary>
    /// Run full waste analysis against a Log Analytics workspace.
    /// Returns a ranked list of cost-reduction opportunities with concrete remediation steps.
    /// </summary>
    public async Task<LogAnalyticsWasteReport> AnalyzeWorkspaceAsync(
        string workspaceId,
        int unusedQueryThresholdDays = 90,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AnalyzeWorkspaceWaste");
        activity?.SetTag("workspace.id", workspaceId);

        var report = new LogAnalyticsWasteReport
        {
            WorkspaceId = workspaceId,
            AnalyzedAt = DateTimeOffset.UtcNow
        };

        try
        {
            // Run analyses in parallel — each is an independent Log Analytics query
            var ingestionTask = GetTableIngestionVolumesAsync(workspaceId, cancellationToken);
            var queryHistoryTask = GetTableQueryHistoryAsync(workspaceId, unusedQueryThresholdDays, cancellationToken);
            var retentionTask = GetWorkspaceRetentionCostsAsync(workspaceId, cancellationToken);

            await Task.WhenAll(ingestionTask, queryHistoryTask, retentionTask);

            var ingestion = await ingestionTask;
            var queryHistory = await queryHistoryTask;
            var retention = await retentionTask;

            // Correlate ingestion volumes with query history
            report.UnusedTables = CorrelateUnusedTables(ingestion, queryHistory);

            // Tables with retention beyond what compliance typically requires
            report.ExcessiveRetentionTables = retention
                .Where(r => r.MonthlySavings > 0)
                .OrderByDescending(r => r.MonthlySavings)
                .ToList();

            // Summarize costs
            report.UnusedIngestionCost = report.UnusedTables.Sum(t => t.EstimatedMonthlyCost);
            report.ExcessRetentionCost = report.ExcessiveRetentionTables.Sum(t => t.MonthlySavings);
            report.TotalMonthlyWaste = report.UnusedIngestionCost + report.ExcessRetentionCost;

            _logger.LogInformation(
                "Log Analytics waste analysis complete for {WorkspaceId}: {UnusedTableCount} unused tables, " +
                "${UnusedIngestionCost:F2}/month ingestion waste, ${RetentionCost:F2}/month retention waste",
                workspaceId,
                report.UnusedTables.Count,
                report.UnusedIngestionCost,
                report.ExcessRetentionCost);

            activity?.SetTag("waste.totalMonthly", report.TotalMonthlyWaste);
            activity?.SetTag("waste.unusedTables", report.UnusedTables.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Log Analytics waste analysis failed for workspace {WorkspaceId}", workspaceId);
            report.Message = $"Analysis failed: {ex.Message}";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }

        return report;
    }

    /// <summary>
    /// Query the Usage table for ingestion volumes per data type over the last 90 days.
    /// </summary>
    private async Task<List<TableIngestionInfo>> GetTableIngestionVolumesAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        const string kql = """
            Usage
            | where TimeGenerated > ago(90d)
            | summarize TotalGB = sum(Quantity) / 1000.0,
                        DailyAvgGB = avg(Quantity) / 1000.0,
                        DataPoints = count()
              by DataType
            | extend MonthlyCostEstimate = DailyAvgGB * 30 * 2.30
            | order by TotalGB desc
            """;

        var result = await ExecuteKqlAsync(workspaceId, kql, TimeSpan.FromDays(90), cancellationToken);
        var tables = new List<TableIngestionInfo>();

        if (result is null) return tables;

        foreach (var row in result.Table.Rows)
        {
            tables.Add(new TableIngestionInfo
            {
                TableName = row["DataType"]?.ToString() ?? "Unknown",
                TotalIngestedGB = Convert.ToDecimal(row["TotalGB"] ?? 0),
                DailyAverageGB = Convert.ToDecimal(row["DailyAvgGB"] ?? 0),
                EstimatedMonthlyCost = Convert.ToDecimal(row["MonthlyCostEstimate"] ?? 0)
            });
        }

        return tables;
    }

    /// <summary>
    /// Query LAQueryLogs to find which tables have actually been queried recently.
    /// Tables absent from LAQueryLogs for N days are candidates for disabling.
    /// </summary>
    private async Task<Dictionary<string, DateTimeOffset>> GetTableQueryHistoryAsync(
        string workspaceId,
        int thresholdDays,
        CancellationToken cancellationToken)
    {
        var kql = $"""
            LAQueryLogs
            | where TimeGenerated > ago({thresholdDays}d)
            | extend QueryText = tostring(RequestContext["xms_armresourceid"])
            | extend TableNames = extract_all(@'(\b[A-Z][A-Za-z0-9_]+)\b\s*\|', QueryText)
            | mv-expand TableName = TableNames to typeof(string)
            | where isnotempty(TableName)
            | summarize LastQueried = max(TimeGenerated) by TableName
            """;

        var result = await ExecuteKqlAsync(workspaceId, kql, TimeSpan.FromDays(thresholdDays), cancellationToken);
        var history = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        if (result is null) return history;

        foreach (var row in result.Table.Rows)
        {
            var tableName = row["TableName"]?.ToString();
            if (string.IsNullOrEmpty(tableName)) continue;

            if (DateTimeOffset.TryParse(row["LastQueried"]?.ToString(), out var lastQueried))
            {
                history[tableName] = lastQueried;
            }
        }

        return history;
    }

    /// <summary>
    /// Estimate retention excess costs by analyzing table sizes and comparing against
    /// standard compliance retention requirements (31 days free, then $0.10/GB/month).
    /// </summary>
    private async Task<List<ExcessiveRetentionTable>> GetWorkspaceRetentionCostsAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        // Get data volume per table to estimate retention costs at default 90-day workspace retention
        const string kql = """
            Usage
            | where TimeGenerated > ago(90d)
            | summarize MonthlyGB = avg(Quantity) / 1000.0 by DataType
            | extend DefaultRetentionDays = 90
            | extend ExcessRetentionDays = DefaultRetentionDays - 31
            | extend RetentionCostPerMonth = MonthlyGB * ExcessRetentionDays * 0.10
            | where RetentionCostPerMonth > 0
            | order by RetentionCostPerMonth desc
            """;

        var result = await ExecuteKqlAsync(workspaceId, kql, TimeSpan.FromDays(90), cancellationToken);
        var tables = new List<ExcessiveRetentionTable>();

        if (result is null) return tables;

        foreach (var row in result.Table.Rows)
        {
            var dataType = row["DataType"]?.ToString() ?? "Unknown";
            var monthlyGb = Convert.ToDecimal(row["MonthlyGB"] ?? 0);
            var monthlySavings = Convert.ToDecimal(row["RetentionCostPerMonth"] ?? 0);

            tables.Add(new ExcessiveRetentionTable
            {
                TableName = dataType,
                CurrentRetentionDays = 90,
                RecommendedRetentionDays = GetRecommendedRetentionDays(dataType),
                TotalStorageGB = monthlyGb * 3, // 90 days ≈ 3 months
                MonthlySavings = monthlySavings,
                Justification = GetRetentionJustification(dataType)
            });
        }

        return tables;
    }

    private List<UnusedLogTable> CorrelateUnusedTables(
        List<TableIngestionInfo> ingestion,
        Dictionary<string, DateTimeOffset> queryHistory)
    {
        var unusedTables = new List<UnusedLogTable>();
        var now = DateTimeOffset.UtcNow;

        foreach (var table in ingestion.Where(t => t.EstimatedMonthlyCost > 1m))
        {
            if (!queryHistory.TryGetValue(table.TableName, out var lastQueried))
            {
                // Never queried — prime candidate for removal
                unusedTables.Add(new UnusedLogTable
                {
                    TableName = table.TableName,
                    MonthlyIngestionGB = table.DailyAverageGB * 30,
                    LastQueried = null,
                    DaysSinceLastQuery = 999,
                    EstimatedMonthlyCost = table.EstimatedMonthlyCost,
                    RecommendedAction = GetRemediationRecommendation(table.TableName, null)
                });
            }
            else
            {
                var daysSince = (int)(now - lastQueried).TotalDays;
                if (daysSince > 60)
                {
                    unusedTables.Add(new UnusedLogTable
                    {
                        TableName = table.TableName,
                        MonthlyIngestionGB = table.DailyAverageGB * 30,
                        LastQueried = lastQueried,
                        DaysSinceLastQuery = daysSince,
                        EstimatedMonthlyCost = table.EstimatedMonthlyCost,
                        RecommendedAction = GetRemediationRecommendation(table.TableName, daysSince)
                    });
                }
            }
        }

        return unusedTables.OrderByDescending(t => t.EstimatedMonthlyCost).ToList();
    }

    private async Task<LogsQueryResult?> ExecuteKqlAsync(
        string workspaceId,
        string kql,
        TimeSpan timeRange,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                workspaceId,
                kql,
                new LogsQueryTimeRange(timeRange),
                cancellationToken: cancellationToken);

            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KQL query failed for workspace {WorkspaceId} (non-fatal)", workspaceId);
            return null;
        }
    }

    private static int GetRecommendedRetentionDays(string tableName) => tableName switch
    {
        "AzureActivity" => 31,
        "AppServiceHTTPLogs" => 31,
        "ContainerLog" => 14,
        "Perf" => 31,
        "InsightsMetrics" => 31,
        "AzureDiagnostics" => 31,
        var t when t.StartsWith("Azure") => 31,
        var t when t.StartsWith("App") => 31,
        _ => 90 // Default: keep 90 days for audit/compliance tables
    };

    private static string GetRetentionJustification(string tableName) => tableName switch
    {
        "AzureActivity" => "Control plane audit logs: 31 days sufficient for ops; use archive for compliance >90 days",
        "AppServiceHTTPLogs" => "HTTP access logs: only needed for active incident investigation",
        "ContainerLog" => "Container stdout/stderr: 14 days is typically sufficient",
        "Perf" => "Performance counters: useful for troubleshooting recent issues only",
        _ => "Standard operational logs: reduce retention to 31 days and archive to cold storage if compliance requires"
    };

    private static string GetRemediationRecommendation(string tableName, int? daysSinceLastQuery) =>
        daysSinceLastQuery is null
            ? $"Table '{tableName}' has never been queried. Disable the diagnostic setting that feeds this table."
            : $"Table '{tableName}' not queried in {daysSinceLastQuery} days. Consider disabling or switching to Basic log tier.";
}

public sealed class TableIngestionInfo
{
    public required string TableName { get; set; }
    public decimal TotalIngestedGB { get; set; }
    public decimal DailyAverageGB { get; set; }
    public decimal EstimatedMonthlyCost { get; set; }
}
