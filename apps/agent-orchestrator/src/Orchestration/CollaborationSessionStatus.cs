namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// Canonical status vocabulary for <see cref="AgentCollaborationSession.Status"/>.
/// </summary>
public static class CollaborationSessionStatus
{
  public const string Active = "active";
  public const string Completed = "completed";
  /// <summary>Workflow ended before all nodes ran (non-fatal semi-complete state).</summary>
  public const string Halted = "halted";
  public const string Failed = "failed";
}
