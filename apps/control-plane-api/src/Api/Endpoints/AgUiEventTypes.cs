using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.ControlPlane.Api.Endpoints;

/// <summary>
/// Canonical AG-UI protocol event type constants shared by all SSE streaming controllers.
/// Values follow the AG-UI specification (uppercase underscore convention).
/// </summary>
internal static class AgUiEventTypes
{
  public const string RunStarted = "RUN_STARTED";
  public const string RunFinished = "RUN_FINISHED";
  public const string RunError = "RUN_ERROR";
  public const string TextMessageStart = "TEXT_MESSAGE_START";
  public const string TextMessageContent = "TEXT_MESSAGE_CONTENT";
  public const string TextMessageEnd = "TEXT_MESSAGE_END";
  public const string ToolCallStart = "TOOL_CALL_START";
  public const string ToolCallArgs = "TOOL_CALL_ARGS";
  public const string ToolCallEnd = "TOOL_CALL_END";
  public const string StateSnapshot = "STATE_SNAPSHOT";

  /// <summary>
  /// Shared serializer options for AG-UI SSE event payloads.
  /// camelCase properties, nulls omitted.
  /// </summary>
  public static readonly JsonSerializerOptions SseJsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };
}
