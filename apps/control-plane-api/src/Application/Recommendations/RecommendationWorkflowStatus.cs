namespace Atlas.ControlPlane.Application.Recommendations;

public static class RecommendationWorkflowStatus
{
  public const string Draft = "draft";
  public const string Pending = "pending";
  public const string PendingApproval = "pending_approval";
  public const string ManualReview = "manual_review";
  public const string Planned = "planned";
  public const string InProgress = "in_progress";
  public const string Verified = "verified";
  public const string Approved = "approved";
  public const string Rejected = "rejected";

  private static readonly HashSet<string> ValidStatuses =
  [
      Draft,
        Pending,
        PendingApproval,
        ManualReview,
        Planned,
        InProgress,
        Verified,
        Approved,
        Rejected
  ];

  private static readonly HashSet<string> QueueStatuses = [Pending, PendingApproval, ManualReview];

  // Database-safe values for EF queries. Keep this list free of runtime normalization logic so
  // LINQ providers can translate Contains(...) to SQL IN clauses.
  private static readonly string[] QueueStatusDatabaseValues =
  [
      Pending,
      PendingApproval,
      ManualReview,
      // Legacy value observed in older records before normalization policy was introduced.
      "pending_second_approval"
  ];

  private static readonly HashSet<string> ChangeSetEligibleStatuses = [Pending, PendingApproval, ManualReview, Approved];

  private static readonly HashSet<string> ApprovableStatuses = [Draft, Pending, PendingApproval, ManualReview];

  public static string Normalize(string? status)
  {
    var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
    return normalized switch
    {
      "pending_second_approval" => PendingApproval,
      _ => normalized
    };
  }

  public static bool IsValid(string? status) => ValidStatuses.Contains(Normalize(status));

  public static bool IsQueueCandidate(string? status) => QueueStatuses.Contains(Normalize(status));

  public static IReadOnlyList<string> GetQueueStatusDatabaseValues() => QueueStatusDatabaseValues;

  public static bool IsChangeSetEligible(string? status) => ChangeSetEligibleStatuses.Contains(Normalize(status));

  public static bool CanApprove(string? status) => ApprovableStatuses.Contains(Normalize(status));

  public static IEnumerable<string> ExpandStatusFilter(string status)
  {
    var normalized = Normalize(status);
    if (normalized == Pending)
    {
      return [Pending, PendingApproval];
    }

    if (normalized == PendingApproval)
    {
      return [PendingApproval, Pending];
    }

    return [normalized];
  }
}
