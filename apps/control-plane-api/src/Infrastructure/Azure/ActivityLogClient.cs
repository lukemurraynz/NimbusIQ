using System.Diagnostics;
using System.Text.Json;
using Azure.Core;
using Atlas.ControlPlane.Infrastructure.Auth;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Infrastructure.Azure;

/// <summary>
/// Represents an Azure Activity Log event from the Activity Log API.
/// Activity Log captures control plane operations (ARM API calls) with full context.
/// </summary>
public record ActivityLogEvent(
    string EventName,
    string OperationName,
    DateTime EventTimestamp,
    string ResourceId,
    string? ResourceGroup,
    string? SubscriptionId,
    string CallerIdentity,
    string? CallerIdentityType,
    string? CorrelationId,
    string Status,
    string? SubStatus,
    string EventCategory,
    string AuthorizationAction,
    JsonElement? Properties);

/// <summary>
/// Client for querying Azure Activity Log (control plane operations) using managed identity.
///
/// Azure Activity Log captures all ARM control plane operations including:
/// - Portal changes (manual human actions)
/// - Pipeline deployments (service principal/managed identity actions)
/// - Policy effects (Azure Policy remediation tasks)
/// - Autoscale operations (platform-initiated scaling)
///
/// This provides authoritative root cause data for drift detection, unlike internal audit events
/// which only capture NimbusIQ application operations.
///
/// See: https://learn.microsoft.com/azure/azure-monitor/essentials/activity-log-schema
/// </summary>
public class ActivityLogClient
{
  private readonly TokenCredential _credential;
  private readonly HttpClient _httpClient;
  private readonly ILogger<ActivityLogClient> _logger;
  private static readonly ActivitySource ActivitySource = new("Atlas.ControlPlane.Azure.ActivityLog");
  private const string ActivityLogApiVersion = "2015-04-01";

  public ActivityLogClient(
      ManagedIdentityCredentialProvider credentialProvider,
      HttpClient httpClient,
      ILogger<ActivityLogClient> logger)
  {
    _logger = logger;
    _credential = credentialProvider.GetCredential();
    _httpClient = httpClient;
  }

  /// <summary>
  /// Queries Activity Log events for a subscription within a time window.
  /// </summary>
  /// <param name="subscriptionId">Azure subscription ID</param>
  /// <param name="startTime">Start of time window (UTC)</param>
  /// <param name="endTime">End of time window (UTC)</param>
  /// <param name="resourceId">Optional filter by specific resource ID</param>
  /// <param name="resourceGroup">Optional filter by resource group</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of Activity Log events</returns>
  public async Task<List<ActivityLogEvent>> QueryActivityLogAsync(
      string subscriptionId,
      DateTime startTime,
      DateTime endTime,
      string? resourceId = null,
      string? resourceGroup = null,
      CancellationToken cancellationToken = default)
  {
    using var activity = ActivitySource.StartActivity("ActivityLog.Query");
    activity?.SetTag("subscription.id", subscriptionId);
    activity?.SetTag("time.start", startTime);
    activity?.SetTag("time.end", endTime);
    activity?.SetTag("filter.resource_id", resourceId);
    activity?.SetTag("filter.resource_group", resourceGroup);

    try
    {
      // Build OData filter for Activity Log API
      var filters = new List<string>
            {
                $"eventTimestamp ge '{startTime:yyyy-MM-ddTHH:mm:ssZ}'",
                $"eventTimestamp le '{endTime:yyyy-MM-ddTHH:mm:ssZ}'"
            };

      if (!string.IsNullOrWhiteSpace(resourceGroup))
      {
        filters.Add($"resourceGroupName eq '{resourceGroup}'");
      }

      if (!string.IsNullOrWhiteSpace(resourceId))
      {
        filters.Add($"resourceId eq '{resourceId}'");
      }

      var filterString = string.Join(" and ", filters);
      var requestUri = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Insights/eventtypes/management/values?api-version={ActivityLogApiVersion}&$filter={Uri.EscapeDataString(filterString)}";

      _logger.LogInformation(
          "Querying Activity Log: Subscription={SubscriptionId}, TimeWindow={Start} to {End}, Filter={Filter}",
          subscriptionId, startTime, endTime, filterString);

      // Get access token for Azure Management API
      var tokenRequest = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
      var token = await _credential.GetTokenAsync(tokenRequest, cancellationToken);

      var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
      request.Headers.Add("Authorization", $"Bearer {token.Token}");

      var response = await _httpClient.SendAsync(request, cancellationToken);
      response.EnsureSuccessStatusCode();

      var content = await response.Content.ReadAsStringAsync(cancellationToken);
      var json = JsonDocument.Parse(content);

      var events = new List<ActivityLogEvent>();

      if (json.RootElement.TryGetProperty("value", out var valueArray))
      {
        foreach (var item in valueArray.EnumerateArray())
        {
          try
          {
            var evt = ParseActivityLogEvent(item);
            if (evt != null)
            {
              events.Add(evt);
            }
          }
          catch (Exception ex)
          {
            _logger.LogWarning(ex, "Failed to parse Activity Log event");
          }
        }
      }

      activity?.SetTag("result.event_count", events.Count);

      _logger.LogInformation(
          "Activity Log query completed: {EventCount} events retrieved",
          events.Count);

      return events;
    }
    catch (Exception ex)
    {
      activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      _logger.LogError(ex,
          "Activity Log query failed for subscription {SubscriptionId}",
          subscriptionId);
      throw;
    }
  }

  /// <summary>
  /// Queries Activity Log events correlated to a specific operation (correlation ID).
  /// Useful for tracking all operations in a deployment or pipeline run.
  /// </summary>
  public async Task<List<ActivityLogEvent>> QueryByCorrelationIdAsync(
      string subscriptionId,
      string correlationId,
      DateTime startTime,
      DateTime endTime,
      CancellationToken cancellationToken = default)
  {
    using var activity = ActivitySource.StartActivity("ActivityLog.QueryByCorrelationId");
    activity?.SetTag("subscription.id", subscriptionId);
    activity?.SetTag("correlation.id", correlationId);

    var filters = new List<string>
        {
            $"eventTimestamp ge '{startTime:yyyy-MM-ddTHH:mm:ssZ}'",
            $"eventTimestamp le '{endTime:yyyy-MM-ddTHH:mm:ssZ}'",
            $"correlationId eq '{correlationId}'"
        };

    var filterString = string.Join(" and ", filters);
    var requestUri = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Insights/eventtypes/management/values?api-version={ActivityLogApiVersion}&$filter={Uri.EscapeDataString(filterString)}";

    try
    {
      var tokenRequest = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
      var token = await _credential.GetTokenAsync(tokenRequest, cancellationToken);

      var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
      request.Headers.Add("Authorization", $"Bearer {token.Token}");

      var response = await _httpClient.SendAsync(request, cancellationToken);
      response.EnsureSuccessStatusCode();

      var content = await response.Content.ReadAsStringAsync(cancellationToken);
      var json = JsonDocument.Parse(content);

      var events = new List<ActivityLogEvent>();

      if (json.RootElement.TryGetProperty("value", out var valueArray))
      {
        foreach (var item in valueArray.EnumerateArray())
        {
          var evt = ParseActivityLogEvent(item);
          if (evt != null)
          {
            events.Add(evt);
          }
        }
      }

      _logger.LogInformation(
          "Activity Log query by correlation ID completed: {EventCount} events for {CorrelationId}",
          events.Count, correlationId);

      return events;
    }
    catch (Exception ex)
    {
      activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      _logger.LogError(ex,
          "Activity Log query by correlation ID failed: {CorrelationId}",
          correlationId);
      throw;
    }
  }

  /// <summary>
  /// Categorizes an Activity Log event into drift cause types.
  /// </summary>
  public static string CategorizeActivityLogEvent(ActivityLogEvent evt)
  {
    var operationName = evt.OperationName.ToLowerInvariant();
    var callerType = evt.CallerIdentityType?.ToLowerInvariant() ?? "";

    // Pipeline deployment: Service principal or managed identity performing write operations
    if ((callerType.Contains("serviceprincipal") || callerType.Contains("managedidentity")) &&
        (operationName.Contains("/write") || operationName.Contains("deploy")))
    {
      return "PipelineDeployment";
    }

    // Policy effect: Azure Policy remediation task
    if (operationName.Contains("microsoft.policyinsights") ||
        operationName.Contains("remediation") ||
        evt.CallerIdentity.Contains("Microsoft.PolicyInsights", StringComparison.OrdinalIgnoreCase))
    {
      return "PolicyEffect";
    }

    // Platform scaling: Autoscale or platform-initiated operations
    if (operationName.Contains("autoscale") ||
        operationName.Contains("scale") ||
        evt.CallerIdentity.Contains("Microsoft.Azure.Autoscale", StringComparison.OrdinalIgnoreCase) ||
        evt.CallerIdentity.Contains("Azure", StringComparison.OrdinalIgnoreCase))
    {
      return "PlatformScaling";
    }

    // Manual portal change: User identity performing write operations
    if ((callerType.Contains("user") || string.IsNullOrEmpty(callerType)) &&
        (operationName.Contains("/write") || operationName.Contains("/delete")))
    {
      return "ManualChange";
    }

    return "UnknownChange";
  }

  /// <summary>
  /// Parses an Activity Log event JSON element into a structured record with strict null safety.
  /// Returns null if essential fields (eventName, operationName, eventTimestamp, resourceId) are missing.
  /// </summary>
  private ActivityLogEvent? ParseActivityLogEvent(JsonElement element)
  {
    // Required fields - return null if missing
    if (!element.TryGetProperty("eventName", out var eventNameElem) ||
        eventNameElem.ValueKind != JsonValueKind.String)
    {
      return null;
    }

    if (!element.TryGetProperty("operationName", out var operationNameElem) ||
        operationNameElem.ValueKind != JsonValueKind.String)
    {
      return null;
    }

    if (!element.TryGetProperty("eventTimestamp", out var timestampElem) ||
        !DateTime.TryParse(timestampElem.GetString(), out var timestamp))
    {
      return null;
    }

    if (!element.TryGetProperty("resourceId", out var resourceIdElem) ||
        resourceIdElem.ValueKind != JsonValueKind.String)
    {
      return null;
    }

    // Required field with fallback
    var status = GetStringProperty(element, "status") ?? "Unknown";

    // Extract caller identity (required)
    var callerIdentity = GetStringProperty(element, "caller") ?? "Unknown";
    string? callerIdentityType = null;

    if (element.TryGetProperty("authorization", out var authElem) &&
        authElem.TryGetProperty("evidence", out var evidenceElem) &&
        evidenceElem.TryGetProperty("principalType", out var principalTypeElem))
    {
      callerIdentityType = principalTypeElem.GetString();
    }

    // Extract authorization action (for filtering)
    string authorizationAction = "None";
    if (element.TryGetProperty("authorization", out var authorizationElem) &&
        authorizationElem.TryGetProperty("action", out var actionElem))
    {
      authorizationAction = actionElem.GetString() ?? "None";
    }

    // Extract event category (Administrative, ServiceHealth, etc.)
    var category = GetStringProperty(element, "category") ?? "Administrative";

    return new ActivityLogEvent(
        EventName: eventNameElem.GetString()!,
        OperationName: operationNameElem.GetString()!,
        EventTimestamp: timestamp,
        ResourceId: resourceIdElem.GetString()!,
        ResourceGroup: GetStringProperty(element, "resourceGroupName"),
        SubscriptionId: GetStringProperty(element, "subscriptionId"),
        CallerIdentity: callerIdentity,
        CallerIdentityType: callerIdentityType,
        CorrelationId: GetStringProperty(element, "correlationId"),
        Status: status,
        SubStatus: GetStringProperty(element, "subStatus"),
        EventCategory: category,
        AuthorizationAction: authorizationAction,
        Properties: element.TryGetProperty("properties", out var props) ? props : null
    );
  }

  private static string? GetStringProperty(JsonElement element, string propertyName)
  {
    if (element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.String)
    {
      return prop.GetString();
    }
    return null;
  }
}
