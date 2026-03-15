using System.Diagnostics;

namespace Atlas.AgentOrchestrator.Integrations.MCP;

/// <summary>
/// Context metadata for MCP tool calls so audit logs can be correlated end-to-end.
/// </summary>
public sealed record ToolCallContext
{
    public Guid? AnalysisRunId { get; init; }
    public Guid? ServiceGroupId { get; init; }
    public Guid? CorrelationId { get; init; }
    public string? ActorId { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public string? TraceParent { get; init; }

    public static ToolCallContext FromActivity(ToolCallContext baseContext, Activity? activity)
    {
        if (activity is null)
        {
            return baseContext;
        }

        return baseContext with
        {
            TraceId = activity.TraceId.ToString(),
            SpanId = activity.SpanId.ToString(),
            TraceParent = activity.Id
        };
    }
}
