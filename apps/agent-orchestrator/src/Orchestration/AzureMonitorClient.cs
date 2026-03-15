using Atlas.AgentOrchestrator.Integrations.Auth;
using Azure.Core;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// Queries Azure Monitor via the ARM REST API using the system-assigned managed identity
/// to check if resources have metrics available.
/// </summary>
public class AzureMonitorClient : IAzureMonitorClient
{
  private const string MetricDefinitionsApiVersion = "2018-01-01";
  private const string ArmResource = "https://management.azure.com/";

  private readonly HttpClient _httpClient;
  private readonly ManagedIdentityCredentialProvider _credentialProvider;
  private readonly ILogger<AzureMonitorClient> _logger;

  public AzureMonitorClient(
      HttpClient httpClient,
      ManagedIdentityCredentialProvider credentialProvider,
      ILogger<AzureMonitorClient> logger)
  {
    _httpClient = httpClient;
    _credentialProvider = credentialProvider;
    _logger = logger;
    _httpClient.BaseAddress = new Uri(ArmResource);
  }

  /// <summary>
  /// Checks if a resource has metrics available in Azure Monitor.
  /// Returns true if the resource has at least one metric definition,
  /// false if no metrics are available or the resource doesn't support monitoring.
  /// </summary>
  public async Task<bool> HasMetricsAsync(string resourceId, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(resourceId))
    {
      return false;
    }

    // Normalize resource ID to ensure it doesn't have leading/trailing slashes
    var normalizedResourceId = resourceId.Trim('/');

    try
    {
      var credential = _credentialProvider.GetSystemAssignedCredential();
      var tokenContext = new TokenRequestContext([ArmResource + ".default"]);
      var accessToken = await credential.GetTokenAsync(tokenContext, cancellationToken);

      using var request = new HttpRequestMessage(
          HttpMethod.Get,
          $"{normalizedResourceId}/providers/microsoft.insights/metricDefinitions?api-version={MetricDefinitionsApiVersion}");

      request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

      using var response = await _httpClient.SendAsync(request, cancellationToken);

      if (!response.IsSuccessStatusCode)
      {
        // 404 means the resource doesn't support metrics or doesn't exist
        // 403 might mean lack of permissions
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
          _logger.LogDebug("Resource {ResourceId} has no metric definitions (404)", resourceId);
          return false;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning(
            "Failed to check metrics for resource {ResourceId}: {StatusCode} - {Error}",
            resourceId,
            (int)response.StatusCode,
            errorBody.Length > 200 ? errorBody[..200] : errorBody);
        return false;
      }

      var responseBody = await response.Content.ReadFromJsonAsync<MetricDefinitionsResponse>(cancellationToken: cancellationToken);

      if (responseBody?.Value is not null && responseBody.Value.Count > 0)
      {
        _logger.LogDebug("Resource {ResourceId} has {MetricCount} metric definitions", resourceId, responseBody.Value.Count);
        return true;
      }

      _logger.LogDebug("Resource {ResourceId} has no metrics available", resourceId);
      return false;
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Exception checking metrics for resource {ResourceId}", resourceId);
      return false;
    }
  }

  private sealed class MetricDefinitionsResponse
  {
    public List<MetricDefinition>? Value { get; set; }
  }

  private sealed class MetricDefinition
  {
    public string? Name { get; set; }
    public string? Unit { get; set; }
    public string? PrimaryAggregationType { get; set; }
    public List<string>? SupportedAggregationTypes { get; set; }
  }
}
