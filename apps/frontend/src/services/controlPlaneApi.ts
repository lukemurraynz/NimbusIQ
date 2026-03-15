/**
 * T027: Control plane API service with correlation tracking
 */
import { RateLimiter } from "../utils/rateLimiter";
import { log } from "../telemetry/logger";

// API Response Types
export interface ServiceGroup {
  id: string;
  name: string;
  description?: string;
  subscriptionId?: string;
  resourceGroupName?: string;
  scopeCount: number;
  tags?: Record<string, string>;
  createdAt?: string;
  updatedAt?: string;
}

export interface ServiceGroupHealthResponse {
  serviceGroup: {
    id: string;
    name: string;
    description?: string;
    businessOwner?: string;
    createdAt: string;
  };
  latestScores: Record<
    string,
    {
      score: number;
      confidence: number;
      resourceCount: number;
      recordedAt: string;
      dimensions?: string;
    }
  >;
  pendingRecommendationCount: number;
  priorityInbox: {
    doNow: Array<{
      id: string;
      title: string;
      priority: string;
      category: string;
      status: string;
      resourceId: string;
      queueScore: number;
      dueDate: string;
    }>;
    thisWeek: Array<{
      id: string;
      title: string;
      priority: string;
      category: string;
      status: string;
      resourceId: string;
      queueScore: number;
      dueDate: string;
    }>;
    backlog: Array<{
      id: string;
      title: string;
      priority: string;
      category: string;
      status: string;
      resourceId: string;
      queueScore: number;
      dueDate: string;
    }>;
  };
  businessImpact: {
    outageRisk: number;
    complianceExposure: number;
    monthlyCostOpportunity: number;
    sustainabilityOpportunity: number;
  };
  topRisks: Array<{
    id: string;
    title: string;
    priority: string;
    category: string;
    queueScore: number;
    dueDate: string;
  }>;
  topSavings: Array<{
    id: string;
    title: string;
    priority: string;
    monthlySavings: number;
    resource: string;
  }>;
  reliabilityWeakPoints: Array<{
    dimension: string;
    score: number;
    severity: string;
  }>;
}

export interface AnalysisRun {
  id: string;
  serviceGroupId: string;
  status: string;
  architectureScore?: number;
  finOpsScore?: number;
  reliabilityScore?: number;
  sustainabilityScore?: number;
  confidenceScore?: number;
  confidenceLevel?: string;
  degradationFactors?: string[];
  initiatedAt?: string;
  createdAt: string;
  completedAt?: string;
}

export interface Recommendation {
  id: string;
  correlationId?: string;
  analysisRunId?: string;
  serviceGroupId?: string;
  serviceGroupName?: string;
  resourceId: string;
  category?: string;
  recommendationType: string;
  actionType: string;
  status: string;
  priority: string;
  confidenceScore: number;
  approvalMode: string;
  requiredApprovals: number;
  receivedApprovals: number;
  createdAt: string;
  riskScore?: number;
  riskWeightedScore?: number;
  trustScore?: number;
  trustLevel?: string;
  evidenceCompleteness?: number;
  freshnessDays?: number;
  title?: string;
  summary?: string;
  description?: string;
  rationale?: string;
  evidenceReferences?: string | string[];
  confidenceSource?: string;
  triggerReason?: string;
  changeContext?: string;
  estimatedImpact?: string;
  tradeoffProfile?: string;
  riskProfile?: string;
  impactedServices?: string;
  source?: string;
  sourceLabel?: string;
  wellArchitectedPillar?: string;
  groundingSource?: string;
  groundingTimestampUtc?: string;
  groundingProvenance?: RecommendationGroundingProvenance;
}

export interface RecommendationQueueItem {
  id: string;
  title?: string;
  priority: string;
  status: string;
  category?: string;
  serviceGroupId?: string;
  serviceGroupName?: string;
  riskWeightedScore: number;
  reason: string;
  validUntil?: string;
  createdAt: string;
}

export interface ConfidenceExplainer {
  recommendationId: string;
  confidenceScore: number;
  confidenceSource: string;
  trustScore: number;
  trustLevel: string;
  evidenceCompleteness: number;
  freshnessDays: number;
  evidenceReferences: string[];
  factors: string[];
  summary: string;
}

export interface RecommendationGroundingProvenance {
  groundingSource?: string;
  groundingQuery?: string;
  groundingTimestampUtc?: string;
  groundingToolRunId?: string | null;
  groundingQuality?: number;
  groundingRecencyScore?: number;
}

export interface RecommendationLineageStep {
  id?: string;
  stage?: string;
  title?: string;
  source?: string;
  summary?: string;
  timestampUtc?: string;
  confidence?: number;
}

export interface RecommendationLineageResponse {
  recommendationId: string;
  category?: string;
  source?: string;
  steps: RecommendationLineageStep[];
  provenance?: RecommendationGroundingProvenance;
}

export interface PolicyImpactSimulationResult {
  recommendationId: string;
  policyThreshold: number;
  policyDecision: "safe_to_approve" | "review_required";
  reasons: string[];
  simulation: ScoreSimulationResult;
}

export interface GuardrailLintFinding {
  id: string;
  severity: "error" | "warning" | "info";
  message: string;
  suggestion: string;
}

export interface GuardrailLintResult {
  changeSetId: string;
  passed: boolean;
  findings: GuardrailLintFinding[];
}

export interface ChangeSetSummary {
  id: string;
  recommendationId: string;
  format: string;
  prTitle: string;
  status: string;
  createdAt: string;
  hasContent?: boolean;
}

export interface ChangeSetDetail extends ChangeSetSummary {
  prDescription?: string;
  validationResult?: string | null;
  content?: string | null;
}

export interface ChangeSetPublishResult {
  id: string;
  status: string;
  releaseId: string;
  attestationId: string;
  baselineCaptured: boolean;
  isIdempotent: boolean;
}

export interface PullRequestResult {
  id: string;
  recommendationId: string;
  changeSetId: string;
  repositoryUrl: string;
  branchName: string;
  prNumber?: number;
  prUrl?: string;
  status: string;
  createdAt: string;
  previewMode?: boolean;
}

export interface RecommendationTaskResult {
  taskId: string;
  provider: string;
  status: string;
  title: string;
  dueDate: string;
  payload: Record<string, unknown>;
}

export interface RecommendationWorkflowStatus {
  recommendationId: string;
  stages: {
    reviewEvidence: boolean;
    simulatePolicy: boolean;
    generateChangeSet: boolean;
    validate: boolean;
    guardrailLint: boolean;
    approveReject: boolean;
    publish: boolean;
  };
  currentStatus: string;
  changeSetId?: string;
  changeSetStatus?: string;
}

export interface RecommendationIacExamples {
  recommendationId: string;
  resourceType: string;
  summary: string;
  bicepModulePath: string;
  bicepVersion: string;
  terraformModuleName: string;
  terraformVersion: string;
  bicepExample: string;
  terraformExample: string;
  generatedBy: string;
  citedModules: Array<{
    bicepModulePath: string;
    bicepVersion: string;
    terraformModuleName: string;
    terraformVersion: string;
  }>;
  evidenceUrls: string[];
  generatedAtUtc: string;
}

export interface ChangeSetValidationResult {
  passed: boolean;
  errors: string[];
  warnings: string[];
}

export interface ValueRealizationSnapshot {
  recordedAt: string;
  scores: Record<
    string,
    { score: number; confidence: number; recordedAt: string }
  >;
}

export interface ValueRealizationResult {
  changeSetId: string;
  status: string;
  baseline?: ValueRealizationSnapshot;
  current?: ValueRealizationSnapshot;
  deltas?: Record<string, { scoreDelta: number; confidenceDelta: number }>;
  updatedAt: string;
}

export interface TopSaverItem {
  recommendationId: string;
  title: string;
  monthlySavings: number;
  category: string;
  savingsReason?: string;
}

export interface CostEvidenceItem {
  subscriptionId: string;
  resourceGroup?: string;
  monthToDateCostUsd: number;
  baselineMonthToDateCostUsd?: number;
  estimatedMonthlySavingsUsd?: number;
  anomalyCount: number;
  advisorRecommendationLinks?: number;
  activityLogCorrelationEvents?: number;
  evidenceSource: string;
  lastQueriedAt: string;
}

export interface RoiDashboardData {
  totalEstimatedAnnualSavings: number;
  totalActualAnnualSavings: number;
  totalEstimatedCost: number;
  totalActualCost: number;
  savingsAccuracy: number;
  averagePaybackDays: number;
  totalRecommendations: number;
  paybackAchievedCount: number;
  topSavers: TopSaverItem[];
  billingEvidenceStatus?: string;
  azureMcpToolCallStatus?: string;
  costEvidence?: CostEvidenceItem[];
  currentAnnualRunRate?: number;
  optimisedAnnualRunRate?: number;
}

export interface TimelineEvent {
  id: string;
  eventType: string;
  eventCategory?: string;
  timestamp: string;
  description?: string;
  impact?: string;
  scoreImpact?: number;
  deltaSummary?: string;
  details: Record<string, unknown>;
}

export interface ServiceGraphNode {
  id: string;
  name: string;
  type: string;
  properties: Record<string, unknown>;
}

export interface ServiceGraphEdge {
  source: string;
  target: string;
  relationship: string;
}

export interface AgentMessage {
  id: string;
  messageId: string;
  parentMessageId?: string;
  agentName: string;
  agentRole: string;
  messageType: string;
  payload?: string;
  confidence?: number;
  createdAt: string;
}

export interface ServiceGraph {
  nodes: ServiceGraphNode[];
  edges: ServiceGraphEdge[];
}

export interface ApiRequestOptions {
  correlationId?: string;
  traceId?: string;
  accessToken?: string;
  timeout?: number; // Timeout in milliseconds (default: 30000)
}

export interface ApiResponseMetadata {
  retryAfterSeconds?: number;
  operationLocation?: string;
  location?: string;
  correlationId?: string;
  traceId?: string;
}

interface DiscoverServiceGroupsAcceptedResponse {
  operationId: string;
  correlationId: string;
  status: string;
  operationLocation: string;
  createdAt: string;
}

interface DiscoverServiceGroupsOperationStatusResponse {
  operationId: string;
  correlationId: string;
  status: "queued" | "running" | "succeeded" | "failed";
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  errorCode?: string;
  errorMessage?: string;
  result?: {
    value: ServiceGroup[];
    discovered: number;
    created: number;
    updated: number;
  };
}

export interface ScorePoint {
  id: string;
  category: string;
  score: number;
  confidence: number;
  dimensions?: string;
  deltaFromPrevious?: string;
  resourceCount: number;
  recordedAt: string;
  analysisRunId?: string;
}

export interface ScoreHistoryResponse {
  value: ScorePoint[];
}

export interface ExplainabilityContributor {
  factor: string;
  impact: number;
  severity: string;
  count: number;
}

export interface ExplainabilityAction {
  priority: number;
  action: string;
  estimatedImpact: number;
  effort: string;
}

export interface ScoreExplainabilityResponse {
  serviceGroupId: string;
  category: string;
  analysisRunId?: string;
  currentScore: number;
  targetScore: number;
  gap: number;
  scoringFormula: string;
  contributingDimensions: Record<string, number>;
  wafPillarScores: Record<string, number>;
  topContributors: ExplainabilityContributor[];
  pathToTarget: ExplainabilityAction[];
  confidence: number;
  recordedAt: string;
}

export interface BlastRadiusAffectedResource {
  resourceId: string;
  name: string;
  type: string;
  impactType: string;
}

export interface BlastRadiusSharedRecommendation {
  recommendationId: string;
  title: string;
  category: string;
  priority: string;
  overlapCount: number;
}

export interface BlastRadiusResponse {
  resourceCount: number;
  identityCount: number;
  affectedResources: BlastRadiusAffectedResource[];
  affectedIdentities: BlastRadiusAffectedResource[];
  sharedRecommendations: BlastRadiusSharedRecommendation[];
}

export interface ScoreSimulationRequest {
  serviceGroupId: string;
  hypotheticalChanges: HypotheticalChange[];
}

export interface HypotheticalChange {
  changeType: string;
  category: string;
  description: string;
  estimatedImpact?: number;
}

export interface CostDelta {
  estimatedMonthlySavings: number;
  estimatedImplementationCost: number;
  netAnnualImpact: number;
}

export interface RiskDelta {
  riskLevel: string;
  scoreDelta: number;
  changeCount: number;
  mitigationNeeded: boolean;
}

export interface ScoreSimulationResult {
  currentScores: Record<string, number>;
  projectedScores: Record<string, number>;
  deltas: Record<string, number>;
  costDeltas?: Record<string, CostDelta>;
  riskDeltas?: Record<string, RiskDelta>;
  confidence: number;
  reasoning: string;
}

export interface ExecutiveNarrative {
  summary: string;
  highlights: NarrativeHighlight[];
  generatedAt: string;
  confidenceSource?: string;
}

export interface NarrativeHighlight {
  category: string;
  trend: string;
  message: string;
  severity: string;
}

export interface GovernanceNegotiationRequest {
  serviceGroupId: string;
  conflictIds: string[];
  preferences?: Record<string, string>;
}

export interface GovernanceNegotiationResult {
  resolution: string;
  compromises: GovernanceCompromise[];
  confidence: number;
  reasoning: string;
  agentReasoningSource?: string;
}

export interface GovernanceCompromise {
  conflictId: string;
  recommendationId: string;
  originalRecommendation: string;
  adjustedRecommendation: string;
  tradeoff: string;
  pillar?: string;
  impactScore?: number;
  swot?: GovernanceSwot;
}

export interface GovernanceSwot {
  strengths: string[];
  weaknesses: string[];
  opportunities: string[];
  threats: string[];
}

export interface DriftCategorySummary {
  category: string;
  violationCount: number;
  criticalCount: number;
  highCount: number;
}

export interface CarbonFinding {
  category: string;
  description: string;
  severity: string;
  impact: string;
}

export interface CarbonRecommendation {
  priority: string;
  category: string;
  title: string;
  description: string;
}

export interface CarbonEmissionsResponse {
  serviceGroupId: string;
  analysisRunId: string;
  assessedAt: string | null;
  /** Monthly carbon footprint in kg CO₂ equivalent. 0 when data is unavailable. */
  monthlyKgCO2e: number;
  /**
   * True when sourced from the Azure Carbon Optimization API.
   * False when using estimate-based scoring only.
   */
  hasRealData: boolean;
  dataStatus?: string;
  dataAvailabilityReason?: string | null;
  sustainabilityScore: number | null;
  confidence: number | null;
  regionEmissions: Record<string, number>;
  topFindings: CarbonFinding[];
  topRecommendations: CarbonRecommendation[];
  aiNarrative: string | null;
}

export class ApiRequestError extends Error {
  readonly status: number;
  readonly path: string;
  readonly errorCode: string | null;
  readonly correlationId: string | null;
  readonly traceId: string | null;
  readonly details: unknown;

  constructor(
    message: string,
    options: {
      status: number;
      path: string;
      errorCode: string | null;
      correlationId: string | null;
      traceId: string | null;
      details: unknown;
    },
  ) {
    super(message);
    this.name = "ApiRequestError";
    this.status = options.status;
    this.path = options.path;
    this.errorCode = options.errorCode;
    this.correlationId = options.correlationId;
    this.traceId = options.traceId;
    this.details = options.details;
  }
}

export class ControlPlaneApiService {
  private readonly baseUrl: string;
  private readonly rateLimiter: RateLimiter;

  constructor(
    baseUrl: string = import.meta.env.VITE_CONTROL_PLANE_API_BASE_URL ??
      "/api/v1",
  ) {
    this.baseUrl = ControlPlaneApiService.normalizeApiBaseUrl(baseUrl);
    this.rateLimiter = new RateLimiter({
      capacity: 50,
      refillRate: 10,
      refillInterval: 1000,
    });
  }

  private static normalizeApiBaseUrl(baseUrl: string): string {
    const fallback = "/api/v1";
    const trimmed = baseUrl.trim();
    if (trimmed.length === 0) {
      return fallback;
    }

    const withoutTrailingSlash = trimmed.replace(/\/+$/, "");
    const lower = withoutTrailingSlash.toLowerCase();

    if (lower.endsWith("/api/v1/drasi")) {
      return withoutTrailingSlash.slice(0, -"/drasi".length);
    }

    if (lower.endsWith("/api/drasi")) {
      return `${withoutTrailingSlash.slice(0, -"/drasi".length)}/v1`;
    }

    if (lower.endsWith("/api/v1")) {
      return withoutTrailingSlash;
    }

    if (lower.endsWith("/api")) {
      return `${withoutTrailingSlash}/v1`;
    }

    if (lower.endsWith("/drasi")) {
      return withoutTrailingSlash.slice(0, -"/drasi".length);
    }

    return `${withoutTrailingSlash}/api/v1`;
  }

  /**
   * Execute API request with correlation tracking
   */
  private async request<T>(
    path: string,
    options: RequestInit & ApiRequestOptions = {},
  ): Promise<T> {
    const {
      correlationId,
      traceId,
      accessToken,
      timeout = 30000,
      ...fetchOptions
    } = options;

    // Apply client-side rate limiting
    await this.rateLimiter.consume();

    const headers = new Headers(fetchOptions.headers);

    // T027: Correlation ID propagation
    if (correlationId) {
      headers.set("X-Correlation-Id", correlationId);
    } else {
      headers.set("X-Correlation-Id", crypto.randomUUID());
    }

    if (traceId) {
      headers.set("X-Trace-Id", traceId);
    }

    if (accessToken) {
      headers.set("Authorization", `Bearer ${accessToken}`);
    }

    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), timeout);

    try {
      const response = await fetch(`${this.baseUrl}${path}`, {
        ...fetchOptions,
        headers,
        signal: controller.signal,
      });
      clearTimeout(timeoutId);

      // Extract correlation info from response
      const responseCorrelationId = response.headers.get("X-Correlation-Id");
      const responseTraceId = response.headers.get("X-Trace-Id");

      if (!response.ok) {
        const errorCode = response.headers.get("x-error-code");
        const contentType = response.headers.get("content-type") ?? "";

        let errorPayload: unknown = null;

        if (contentType.includes("application/json")) {
          errorPayload = await response.json().catch(() => null);
        } else {
          const text = await response.text().catch(() => "");
          errorPayload = text || null;
        }

        let errorMessage = `Request failed: ${response.status}`;
        if (errorPayload && typeof errorPayload === "object") {
          const typed = errorPayload as {
            message?: string;
            detail?: string;
            title?: string;
          };
          errorMessage =
            typed.message ?? typed.detail ?? typed.title ?? errorMessage;
        } else if (
          typeof errorPayload === "string" &&
          errorPayload.trim().length > 0
        ) {
          errorMessage = errorPayload.trim();
        }

        // 5xx = unexpected server error; 4xx = client/not-found, often handled by callers
        const logLevel = response.status >= 500 ? 'error' : 'warn';
        log[logLevel]("API request failed:", {
          path,
          status: response.status,
          errorCode,
          correlationId: responseCorrelationId,
          traceId: responseTraceId,
          error: errorPayload,
        });

        throw new ApiRequestError(errorMessage, {
          status: response.status,
          path,
          errorCode,
          correlationId: responseCorrelationId,
          traceId: responseTraceId,
          details: errorPayload,
        });
      }

      return response.json() as Promise<T>;
    } catch (error) {
      clearTimeout(timeoutId);
      if (error instanceof Error && error.name === "AbortError") {
        throw new ApiRequestError(`Request timeout after ${timeout}ms`, {
          status: 408,
          path,
          errorCode: "RequestTimeout",
          correlationId: null,
          traceId: null,
          details: null,
        });
      }
      throw error;
    }
  }

  private static parseRetryAfter(value: string | null): number | undefined {
    if (!value) return undefined;
    const parsed = Number(value);
    if (Number.isFinite(parsed) && parsed >= 0) {
      return parsed;
    }
    return undefined;
  }

  private static async delay(ms: number): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, ms));
  }

  private toApiRelativePath(operationLocation: string): string {
    try {
      const parsed = new URL(operationLocation, window.location.origin);
      const pathWithQuery = `${parsed.pathname}${parsed.search}`;
      const apiPrefix = "/api/v1";
      if (pathWithQuery.toLowerCase().startsWith(apiPrefix)) {
        return pathWithQuery.slice(apiPrefix.length) || "/";
      }
      return pathWithQuery;
    } catch {
      const normalized = operationLocation.trim();
      const apiPrefix = "/api/v1";
      if (normalized.toLowerCase().startsWith(apiPrefix)) {
        return normalized.slice(apiPrefix.length) || "/";
      }
      return normalized;
    }
  }

  private async requestWithMetadata<T>(
    path: string,
    options: RequestInit & ApiRequestOptions = {},
  ): Promise<{ data: T; metadata: ApiResponseMetadata }> {
    const {
      correlationId,
      traceId,
      accessToken,
      timeout = 30000,
      ...fetchOptions
    } = options;

    // Apply client-side rate limiting
    await this.rateLimiter.consume();

    const headers = new Headers(fetchOptions.headers);
    headers.set("X-Correlation-Id", correlationId ?? crypto.randomUUID());
    if (traceId) headers.set("X-Trace-Id", traceId);
    if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);

    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), timeout);

    try {
      const response = await fetch(`${this.baseUrl}${path}`, {
        ...fetchOptions,
        headers,
        signal: controller.signal,
      });
      clearTimeout(timeoutId);

      const responseCorrelationId = response.headers.get("X-Correlation-Id");
      const responseTraceId = response.headers.get("X-Trace-Id");

      if (!response.ok) {
        const errorCode = response.headers.get("x-error-code");
        const contentType = response.headers.get("content-type") ?? "";
        let errorPayload: unknown = null;

        if (contentType.includes("application/json")) {
          errorPayload = await response.json().catch(() => null);
        } else {
          const text = await response.text().catch(() => "");
          errorPayload = text || null;
        }

        let errorMessage = `Request failed: ${response.status}`;
        if (errorPayload && typeof errorPayload === "object") {
          const typed = errorPayload as {
            message?: string;
            detail?: string;
            title?: string;
          };
          errorMessage =
            typed.message ?? typed.detail ?? typed.title ?? errorMessage;
        } else if (
          typeof errorPayload === "string" &&
          errorPayload.trim().length > 0
        ) {
          errorMessage = errorPayload.trim();
        }

        throw new ApiRequestError(errorMessage, {
          status: response.status,
          path,
          errorCode,
          correlationId: responseCorrelationId,
          traceId: responseTraceId,
          details: errorPayload,
        });
      }

      const data = (await response.json()) as T;
      return {
        data,
        metadata: {
          retryAfterSeconds: ControlPlaneApiService.parseRetryAfter(
            response.headers.get("Retry-After"),
          ),
          operationLocation:
            response.headers.get("operation-location") ?? undefined,
          location: response.headers.get("Location") ?? undefined,
          correlationId: responseCorrelationId ?? undefined,
          traceId: responseTraceId ?? undefined,
        },
      };
    } catch (error) {
      clearTimeout(timeoutId);
      if (error instanceof Error && error.name === "AbortError") {
        throw new ApiRequestError(`Request timeout after ${timeout}ms`, {
          status: 408,
          path,
          errorCode: "RequestTimeout",
          correlationId: null,
          traceId: null,
          details: null,
        });
      }
      throw error;
    }
  }

  /**
   * List service groups
   */
  async listServiceGroups(accessToken?: string, correlationId?: string) {
    return this.request<{ value: ServiceGroup[] }>("/service-groups", {
      correlationId,
      accessToken,
    });
  }

  /**
   * Discover Azure Service Groups via Resource Graph and upsert into the DB.
   * Requires the system-assigned managed identity to have Service Group Reader role.
   */
  async discoverServiceGroups(accessToken?: string, correlationId?: string) {
    const start =
      await this.requestWithMetadata<DiscoverServiceGroupsAcceptedResponse>(
        "/service-groups/discover/operations",
        {
          method: "POST",
          correlationId,
          accessToken,
          timeout: 10000,
        },
      );

    const operationLocation =
      start.metadata.operationLocation ?? start.data.operationLocation;

    if (!operationLocation) {
      throw new ApiRequestError(
        "Discovery operation did not return an operation-location header",
        {
          status: 500,
          path: "/service-groups/discover/operations",
          errorCode: "MissingOperationLocation",
          correlationId: start.metadata.correlationId ?? null,
          traceId: start.metadata.traceId ?? null,
          details: start.data,
        },
      );
    }

    const statusPath = this.toApiRelativePath(operationLocation);

    for (let attempt = 0; attempt < 180; attempt++) {
      let statusResponse:
        | {
            data: DiscoverServiceGroupsOperationStatusResponse;
            metadata: ApiResponseMetadata;
          }
        | undefined;

      try {
        statusResponse =
          await this.requestWithMetadata<DiscoverServiceGroupsOperationStatusResponse>(
            statusPath,
            {
              correlationId,
              accessToken,
              timeout: 65000,
            },
          );
      } catch (error) {
        if (
          error instanceof ApiRequestError &&
          error.errorCode === "DiscoveryOperationNotFound"
        ) {
          const fallback = await this.request<{
            value: ServiceGroup[];
            discovered: number;
            created: number;
            updated: number;
          }>("/service-groups/discover", {
            method: "POST",
            correlationId,
            accessToken,
            timeout: 120000,
          });

          return fallback;
        }

        throw error;
      }

      const { data, metadata } = statusResponse;

      if (data.status === "succeeded") {
        if (!data.result) {
          throw new ApiRequestError(
            "Discovery operation completed without result",
            {
              status: 500,
              path: statusPath,
              errorCode: "MissingDiscoveryResult",
              correlationId: metadata.correlationId ?? null,
              traceId: metadata.traceId ?? null,
              details: data,
            },
          );
        }

        return data.result;
      }

      if (data.status === "failed") {
        throw new ApiRequestError(
          data.errorMessage ?? "Discovery operation failed",
          {
            status: 500,
            path: statusPath,
            errorCode: data.errorCode ?? "DiscoveryOperationFailed",
            correlationId: metadata.correlationId ?? data.correlationId ?? null,
            traceId: metadata.traceId ?? null,
            details: data,
          },
        );
      }

      const retryAfterMs = (metadata.retryAfterSeconds ?? 3) * 1000;
      await ControlPlaneApiService.delay(retryAfterMs);
    }

    throw new ApiRequestError("Discovery operation timed out while polling", {
      status: 408,
      path: statusPath,
      errorCode: "DiscoveryPollingTimeout",
      correlationId: correlationId ?? null,
      traceId: null,
      details: null,
    });
  }

  /**
   * Start analysis run (LRO — returns 202 Accepted with operation-location header)
   */
  async startAnalysis(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<{
      runId: string;
      correlationId: string;
      status: string;
      createdAt: string;
    }>(`/service-groups/${serviceGroupId}/analysis?api-version=2025-02-16`, {
      method: "POST",
      correlationId,
      accessToken,
    });
  }

  async startAnalysisWithMetadata(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.requestWithMetadata<{
      runId: string;
      correlationId: string;
      status: string;
      createdAt: string;
    }>(`/service-groups/${serviceGroupId}/analysis?api-version=2025-02-16`, {
      method: "POST",
      correlationId,
      accessToken,
    });
  }

  /**
   * Get analysis run status (LRO poll endpoint)
   */
  async getAnalysisStatus(
    serviceGroupId: string,
    runId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<AnalysisRun>(
      `/service-groups/${serviceGroupId}/analysis/${runId}?api-version=2025-02-16`,
      { correlationId, accessToken },
    );
  }

  async getAnalysisStatusWithMetadata(
    serviceGroupId: string,
    runId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.requestWithMetadata<AnalysisRun>(
      `/service-groups/${serviceGroupId}/analysis/${runId}?api-version=2025-02-16`,
      { correlationId, accessToken },
    );
  }

  /**
   * Get analysis scores for a completed or partial run.
   */
  async getAnalysisScores(
    serviceGroupId: string,
    runId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<{
      runId: string;
      serviceGroupId: string;
      status: string;
      completedAt?: string;
      scores: Array<{
        category: string;
        score: number;
        confidence: number;
        dimensions: Record<string, number>;
        resourceCount: number;
        createdAt: string;
      }>;
    }>(
      `/service-groups/${serviceGroupId}/analysis/${runId}/scores?api-version=2025-02-16`,
      { correlationId, accessToken },
    );
  }

  async getAnalysisScoresWithMetadata(
    serviceGroupId: string,
    runId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.requestWithMetadata<{
      runId: string;
      serviceGroupId: string;
      status: string;
      completedAt?: string;
      scores: Array<{
        category: string;
        score: number;
        confidence: number;
        dimensions: Record<string, number>;
        resourceCount: number;
        createdAt: string;
      }>;
    }>(
      `/service-groups/${serviceGroupId}/analysis/${runId}/scores?api-version=2025-02-16`,
      { correlationId, accessToken },
    );
  }

  /**
   * Get agent messages (debate timeline) for a completed analysis run.
   */
  async getAgentMessages(
    runId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<{ value: AgentMessage[] }>(
      `/analysis/${runId}/messages?api-version=2025-02-16`,
      { correlationId, accessToken },
    );
  }

  /**
   * List analysis runs (optionally filtered by serviceGroupId, status, limit)
   */
  async listAnalysisRuns(
    filters?: { serviceGroupId?: string; status?: string; limit?: number },
    accessToken?: string,
    correlationId?: string,
  ) {
    const params = new URLSearchParams();
    if (filters?.serviceGroupId)
      params.set("serviceGroupId", filters.serviceGroupId);
    if (filters?.status) params.set("status", filters.status);
    if (filters?.limit !== undefined)
      params.set("limit", String(filters.limit));
    const query = params.toString();
    const path =
      query.length > 0
        ? `/analysis?${query}&api-version=2025-02-16`
        : "/analysis?api-version=2025-02-16";
    return this.request<
      Array<{
        id: string;
        serviceGroupId: string;
        status: string;
        correlationId: string;
        initiatedAt: string;
      }>
    >(path, { correlationId, accessToken });
  }

  /**
   * List recommendations
   */
  async listRecommendations(
    filters?: {
      status?: string;
      analysisRunId?: string;
      serviceGroupId?: string;
      orderBy?: string;
      source?: string;
      trustLevel?: string;
      category?: string;
      confidenceBand?: string;
      queueBand?: string;
      freshnessBand?: string;
      search?: string;
      limit?: number;
      offset?: number;
    },
    accessToken?: string,
    correlationId?: string,
  ) {
    const params = new URLSearchParams();
    if (filters?.status) params.set("status", filters.status);
    if (filters?.analysisRunId)
      params.set("analysisRunId", filters.analysisRunId);
    if (filters?.serviceGroupId)
      params.set("serviceGroupId", filters.serviceGroupId);
    if (filters?.orderBy) params.set("orderBy", filters.orderBy);
    if (filters?.source) params.set("source", filters.source);
    if (filters?.trustLevel) params.set("trustLevel", filters.trustLevel);
    if (filters?.category) params.set("category", filters.category);
    if (filters?.confidenceBand)
      params.set("confidenceBand", filters.confidenceBand);
    if (filters?.queueBand) params.set("queueBand", filters.queueBand);
    if (filters?.freshnessBand)
      params.set("freshnessBand", filters.freshnessBand);
    if (filters?.search) params.set("search", filters.search);
    if (typeof filters?.limit === "number")
      params.set("limit", String(filters.limit));
    if (typeof filters?.offset === "number")
      params.set("offset", String(filters.offset));
    const query = params.toString();
    const path =
      query.length > 0 ? `/recommendations?${query}` : "/recommendations";

    return this.request<{ value: Recommendation[] }>(path, {
      correlationId,
      accessToken,
    });
  }

  /**
   * List risk-weighted recommendation queue for triage.
   */
  async listRecommendationPriorityQueue(
    limit = 25,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<{ value: RecommendationQueueItem[] }>(
      `/recommendations/priority-queue?limit=${Math.max(1, limit)}`,
      { correlationId, accessToken },
    );
  }

  /**
   * Get recommendation details
   */
  async getRecommendation(
    recommendationId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<Recommendation>(
      `/recommendations/${recommendationId}`,
      { correlationId, accessToken },
    );
  }

  /**
   * Get confidence explainability details for a recommendation.
   */
  async getRecommendationConfidenceExplainer(
    recommendationId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<ConfidenceExplainer>(
      `/recommendations/${recommendationId}/confidence-explainer`,
      { correlationId, accessToken },
    );
  }

  async getRecommendationLineage(
    recommendationId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<RecommendationLineageResponse>(
      `/recommendations/${recommendationId}/lineage`,
      { correlationId, accessToken },
    );
  }

  /**
   * Simulate policy impact for a recommendation before approval.
   */
  async simulateRecommendationPolicyImpact(
    recommendationId: string,
    request: {
      estimatedImpactOverride?: number;
      policyThreshold?: number;
    } = {},
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<PolicyImpactSimulationResult>(
      `/recommendations/${recommendationId}/policy-impact-simulation`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
        correlationId,
        accessToken,
      },
    );
  }

  /**
   * Get ROI & value tracking dashboard data
   */
  async getValueTrackingDashboard(
    serviceGroupId?: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    const params = new URLSearchParams();
    if (serviceGroupId) params.set("serviceGroupId", serviceGroupId);
    params.set("api-version", "2025-01-23");
    return this.request<RoiDashboardData>(
      `/value-tracking/dashboard?${params.toString()}`,
      { correlationId, accessToken },
    );
  }

  /**
   * List change sets for a recommendation
   */
  async listChangeSets(
    recommendationId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<{ value: ChangeSetSummary[] }>(
      `/recommendations/${recommendationId}/change-sets`,
      { correlationId, accessToken },
    );
  }

  /**
   * Generate an IaC change set for a recommendation.
   */
  async generateChangeSet(
    recommendationId: string,
    format = "bicep",
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<ChangeSetSummary>(
      `/recommendations/${recommendationId}/change-sets`,
      {
        method: "POST",
        correlationId,
        accessToken,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ format }),
      },
    );
  }

  /**
   * Get change set detail
   */
  async getChangeSet(
    changeSetId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<ChangeSetDetail>(`/change-sets/${changeSetId}`, {
      correlationId,
      accessToken,
    });
  }

  /**
   * Run preflight validation for a change set
   */
  async validateChangeSet(
    changeSetId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<{
      id: string;
      status: string;
      passed: boolean;
      errors: string[];
      warnings: string[];
    }>(`/change-sets/${changeSetId}/validate`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({}),
      correlationId,
      accessToken,
    });
  }

  /**
   * Run guardrail linting against IaC artifact content.
   */
  async lintChangeSetGuardrails(
    changeSetId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<GuardrailLintResult>(
      `/change-sets/${changeSetId}/guardrail-lint`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({}),
        correlationId,
        accessToken,
      },
    );
  }

  /**
   * Get value realization deltas for a change set
   */
  async getValueRealization(
    changeSetId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<ValueRealizationResult>(
      `/change-sets/${changeSetId}/value-realization`,
      { correlationId, accessToken },
    );
  }

  /**
   * Publish a validated change set with release attestation metadata.
   */
  async publishChangeSet(
    changeSetId: string,
    request: {
      releaseId: string;
      componentName?: string;
      componentVersion: string;
      mockDetected?: boolean;
      mockDetectionDetails?: string;
      validationScopeId?: string;
    },
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<ChangeSetPublishResult>(
      `/change-sets/${changeSetId}/publish`,
      {
        method: "POST",
        correlationId,
        accessToken,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      },
    );
  }

  /**
   * Create an auto-remediation pull request for a recommendation.
   */
  async createRecommendationPullRequest(
    recommendationId: string,
    request: {
      changeSetId: string;
      repositoryUrl: string;
      targetBranch?: string;
      autoMerge?: boolean;
      reviewers?: string[];
      labels?: string[];
    },
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<PullRequestResult>(
      `/gitops/recommendations/${recommendationId}/create-pr`,
      {
        method: "POST",
        correlationId,
        accessToken,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      },
    );
  }

  /**
   * Approve recommendation
   */
  async approveRecommendation(
    recommendationId: string,
    comments: string,
    approvalIntentHash?: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<Recommendation>(
      `/recommendations/${recommendationId}/approve`,
      {
        method: "POST",
        correlationId,
        accessToken,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ comments, approvalIntentHash }),
      },
    );
  }

  /**
   * Reject recommendation
   */
  async rejectRecommendation(
    recommendationId: string,
    reason: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<Recommendation>(
      `/recommendations/${recommendationId}/reject`,
      {
        method: "POST",
        correlationId,
        accessToken,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason }),
      },
    );
  }

  /**
   * Update recommendation workflow status (planned, in_progress, verified, etc).
   */
  async updateRecommendationStatus(
    recommendationId: string,
    request: { status: string; comments?: string },
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<Recommendation>(
      `/recommendations/${recommendationId}/status`,
      {
        method: "POST",
        correlationId,
        accessToken,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      },
    );
  }

  /**
   * Create a work-tracking task payload from a recommendation.
   */
  async createRecommendationTask(
    recommendationId: string,
    request: {
      provider?: string;
      title?: string;
      assignee?: string;
      dueDate?: string;
      notes?: string;
    },
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<RecommendationTaskResult>(
      `/recommendations/${recommendationId}/tasks`,
      {
        method: "POST",
        correlationId,
        accessToken,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      },
    );
  }

  async getRecommendationWorkflowStatus(
    recommendationId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<RecommendationWorkflowStatus>(
      `/recommendations/${recommendationId}/workflow`,
      {
        correlationId,
        accessToken,
      },
    );
  }

  async getRecommendationIacExamples(
    recommendationId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<RecommendationIacExamples>(
      `/recommendations/${recommendationId}/iac-examples`,
      {
        correlationId,
        accessToken,
      },
    );
  }

  /**
   * Current user identity/roles
   */
  async getMe(accessToken?: string, correlationId?: string) {
    return this.request<{ name?: string; roles: string[] }>("/me", {
      correlationId,
      accessToken,
    });
  }

  /**
   * Timeline (past/present/future)
   */
  async getTimeline(
    serviceGroupId: string,
    days: number,
    accessToken?: string,
    correlationId?: string,
  ) {
    const params = new URLSearchParams({
      serviceGroupId,
      days: String(days),
      "api-version": "2025-02-16",
    });
    return this.request<{
      historicalEvents: TimelineEvent[];
      projectedEvents: TimelineEvent[];
    }>(`/timeline?${params}`, {
      correlationId,
      accessToken,
    });
  }

  /**
   * Get service graph (Phase 1: Knowledge Graph)
   */
  async getServiceGraph(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<{
      nodes: Array<{
        id: string;
        name: string;
        type: string;
        metadata?: Record<string, string>;
      }>;
      edges: Array<{ sourceId: string; targetId: string; type: string }>;
      domains: Array<{
        id: string;
        name: string;
        type: string;
        nodeIds: string[];
      }>;
    }>(`/service-groups/${serviceGroupId}/graph`, {
      correlationId,
      accessToken,
    });
  }

  /**
   * Get service topology (simplified view for visualization)
   */
  async getServiceTopology(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<{
      nodes: Array<{ id: string; name: string; type: string }>;
      edges: Array<{ sourceId: string; targetId: string; type: string }>;
    }>(`/service-groups/${serviceGroupId}/topology`, {
      correlationId,
      accessToken,
    });
  }

  /**
   * Build/rebuild service graph
   */
  async buildServiceGraph(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    return this.request<{
      message: string;
      nodesCreated: number;
      edgesCreated: number;
    }>(`/service-groups/${serviceGroupId}/graph/build`, {
      method: "POST",
      correlationId,
      accessToken,
    });
  }

  /**
   * Get multi-agent analysis scores
   */
  async getAgentScores(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    type Finding = {
      category: string;
      severity: string;
      description: string;
      impact: string;
      affectedResources?: string[];
    };

    type Recommendation = {
      action: string;
      priority: string;
      rationale: string;
      expectedImpact: string;
      estimatedEffort: string;
    };

    type AgentScores = {
      architecture: {
        score: number;
        confidence: number;
        findings: Finding[];
        recommendations: Recommendation[];
      };
      finops: {
        score: number;
        confidence: number;
        findings: Finding[];
        recommendations: Recommendation[];
      };
      reliability: {
        score: number;
        confidence: number;
        findings: Finding[];
        recommendations: Recommendation[];
      };
      sustainability: {
        score: number;
        confidence: number;
        findings: Finding[];
        recommendations: Recommendation[];
      };
      technicalDebt: { score: number; confidence: number; findings: Finding[] };
    };

    return this.request<AgentScores>(
      `/service-groups/${serviceGroupId}/agent-scores`,
      {
        correlationId,
        accessToken,
      },
    );
  }
  /**
   * Get drift snapshots for a service group with optional date filtering
   */
  async getDriftSnapshots(
    serviceGroupId: string,
    days?: number,
    accessToken?: string,
    correlationId?: string,
  ) {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });
    if (days !== undefined) {
      const since = new Date(
        Date.now() - days * 24 * 60 * 60 * 1000,
      ).toISOString();
      params.set("startDate", since);
    }
    return this.request<
      Array<{
        id: string;
        serviceGroupId: string;
        snapshotTime: string;
        totalViolations: number;
        criticalViolations: number;
        highViolations: number;
        mediumViolations: number;
        lowViolations: number;
        driftScore: number;
        categoryBreakdown: string | null;
        trendAnalysis: string | null;
        createdAt: string;
      }>
    >(`/drift/snapshots/${serviceGroupId}?${params.toString()}`, {
      correlationId,
      accessToken,
    });
  }

  /**
   * Get drift trend analysis for a service group
   */
  async getDriftTrends(
    serviceGroupId: string,
    days?: number,
    accessToken?: string,
    correlationId?: string,
  ) {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });
    if (days !== undefined) {
      params.set("days", String(days));
    }
    return this.request<{
      serviceGroupId: string;
      periodDays: number;
      snapshots: Array<{
        id: string;
        serviceGroupId: string;
        snapshotTime: string;
        totalViolations: number;
        criticalViolations: number;
        highViolations: number;
        mediumViolations: number;
        lowViolations: number;
        driftScore: number;
        categoryBreakdown: string | null;
        trendAnalysis: string | null;
        createdAt: string;
        causeType?: string;
        causeActor?: string;
        causeSource?: string;
        causeEventTime?: string;
        causeConfidence?: number;
        causeEventId?: string;
        causeIsAuthoritative?: boolean;
      }>;
      trendDirection: string;
      averageScore: number;
      scoreChange: number;
      categoryTrends: Record<string, number>;
    }>(`/drift/trends/${serviceGroupId}?${params.toString()}`, {
      correlationId,
      accessToken,
    });
  }

  /**
   * Get latest drift status (most recent snapshot) for a service group.
   * Returns null when no snapshot exists yet (404 is a valid "no data" state, not an error).
   */
  async getDriftStatus(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ) {
    try {
      return await this.request<{
        id: string;
        serviceGroupId: string;
        snapshotTime: string;
        totalViolations: number;
        criticalViolations: number;
        highViolations: number;
        mediumViolations: number;
        lowViolations: number;
        driftScore: number;
        categoryBreakdown: string | null;
        trendAnalysis: string | null;
        createdAt: string;
        causeType?: string;
        causeActor?: string;
        causeSource?: string;
        causeEventTime?: string;
        causeConfidence?: number;
        causeEventId?: string;
        causeIsAuthoritative?: boolean;
      }>(`/drift/status/${serviceGroupId}?api-version=2025-02-16`, {
        correlationId,
        accessToken,
      });
    } catch (err) {
      if (err instanceof ApiRequestError && err.status === 404) {
        return null;
      }
      throw err;
    }
  }

  async getDriftCategories(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ): Promise<DriftCategorySummary[]> {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });
    try {
      return await this.request<DriftCategorySummary[]>(
        `/drift/categories/${encodeURIComponent(serviceGroupId)}?${params}`,
        { correlationId, accessToken },
      );
    } catch {
      return [];
    }
  }

  /**
   * Get violations for a service group
   */
  async getViolations(
    serviceGroupId: string,
    options?: { status?: string; severity?: string; limit?: number },
    accessToken?: string,
    correlationId?: string,
  ) {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });
    if (options?.status) params.set("status", options.status);
    if (options?.severity) params.set("severity", options.severity);
    if (options?.limit !== undefined)
      params.set("limit", String(options.limit));
    return this.request<
      Array<{
        id: string;
        ruleId: string;
        ruleName: string;
        category: string;
        driftCategory: string;
        resourceId: string;
        resourceType: string;
        violationType: string;
        severity: string;
        currentState: string;
        expectedState: string;
        status: string;
        detectedAt: string;
      }>
    >(`/service-groups/${serviceGroupId}/violations?${params.toString()}`, {
      correlationId,
      accessToken,
    });
  }
  /**
   * Get audit trail events for an entity (e.g., a recommendation).
   * Security: accessToken is passed via Authorization header, never in URL query parameters (API-001).
   */
  async getAuditEvents(
    options?: {
      entityType?: string;
      entityId?: string;
      eventType?: string;
      startDate?: string;
      endDate?: string;
      maxResults?: number;
      continuationToken?: string;
    },
    accessToken?: string,
    correlationId?: string,
  ) {
    try {
      return await this.request<{
        value: Array<{
          id: string;
          eventName: string;
          actorType: string;
          actorId: string;
          eventPayload?: string;
          timestamp: string;
        }>;
      }>("/audit/events/query", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          apiVersion: "2025-02-16",
          entityType: options?.entityType,
          // Backend currently stores recommendation IDs in EntityId for this UI usage.
          entityId: options?.entityId,
          eventType: options?.eventType,
          startDate: options?.startDate,
          endDate: options?.endDate,
          maxResults: options?.maxResults,
          continuationToken: options?.continuationToken,
        }),
        correlationId,
        accessToken,
      });
    } catch {
      return { value: [] };
    }
  }

  // --- Score History & Explainability ---

  async getScoreHistory(
    serviceGroupId: string,
    options?: { category?: string; limit?: number; since?: string },
    accessToken?: string,
    correlationId?: string,
  ): Promise<ScoreHistoryResponse> {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });
    if (options?.category) params.set("category", options.category);
    if (options?.limit) params.set("limit", String(options.limit));
    if (options?.since) params.set("since", options.since);
    try {
      return await this.request<ScoreHistoryResponse>(
        `/scores/history/${encodeURIComponent(serviceGroupId)}?${params}`,
        { correlationId, accessToken },
      );
    } catch {
      return { value: [] };
    }
  }

  async getLatestScores(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ): Promise<ScoreHistoryResponse> {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });
    try {
      return await this.request<ScoreHistoryResponse>(
        `/scores/latest/${encodeURIComponent(serviceGroupId)}?${params}`,
        { correlationId, accessToken },
      );
    } catch {
      return { value: [] };
    }
  }

  async getScoreExplainability(
    serviceGroupId: string,
    category: string,
    targetScore?: number,
    accessToken?: string,
    correlationId?: string,
  ): Promise<ScoreExplainabilityResponse | null> {
    const params = new URLSearchParams({
      "api-version": "2025-02-16",
      category,
    });
    if (typeof targetScore === "number") {
      params.set("targetScore", String(targetScore));
    }

    try {
      return await this.request<ScoreExplainabilityResponse>(
        `/scores/explainability/${encodeURIComponent(serviceGroupId)}?${params}`,
        { correlationId, accessToken },
      );
    } catch (err) {
      if (err instanceof ApiRequestError && err.status === 404) {
        return null;
      }
      throw err;
    }
  }

  async getBlastRadius(
    serviceGroupId: string,
    options?: { resourceIds?: string[]; recommendationId?: string },
    accessToken?: string,
    correlationId?: string,
  ): Promise<BlastRadiusResponse> {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });

    if (options?.resourceIds && options.resourceIds.length > 0) {
      params.set("resourceIds", options.resourceIds.join(","));
    }

    if (options?.recommendationId) {
      params.set("recommendationId", options.recommendationId);
    }

    return this.request<BlastRadiusResponse>(
      `/service-groups/${encodeURIComponent(serviceGroupId)}/blast-radius?${params}`,
      { correlationId, accessToken },
    );
  }

  async getServiceGroupHealth(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ): Promise<ServiceGroupHealthResponse> {
    return this.request<ServiceGroupHealthResponse>(
      `/service-groups/${encodeURIComponent(serviceGroupId)}/health`,
      { correlationId, accessToken },
    );
  }

  // --- Score Simulation ---

  async simulateScores(
    request: ScoreSimulationRequest,
    accessToken?: string,
    correlationId?: string,
  ): Promise<ScoreSimulationResult> {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });
    return this.request<ScoreSimulationResult>(`/scores/simulate?${params}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request),
      correlationId,
      accessToken,
    });
  }

  // --- Executive Narrative ---

  async getExecutiveNarrative(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ): Promise<ExecutiveNarrative> {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });
    return this.request<ExecutiveNarrative>(
      `/narrative/${encodeURIComponent(serviceGroupId)}?${params}`,
      { correlationId, accessToken },
    );
  }

  // --- Governance Negotiation ---

  async negotiateGovernance(
    request: GovernanceNegotiationRequest,
    accessToken?: string,
    correlationId?: string,
  ): Promise<GovernanceNegotiationResult> {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });
    return this.request<GovernanceNegotiationResult>(
      `/governance/negotiate?${params}`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
        correlationId,
        accessToken,
      },
    );
  }

  // --- Sustainability / Carbon ---

  async getCarbonEmissions(
    serviceGroupId: string,
    accessToken?: string,
    correlationId?: string,
  ): Promise<CarbonEmissionsResponse> {
    const params = new URLSearchParams({ "api-version": "2025-02-16" });
    return this.request<CarbonEmissionsResponse>(
      `/sustainability/carbon/${encodeURIComponent(serviceGroupId)}?${params}`,
      { correlationId, accessToken },
    );
  }
}

export const controlPlaneApi = new ControlPlaneApiService();
