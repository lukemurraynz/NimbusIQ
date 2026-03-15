using System.Diagnostics;
using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Atlas.ControlPlane.Infrastructure.Auth;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Infrastructure.Azure;

/// <summary>
/// Client for querying Azure Monitor (Metrics and Logs) using managed identity.
/// Supports Kusto queries for Log Analytics workspaces.
/// </summary>
public class AzureMonitorClient
{
    private readonly LogsQueryClient _logsClient;
    private readonly MetricsQueryClient _metricsClient;
    private readonly ILogger<AzureMonitorClient> _logger;
    private static readonly ActivitySource ActivitySource = new("Atlas.ControlPlane.Azure.Monitor");

    public AzureMonitorClient(
        ManagedIdentityCredentialProvider credentialProvider,
        ILogger<AzureMonitorClient> logger)
    {
        _logger = logger;
        // Use the shared user-assigned identity (AZURE_CLIENT_ID), which receives
        // subscription-scope Monitoring Reader during azd provision.
        var credential = credentialProvider.GetCredential();

        _logsClient = new LogsQueryClient(credential);
        _metricsClient = new MetricsQueryClient(credential);
    }

    /// <summary>
    /// Executes a Kusto query against Log Analytics workspace.
    /// </summary>
    /// <param name="workspaceId">Log Analytics workspace ID</param>
    /// <param name="query">Kusto query string</param>
    /// <param name="timeRange">Time range for the query</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<LogsQueryResult> QueryLogsAsync(
        string workspaceId,
        string query,
        QueryTimeRange timeRange,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("Monitor.QueryLogs");
        activity?.SetTag("workspace.id", workspaceId);
        activity?.SetTag("query.length", query.Length);
        activity?.SetTag("query.time_range", timeRange.ToString());

        try
        {
            var response = await _logsClient.QueryWorkspaceAsync(
                workspaceId,
                query,
                timeRange,
                cancellationToken: cancellationToken);

            var result = response.Value;

            activity?.SetTag("result.table_count", result.AllTables.Count);
            activity?.SetTag("result.status", result.Status.ToString());

            _logger.LogInformation(
                "Log Analytics query executed successfully. Workspace: {WorkspaceId}, Tables: {TableCount}, Status: {Status}",
                workspaceId,
                result.AllTables.Count,
                result.Status);

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Log Analytics query failed for workspace {WorkspaceId}", workspaceId);
            throw;
        }
    }

    /// <summary>
    /// Queries metrics for an Azure resource.
    /// </summary>
    /// <param name="resourceId">Full Azure resource ID</param>
    /// <param name="metricNames">List of metric names to query</param>
    /// <param name="timeRange">Time range for the metrics</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<MetricsQueryResult> QueryMetricsAsync(
        string resourceId,
        IEnumerable<string> metricNames,
        QueryTimeRange timeRange,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("Monitor.QueryMetrics");
        activity?.SetTag("resource.id", resourceId);
        activity?.SetTag("metric.names", string.Join(",", metricNames));

        try
        {
            var response = await _metricsClient.QueryResourceAsync(
                resourceId,
                metricNames,
                new MetricsQueryOptions
                {
                    TimeRange = timeRange,
                    Granularity = TimeSpan.FromMinutes(5)
                },
                cancellationToken);

            var result = response.Value;

            activity?.SetTag("result.metric_count", result.Metrics.Count);

            _logger.LogInformation(
                "Metrics query executed successfully. Resource: {ResourceId}, Metrics: {MetricCount}",
                resourceId,
                result.Metrics.Count);

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Metrics query failed for resource {ResourceId}", resourceId);
            throw;
        }
    }

    /// <summary>
    /// Executes a batch query across multiple workspaces using the SDK batch API.
    /// </summary>
    public async Task<LogsBatchQueryResultCollection?> QueryLogsBatchAsync(
        IEnumerable<(string WorkspaceId, string Query, QueryTimeRange TimeRange)> queries,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("Monitor.QueryLogsBatch");
        var queryList = queries.ToList();
        activity?.SetTag("query.count", queryList.Count);

        if (queryList.Count == 0)
            return null;

        try
        {
            var batch = new LogsBatchQuery();
            foreach (var (workspaceId, query, timeRange) in queryList)
            {
                batch.AddWorkspaceQuery(workspaceId, query, timeRange);
            }

            var response = await _logsClient.QueryBatchAsync(batch, cancellationToken);

            _logger.LogInformation("Batch log queries executed successfully. Query count: {QueryCount}", queryList.Count);

            return response.Value;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Batch log query failed");
            throw;
        }
    }
}
