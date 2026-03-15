export const RECOMMENDATION_WORKFLOW_STATUS = {
  draft: "draft",
  pending: "pending",
  pendingApproval: "pending_approval",
  manualReview: "manual_review",
  planned: "planned",
  inProgress: "in_progress",
  verified: "verified",
  approved: "approved",
  rejected: "rejected",
} as const;

export const CHANGE_SET_ELIGIBLE_STATUSES = new Set<string>([
  RECOMMENDATION_WORKFLOW_STATUS.pending,
  RECOMMENDATION_WORKFLOW_STATUS.pendingApproval,
  RECOMMENDATION_WORKFLOW_STATUS.manualReview,
  RECOMMENDATION_WORKFLOW_STATUS.approved,
]);

export function normalizeRecommendationStatus(
  status: string | undefined,
): string {
  const normalized = String(status ?? "")
    .trim()
    .toLowerCase();
  if (normalized === "pending_second_approval") {
    return RECOMMENDATION_WORKFLOW_STATUS.pendingApproval;
  }

  return normalized;
}

export function isQueueCandidateStatus(status: string | undefined): boolean {
  const normalized = normalizeRecommendationStatus(status);
  return (
    normalized === RECOMMENDATION_WORKFLOW_STATUS.pending ||
    normalized === RECOMMENDATION_WORKFLOW_STATUS.pendingApproval ||
    normalized === RECOMMENDATION_WORKFLOW_STATUS.manualReview
  );
}
