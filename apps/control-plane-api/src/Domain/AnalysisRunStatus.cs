namespace Atlas.ControlPlane.Domain.Entities;

/// <summary>
/// Canonical status vocabulary for <see cref="AnalysisRun.Status"/>.
/// All writes to and comparisons against AnalysisRun.Status must use these constants
/// to prevent typo-driven inconsistencies across the codebase.
/// </summary>
/// <remarks>
/// Transition matrix:
///   queued → orchestrating → completed
///                          → partial
///                          → failed
///   queued → cancelled
/// </remarks>
public static class AnalysisRunStatus
{
  /// <summary>Row inserted; waiting to be claimed by the control-plane processor.</summary>
  public const string Queued = "queued";

  /// <summary>Control-plane AnalysisOrchestrationService has claimed the run (discovery + scoring in progress).</summary>
  public const string Running = "running";

  /// <summary>Agent orchestrator Worker has claimed a completed run to run AI agents against it.</summary>
  public const string Orchestrating = "orchestrating";

  /// <summary>All agents completed successfully.</summary>
  public const string Completed = "completed";

  /// <summary>Some agents completed but at least one reported an error.</summary>
  public const string Partial = "partial";

  /// <summary>Orchestration halted due to an unrecoverable error.</summary>
  public const string Failed = "failed";

  /// <summary>Explicitly cancelled before or during orchestration.</summary>
  public const string Cancelled = "cancelled";

  /// <summary>Returns true when the run has reached a terminal state and will not change.</summary>
  public static bool IsTerminal(string? status) =>
      status is Completed or Partial or Failed or Cancelled;
}
