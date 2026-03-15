using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Atlas.AgentOrchestrator.Agents;
using Atlas.AgentOrchestrator.Contracts;
using Atlas.AgentOrchestrator.Integrations.Azure;
using Atlas.AgentOrchestrator.Integrations.MCP;
using Atlas.AgentOrchestrator.Integrations.Prompts;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// Multi-agent orchestration implemented with Microsoft Agent Framework workflows.
/// </summary>
public class MultiAgentOrchestrator
{
    private readonly ILogger<MultiAgentOrchestrator> _logger;
    private readonly IAzureAIFoundryClient? _aiFoundryClient;
    private readonly AzureCostManagementClient? _costClient;
    private readonly AzureCarbonClient? _carbonClient;
    private readonly ConcurrentMediatorOrchestrator? _concurrentMediator;
    private readonly MafGroundingSkill? _mafGroundingSkill;
    private readonly IPromptProvider? _promptProvider;
    private readonly Dictionary<string, AIAgent> _agents;
    private const string SessionContextKey = "analysisContext";
    private const string SessionPreviousResultsKey = "previousResults";
    private static readonly JsonSerializerOptions SessionSerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.MultiAgent");

    public MultiAgentOrchestrator(
        ILogger<MultiAgentOrchestrator> logger,
        ServiceIntelligenceAgent serviceIntelligenceAgent,
        BestPracticeEngine bestPracticeEngine,
        DriftDetectionAgent driftDetectionAgent,
        ServiceHierarchyAnalyzer serviceHierarchyAnalyzer,
        WellArchitectedAssessmentAgent wafAgent,
        CloudNativeMaturityAgent cloudNativeAgent,
        FinOpsOptimizerAgent finOpsAgent,
        ArchitectureAgent architectureAgent,
        ReliabilityAgent reliabilityAgent,
        SustainabilityAgent sustainabilityAgent,
        GovernanceNegotiationAgent governanceAgent,
        IAzureAIFoundryClient? aiFoundryClient = null,
        AzureCostManagementClient? costManagementClient = null,
        AzureCarbonClient? carbonClient = null,
        ConcurrentMediatorOrchestrator? concurrentMediator = null,
        MafGroundingSkill? mafGroundingSkill = null,
        IPromptProvider? promptProvider = null)
    {
        _logger = logger;
        _costClient = costManagementClient;
        _carbonClient = carbonClient;
        _aiFoundryClient = aiFoundryClient;
        _concurrentMediator = concurrentMediator;
        _mafGroundingSkill = mafGroundingSkill;
        _promptProvider = promptProvider;
        _agents = new Dictionary<string, AIAgent>
        {
            ["GroundingSkill"] = CreateDeterministicAgent(
                "maf-grounding-skill-agent",
                "MAF Grounding Skill",
                "Builds MCP-grounded context for downstream agents using Azure MCP and Learn MCP.",
                async (context, _, cancellationToken) => await BuildGroundingSkillResultAsync(context, cancellationToken)),
            ["ServiceIntelligence"] = CreateDeterministicAgent(
                "service-intelligence-agent",
                "Service Intelligence",
                "Calculates service-group intelligence scores.",
                (context, _, _) => Task.FromResult<object>(serviceIntelligenceAgent.CalculateScores(context.Snapshot))),
            ["BestPractice"] = CreateDeterministicAgent(
                "best-practice-agent",
                "Best Practice",
                "Evaluates best-practice rules against discovered resources.",
                async (context, _, cancellationToken) => await bestPracticeEngine.EvaluateAsync(context.Snapshot, cancellationToken)),
            ["DriftDetection"] = CreateDeterministicAgent(
                "drift-detection-agent",
                "Drift Detection",
                "Detects drift across service resources and best-practice violations.",
                async (context, _, cancellationToken) => await driftDetectionAgent.AnalyzeDriftAsync(context.Snapshot, null, cancellationToken)),
            ["ServiceHierarchy"] = CreateDeterministicAgent(
                "service-hierarchy-agent",
                "Service Hierarchy",
                "Analyzes parent-child service relationships, cascades recommendations, and identifies cross-cutting concerns.",
                async (context, previousResults, cancellationToken) =>
                    await AdaptHierarchyAgentAsync(serviceHierarchyAnalyzer, context, previousResults, cancellationToken)),
            ["WellArchitected"] = CreateDeterministicAgent(
                "well-architected-agent",
                "Well-Architected",
                "Performs Azure Well-Architected pillar assessment.",
                async (context, previousResults, cancellationToken) => await AdaptWafAgentAsync(wafAgent, context, previousResults, cancellationToken)),
            ["CloudNative"] = CreateDeterministicAgent(
                "cloud-native-maturity-agent",
                "Cloud Native",
                "Assesses cloud-native maturity for the service group.",
                async (context, _, cancellationToken) => await cloudNativeAgent.AssessAsync(
                    new CloudNativeContext
                    {
                        ServiceGroupId = context.ServiceGroupId,
                        Resources = MapToCloudNativeResources(context.Snapshot.ResourceInventory)
                    },
                    cancellationToken)),
            ["FinOps"] = CreateDeterministicAgent(
                "finops-optimizer-agent",
                "FinOps",
                "Generates cost optimization findings and recommendations.",
                async (context, _, cancellationToken) =>
                {
                    // Resolve real month-to-date spend from the first subscription in scope.
                    // Falls back to 0 if Cost Management Reader role is not assigned.
                    var firstSubscription = context.Snapshot.ResourceInventory is not null
                        ? TryExtractFirstSubscriptionId(context.Snapshot.ResourceInventory)
                        : null;
                    var currentCost = firstSubscription is not null && _costClient is not null
                        ? await _costClient.GetMonthToDateCostAsync(firstSubscription, cancellationToken)
                        : 0m;
                    return await finOpsAgent.AnalyzeAsync(
                        new FinOpsContext
                        {
                            ServiceGroupId = context.ServiceGroupId,
                            SubscriptionId = firstSubscription,
                            CurrentMonthlyCost = currentCost,
                            Resources = MapToFinOpsResources(context.Snapshot.ResourceInventory),
                            HistoricalCosts = new List<DailyCost>(),
                            McpContext = new ToolCallContext
                            {
                                AnalysisRunId = context.AnalysisRunId,
                                ServiceGroupId = context.ServiceGroupId,
                                CorrelationId = context.CorrelationId,
                                ActorId = "finops-optimizer-agent",
                                TraceId = Activity.Current?.TraceId.ToString(),
                                SpanId = Activity.Current?.SpanId.ToString(),
                                TraceParent = Activity.Current?.Id
                            }
                        },
                        cancellationToken);
                }),
            ["Architecture"] = CreateDeterministicAgent(
                "architecture-agent",
                "Architecture",
                "Evaluates architecture maturity and technical design patterns.",
                async (context, _, cancellationToken) => await architectureAgent.AnalyzeArchitectureAsync(
                    ExtractServiceGraphContext(context),
                    cancellationToken)),
            ["Reliability"] = CreateDeterministicAgent(
                "reliability-agent",
                "Reliability",
                "Evaluates system reliability, availability, and resilience patterns.",
                async (context, _, cancellationToken) => await reliabilityAgent.AnalyzeReliabilityAsync(
                    ExtractServiceGraphContext(context),
                    new ReliabilityContext(),
                    cancellationToken)),
            ["Sustainability"] = CreateDeterministicAgent(
                "sustainability-agent",
                "Sustainability",
                "Evaluates environmental impact and carbon efficiency.",
                async (context, _, cancellationToken) =>
                {
                    // Populate sustainability context with real Azure Carbon API data when available
                    var sustainabilityContext = new SustainabilityContext();

                    if (_carbonClient is not null)
                    {
                        var subscriptionIds = context.Snapshot.ResourceInventory is not null
                            ? ExtractSubscriptionIds(context.Snapshot.ResourceInventory)
                            : Array.Empty<string>();

                        if (subscriptionIds.Count > 0)
                        {
                            var carbonData = await _carbonClient.GetMonthlyCarbonEmissionsAsync(
                                subscriptionIds, cancellationToken);

                            if (carbonData.HasRealData)
                            {
                                sustainabilityContext.MonthlyCarbonKg = carbonData.TotalEmissionsKg;
                                sustainabilityContext.HasCarbonIntensityData = true;
                                sustainabilityContext.RegionEmissions = carbonData.RegionEmissions;
                            }
                        }
                    }

                    return await sustainabilityAgent.AnalyzeSustainabilityAsync(
                        ExtractServiceGraphContext(context),
                        sustainabilityContext,
                        cancellationToken);
                }),
            ["Governance"] = CreateDeterministicAgent(
                "governance-negotiation-agent",
                "Governance",
                "Negotiates governance conflicts and produces conflict resolution outcomes.",
                async (context, previousResults, cancellationToken) =>
                    await AdaptGovernanceAgentAsync(governanceAgent, context, previousResults, cancellationToken))
        };
    }

    private static AIAgent CreateDeterministicAgent(
        string id,
        string name,
        string description,
        Func<AnalysisContext, Dictionary<string, object>, CancellationToken, Task<object>> executeAsync)
    {
        return new DeterministicAnalysisAIAgent(id, name, description, executeAsync);
    }

    public async Task<AgentCollaborationResult> OrchestrateAnalysisAsync(
        AnalysisContext context,
        CollaborationProtocol protocol = CollaborationProtocol.ConcurrentMediator,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("MultiAgent.OrchestrateAnalysis");
        activity?.SetTag("collaboration.protocol", protocol.ToString());
        activity?.SetTag("collaboration.serviceGroupId", context.ServiceGroupId);

        var session = new AgentCollaborationSession
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid().ToString(),
            Protocol = protocol,
            AnalysisRunId = context.AnalysisRunId,
            SessionType = "analysis",
            PrimaryAgent = DeterminePrimaryAgent(context),
            ParticipatingAgents = _agents.Keys.ToList(),
            Status = CollaborationSessionStatus.Active,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            // Concurrent+Mediator: run sequential pipeline then fan-out governance evaluation
            if (protocol == CollaborationProtocol.ConcurrentMediator && _concurrentMediator is not null)
            {
                return await OrchestrateWithConcurrentMediationAsync(context, session, cancellationToken);
            }

            var executionOrder = ResolveExecutionOrder(protocol);
            var includePriorResults = true;
            var executionState = new WorkflowExecutionState(context, includePriorResults);

            var executorBindings = new List<ExecutorBinding>();
            foreach (var agentName in executionOrder)
            {
                var executor = new WorkflowAgentExecutor(
                    $"executor-{agentName}",
                    agentName,
                    _agents[agentName],
                    _logger);
                executorBindings.Add(executor);
            }

            WorkflowBuilder builder = new(executorBindings[0]);
            builder.WithName($"nimbusiq-{protocol.ToString().ToLowerInvariant()}");
            builder.WithDescription("NimbusIQ multi-agent orchestration workflow powered by Microsoft Agent Framework.");

            for (var index = 0; index < executorBindings.Count - 1; index++)
            {
                builder.AddEdge(executorBindings[index], executorBindings[index + 1]);
            }

            builder.WithOutputFrom(executorBindings[^1]);
            var workflow = builder.Build(validateOrphans: true);

            await using Run run = await InProcessExecution.RunAsync(
                workflow,
                executionState,
                session.SessionId,
                cancellationToken);

            var runStatus = await run.GetStatusAsync(cancellationToken);
            session.Status = runStatus == RunStatus.Ended ? CollaborationSessionStatus.Completed : CollaborationSessionStatus.Halted;
            session.CompletedAt = DateTime.UtcNow;

            if (runStatus != RunStatus.Ended)
            {
                _logger.LogWarning(
                    "Workflow session {SessionId} halted with status {RunStatus}",
                    session.Id,
                    runStatus);
            }

            var outcome = await SynthesizeOutcomeAsync(executionState.AgentResults, executionState.Messages, cancellationToken);
            session.Outcome = JsonSerializer.Serialize(outcome);

            var result = new AgentCollaborationResult
            {
                Session = session,
                Messages = executionState.Messages,
                AgentResults = executionState.AgentResults,
                FinalOutcome = outcome,
                DurationMs = (session.CompletedAt - session.StartedAt)?.TotalMilliseconds ?? 0
            };

            activity?.SetTag("collaboration.status", session.Status);
            activity?.SetTag("collaboration.durationMs", result.DurationMs);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Multi-agent collaboration session {SessionId} failed", session.Id);
            session.Status = CollaborationSessionStatus.Failed;
            session.CompletedAt = DateTime.UtcNow;
            session.Outcome = JsonSerializer.Serialize(new { error = ex.Message });
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Runs the sequential pipeline (discovery → evaluation agents) then delegates
    /// governance to the ConcurrentMediatorOrchestrator for parallel fan-out/fan-in.
    /// </summary>
    private async Task<AgentCollaborationResult> OrchestrateWithConcurrentMediationAsync(
        AnalysisContext context,
        AgentCollaborationSession session,
        CancellationToken cancellationToken)
    {
        // Phase 1: run non-governance agents sequentially (same as before)
        var sequentialAgents = new List<string>
        {
            "GroundingSkill",
            "ServiceIntelligence", "BestPractice", "DriftDetection", "ServiceHierarchy",
            "WellArchitected", "CloudNative", "FinOps", "Architecture", "Reliability", "Sustainability"
        };

        var executionState = new WorkflowExecutionState(context, includePriorResults: true);

        var executorBindings = new List<ExecutorBinding>();
        foreach (var agentName in sequentialAgents)
        {
            var executor = new WorkflowAgentExecutor(
                $"executor-{agentName}",
                agentName,
                _agents[agentName],
                _logger);
            executorBindings.Add(executor);
        }

        WorkflowBuilder builder = new(executorBindings[0]);
        builder.WithName("nimbusiq-concurrent-mediator-phase1");
        builder.WithDescription("Sequential evaluation phase before concurrent governance mediation.");

        for (var index = 0; index < executorBindings.Count - 1; index++)
        {
            builder.AddEdge(executorBindings[index], executorBindings[index + 1]);
        }

        builder.WithOutputFrom(executorBindings[^1]);
        var workflow = builder.Build(validateOrphans: true);

        await using Run run = await InProcessExecution.RunAsync(
            workflow,
            executionState,
            session.SessionId,
            cancellationToken);

        var runStatus = await run.GetStatusAsync(cancellationToken);

        // Phase 2: concurrent governance mediation on the collected results
        _logger.LogInformation("Sequential phase complete; starting concurrent governance mediation");

        var grounding = executionState.AgentResults.GetValueOrDefault("GroundingSkill") as MafGroundingResult;
        var mediationConstraint = grounding?.SuggestedConstraint ?? "optimize_within_policy";
        var mediationObjectives = grounding?.SuggestedObjectives is { Count: > 0 }
            ? grounding.SuggestedObjectives
            : ["reduce_cost", "maintain_sla", "enforce_security"];

        var mediationResult = await _concurrentMediator!.EvaluateAndMediateAsync(
            constraint: mediationConstraint,
            objectives: mediationObjectives,
            agentResults: executionState.AgentResults,
            context: context,
            cancellationToken: cancellationToken);

        var mediationOutcome = mediationResult.Outcome;
        executionState.Messages.AddRange(mediationResult.Messages.Select(message => ToAgentMessage(context, message)));

        // Merge mediation outcome into agent results
        executionState.AgentResults["GovernanceMediation"] = mediationOutcome;

        session.Status = runStatus == RunStatus.Ended ? CollaborationSessionStatus.Completed : CollaborationSessionStatus.Halted;
        session.CompletedAt = DateTime.UtcNow;
        // Protocol enum on session already reflects ConcurrentMediator from initialization.

        executionState.Messages.Add(new AgentMessage
        {
            Id = Guid.NewGuid(),
            AnalysisRunId = context.AnalysisRunId,
            FromAgent = "MultiAgentOrchestrator",
            ToAgent = "orchestrator",
            AgentName = "GovernanceMediator",
            MessageType = "result",
            Content = mediationOutcome,
            Metadata = JsonSerializer.Serialize(new
            {
                toolName = "mediateGovernance",
                protocol = CollaborationProtocol.ConcurrentMediator.ToString(),
                requiresDualApproval = mediationOutcome.RequiresDualApproval
            }),
            Timestamp = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });

        var outcome = await SynthesizeOutcomeAsync(executionState.AgentResults, executionState.Messages, cancellationToken);
        session.Outcome = JsonSerializer.Serialize(outcome);

        return new AgentCollaborationResult
        {
            Session = session,
            Messages = executionState.Messages,
            AgentResults = executionState.AgentResults,
            FinalOutcome = outcome,
            DurationMs = (session.CompletedAt - session.StartedAt)?.TotalMilliseconds ?? 0
        };
    }

    private List<string> ResolveExecutionOrder(CollaborationProtocol protocol)
    {
        return protocol switch
        {
            CollaborationProtocol.Sequential or CollaborationProtocol.Leader => new List<string>
            {
                "GroundingSkill",
                "ServiceIntelligence",
                "BestPractice",
                "DriftDetection",
                "ServiceHierarchy",
                "WellArchitected",
                "CloudNative",
                "FinOps",
                "Architecture",
                "Reliability",
                "Sustainability",
                "Governance"
            },
            _ => _agents.Keys.OrderBy(static key => key).ToList()
        };
    }

    private static string GetToolNameForAgent(string agentName)
    {
        return agentName switch
        {
            "ServiceIntelligence" => "scoreServiceIntelligence",
            "GroundingSkill" => "buildGroundingContext",
            "BestPractice" => "evaluateCompliance",
            "DriftDetection" => "assessDrift",
            "ServiceHierarchy" => "analyzeHierarchy",
            "WellArchitected" => "scorePillars",
            "CloudNative" => "assessMaturity",
            "FinOps" => "analyzeCosts",
            "Architecture" => "analyzeArchitecture",
            "Reliability" => "analyzeReliability",
            "Sustainability" => "analyzeSustainability",
            "Governance" => "mediateGovernance",
            _ => agentName
        };
    }

    private static AgentMessage ToAgentMessage(AnalysisContext context, A2AMessage message)
    {
        return new AgentMessage
        {
            Id = Guid.NewGuid(),
            AnalysisRunId = context.AnalysisRunId,
            FromAgent = message.SenderAgent,
            ToAgent = message.RecipientAgent ?? "broadcast",
            AgentName = message.SenderAgent,
            MessageType = $"a2a.{message.MessageType}",
            Content = message,
            Metadata = JsonSerializer.Serialize(new
            {
                correlationId = message.CorrelationId,
                priority = message.Priority,
                ttlSeconds = message.TtlSeconds,
                traceId = message.Lineage.TraceId,
                spanId = message.Lineage.SpanId
            }),
            Timestamp = message.Timestamp.UtcDateTime,
            CreatedAt = message.Timestamp.UtcDateTime
        };
    }

    private async Task<object> SynthesizeOutcomeAsync(
        Dictionary<string, object> agentResults,
        List<AgentMessage> messages,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        var consolidatedScore = CalculateConsolidatedScore(agentResults);
        var keyFindings = ExtractKeyFindings(agentResults);
        var conflicts = IdentifyConflicts(agentResults);

        string? aiNarrative = null;
        if (_aiFoundryClient != null)
        {
            try
            {
                var prompt = BuildSynthesisPrompt(agentResults, consolidatedScore);
                aiNarrative = await _aiFoundryClient.SendPromptAsync(prompt, cancellationToken);
                _logger.LogInformation("AI synthesis narrative generated for analysis run.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI narrative synthesis failed; proceeding with deterministic outcome only.");
            }
        }

        return new
        {
            ParticipatingAgents = agentResults.Keys.ToList(),
            SuccessfulAgents = messages.Count(m => m.MessageType == "result"),
            FailedAgents = messages.Count(m => m.MessageType == "error"),
            KeyFindings = keyFindings,
            ConflictingRecommendations = conflicts,
            ConsolidatedScore = consolidatedScore,
            AINarrative = aiNarrative,
            ExecutionMetrics = new
            {
                TotalAgents = agentResults.Count,
                TotalMessages = messages.Count,
                AvgDurationMs = messages
                    .Select(m => JsonSerializer.Deserialize<Dictionary<string, object>>(m.Metadata ?? "{}"))
                    .Select(ExtractDurationMs)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .DefaultIfEmpty(0)
                    .Average()
            }
        };
    }

    private static double? ExtractDurationMs(Dictionary<string, object>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("durationMs", out var raw) || raw is null)
        {
            return null;
        }

        if (raw is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Number && json.TryGetDouble(out var number))
            {
                return number;
            }

            if (json.ValueKind == JsonValueKind.String &&
                double.TryParse(
                    json.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }

            return null;
        }

        if (raw is IConvertible)
        {
            try
            {
                return Convert.ToDouble(raw);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static List<string> ExtractKeyFindings(Dictionary<string, object> agentResults)
    {
        var findings = new List<string>();
        foreach (var (agentName, _) in agentResults)
        {
            findings.Add($"{agentName} analysis completed successfully");
        }

        return findings;
    }

    private static List<object> IdentifyConflicts(Dictionary<string, object> agentResults)
    {
        var conflicts = new List<object>();

        // Collect all recommendations from agents that have AgentAnalysisResult
        var allRecommendations = agentResults
            .Where(kv => kv.Value is AgentAnalysisResult)
            .SelectMany(kv => ((AgentAnalysisResult)kv.Value).Recommendations
                .Select(r => new { AgentName = kv.Key, Recommendation = r }))
            .ToList();

        // Detect conflicting actions on the same resource
        var grouped = allRecommendations
            .Where(r => r.Recommendation.AffectedResource != null)
            .GroupBy(r => r.Recommendation.AffectedResource);

        foreach (var group in grouped)
        {
            var items = group.ToList();
            if (items.Count < 2)
                continue;

            var scaleUpAgents = items
                .Where(i => i.Recommendation.Action?.Contains("scale-up", StringComparison.OrdinalIgnoreCase) == true
                         || i.Recommendation.Action?.Contains("ScaleUp", StringComparison.OrdinalIgnoreCase) == true
                         || i.Recommendation.Action?.Contains("increase", StringComparison.OrdinalIgnoreCase) == true)
                .Select(i => i.AgentName)
                .ToList();

            var scaleDownAgents = items
                .Where(i => i.Recommendation.Action?.Contains("scale-down", StringComparison.OrdinalIgnoreCase) == true
                         || i.Recommendation.Action?.Contains("ScaleDown", StringComparison.OrdinalIgnoreCase) == true
                         || i.Recommendation.Action?.Contains("decrease", StringComparison.OrdinalIgnoreCase) == true
                         || i.Recommendation.Action?.Contains("reduce", StringComparison.OrdinalIgnoreCase) == true
                         || i.Recommendation.Action?.Contains("rightsize", StringComparison.OrdinalIgnoreCase) == true)
                .Select(i => i.AgentName)
                .ToList();

            if (scaleUpAgents.Count > 0 && scaleDownAgents.Count > 0)
            {
                conflicts.Add(new
                {
                    Resource = group.Key,
                    ConflictType = "scale-direction",
                    Description = $"Agents recommend conflicting scaling actions for {group.Key}",
                    ScaleUpAgents = scaleUpAgents,
                    ScaleDownAgents = scaleDownAgents
                });
            }
        }

        return conflicts;
    }

    private static decimal CalculateConsolidatedScore(Dictionary<string, object> agentResults)
    {
        // Weighted average across conceptual dimensions:
        // - Architecture 30%
        // - Reliability 30%  (split evenly across BestPractice + WellArchitected)
        // - FinOps 20%
        // - Sustainability 20% (CloudNative proxy)
        // Map agent names to weights (best-effort — unmapped agents contribute to unweighted average)
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["Architecture"] = 0.25,
            ["BestPractice"] = 0.10,   // Reliability proxy
            ["WellArchitected"] = 0.10, // Reliability proxy
            ["Reliability"] = 0.10,
            ["FinOps"] = 0.20,
            ["CloudNative"] = 0.10,
            ["Sustainability"] = 0.10,
            ["DriftDetection"] = 0.00, // Status indicator, not a quality score
            ["Governance"] = 0.05,
        };

        double weightedSum = 0;
        double totalWeight = 0;
        var unweightedScores = new List<double>();

        foreach (var (agentName, result) in agentResults)
        {
            double score;
            if (result is AgentAnalysisResult analysisResult)
            {
                score = analysisResult.Score;
            }
            else
            {
                continue;
            }

            if (weights.TryGetValue(agentName, out var weight) && weight > 0)
            {
                weightedSum += score * weight;
                totalWeight += weight;
            }
            else
            {
                unweightedScores.Add(score);
            }
        }

        // If we have weighted scores, use them; otherwise fall back to simple average
        if (totalWeight > 0)
        {
            var weightedAvg = weightedSum / totalWeight;
            if (unweightedScores.Count > 0)
            {
                var unweightedAvg = unweightedScores.Average();
                return (decimal)Math.Round((weightedAvg * 0.8) + (unweightedAvg * 0.2), 2);
            }
            return (decimal)Math.Round(weightedAvg, 2);
        }

        if (unweightedScores.Count > 0)
            return (decimal)Math.Round(unweightedScores.Average(), 2);

        return 75.0m;
    }

    private static string DeterminePrimaryAgent(AnalysisContext context)
    {
        _ = context;
        return "ServiceIntelligence";
    }

    private string BuildSynthesisPrompt(Dictionary<string, object> agentResults, decimal consolidatedScore)
    {
        var agentNames = string.Join(", ", agentResults.Keys);
        var agentCount = agentResults.Count;
        _logger.LogDebug("Building AI synthesis prompt for {AgentCount} agents.", agentCount);

        if (_promptProvider is null)
        {
            throw new InvalidOperationException("Prompt provider is required for synthesis summary prompt rendering.");
        }

        return _promptProvider.Render(
            "synthesis-summary",
            new Dictionary<string, string>
            {
                ["AgentCount"] = agentCount.ToString(),
                ["AgentNames"] = agentNames,
                ["ConsolidatedScore"] = consolidatedScore.ToString("F1")
            });
    }

    private static ServiceGraphContext ExtractServiceGraphContext(AnalysisContext context)
    {
        if (!context.Metadata.TryGetValue("serviceGraphContext", out var raw))
            return new ServiceGraphContext { ServiceGroupId = context.ServiceGroupId };

        string? json = raw switch
        {
            string s => s,
            System.Text.Json.JsonElement el when el.ValueKind == System.Text.Json.JsonValueKind.String => el.GetString(),
            System.Text.Json.JsonElement el => el.GetRawText(),
            _ => null
        };

        if (json is null) return new ServiceGraphContext { ServiceGroupId = context.ServiceGroupId };

        try
        {
            return JsonSerializer.Deserialize<ServiceGraphContext>(json, SessionSerializerOptions)
                ?? new ServiceGraphContext { ServiceGroupId = context.ServiceGroupId };
        }
        catch
        {
            return new ServiceGraphContext { ServiceGroupId = context.ServiceGroupId };
        }
    }

    private static List<CloudNativeResourceInfo> MapToCloudNativeResources(string? resourceInventoryJson)
    {
        if (string.IsNullOrEmpty(resourceInventoryJson)) return new List<CloudNativeResourceInfo>();

        try
        {
            var items = JsonSerializer.Deserialize<List<ResourceInventoryItem>>(resourceInventoryJson, SessionSerializerOptions);
            return items?.ConvertAll(r => new CloudNativeResourceInfo
            {
                ResourceId = r.AzureResourceId ?? r.Id ?? string.Empty,
                ResourceType = r.ResourceType ?? string.Empty,
                ResourceName = r.ResourceName ?? string.Empty
            }) ?? new List<CloudNativeResourceInfo>();
        }
        catch
        {
            return new List<CloudNativeResourceInfo>();
        }
    }

    private static List<FinOpsResourceInfo> MapToFinOpsResources(string? resourceInventoryJson)
    {
        if (string.IsNullOrEmpty(resourceInventoryJson)) return new List<FinOpsResourceInfo>();

        try
        {
            var items = JsonSerializer.Deserialize<List<ResourceInventoryItem>>(resourceInventoryJson, SessionSerializerOptions);
            return items?.ConvertAll(r => new FinOpsResourceInfo
            {
                ResourceId = r.AzureResourceId ?? r.Id ?? string.Empty,
                ResourceType = r.ResourceType ?? string.Empty,
                Sku = r.Sku ?? string.Empty
            }) ?? new List<FinOpsResourceInfo>();
        }
        catch
        {
            return new List<FinOpsResourceInfo>();
        }
    }

    /// <summary>
    /// Extracts the first Azure subscription ID from the resource inventory JSON.
    /// Used to scope the Cost Management query for month-to-date spend.
    /// </summary>
    private static string? TryExtractFirstSubscriptionId(string resourceInventoryJson)
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<ResourceInventoryItem>>(resourceInventoryJson, SessionSerializerOptions);
            var firstId = items?
                .Select(static r => r.AzureResourceId ?? r.ArmId ?? r.Id)
                .FirstOrDefault(static id => !string.IsNullOrWhiteSpace(id));
            if (firstId is null) return null;

            // ARM resource IDs: /subscriptions/{subId}/resourceGroups/...
            var parts = firstId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var subIndex = Array.FindIndex(parts, p => p.Equals("subscriptions", StringComparison.OrdinalIgnoreCase));
            return subIndex >= 0 && subIndex + 1 < parts.Length ? parts[subIndex + 1] : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ExtractSubscriptionIds(string resourceInventoryJson)
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<ResourceInventoryItem>>(resourceInventoryJson, SessionSerializerOptions);
            if (items is null) return Array.Empty<string>();

            var subscriptionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var resourceId = item.AzureResourceId ?? item.ArmId ?? item.Id;
                if (string.IsNullOrWhiteSpace(resourceId)) continue;

                var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var subIndex = Array.FindIndex(parts, p => p.Equals("subscriptions", StringComparison.OrdinalIgnoreCase));
                if (subIndex >= 0 && subIndex + 1 < parts.Length)
                    subscriptionIds.Add(parts[subIndex + 1]);
            }

            return subscriptionIds.ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<object> BuildGroundingSkillResultAsync(
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        if (_mafGroundingSkill is null)
        {
            return new MafGroundingResult
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                SuggestedConstraint = "optimize_within_policy",
                SuggestedObjectives = ["reduce_cost", "maintain_sla", "enforce_security"],
                Warnings = ["MAF grounding skill not configured."]
            };
        }

        var firstSubscription = context.Snapshot.ResourceInventory is not null
            ? TryExtractFirstSubscriptionId(context.Snapshot.ResourceInventory)
            : null;

        var resourceTypes = MapToCloudNativeResources(context.Snapshot.ResourceInventory)
            .Select(static resource => resource.ResourceType)
            .Where(static resourceType => !string.IsNullOrWhiteSpace(resourceType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        var constraint = context.Metadata.TryGetValue("constraint", out var rawConstraint)
            ? rawConstraint?.ToString()
            : null;

        var request = new MafGroundingRequest
        {
            ServiceGroupId = context.ServiceGroupId,
            SubscriptionId = firstSubscription,
            Constraint = constraint,
            ResourceTypes = resourceTypes,
            ToolCallContext = new ToolCallContext
            {
                AnalysisRunId = context.AnalysisRunId,
                ServiceGroupId = context.ServiceGroupId,
                CorrelationId = context.CorrelationId,
                ActorId = "maf-grounding-skill-agent",
                TraceId = Activity.Current?.TraceId.ToString(),
                SpanId = Activity.Current?.SpanId.ToString(),
                TraceParent = Activity.Current?.Id
            }
        };

        return await _mafGroundingSkill.BuildGroundingContextAsync(
            request,
            requestingAgentId: "GroundingSkill",
            cancellationToken: cancellationToken);
    }

    private static async Task<object> AdaptGovernanceAgentAsync(
        GovernanceNegotiationAgent governanceAgent,
        AnalysisContext context,
        Dictionary<string, object> previousResults,
        CancellationToken cancellationToken)
    {
        _ = context;

        var finOpsResult = previousResults.GetValueOrDefault("FinOps") as AgentAnalysisResult;
        var archResult = previousResults.GetValueOrDefault("Architecture") as AgentAnalysisResult;

        // Derive a cost proxy from FinOps score: lower score = higher optimisation potential
        var finOpsScore = finOpsResult?.Score ?? 75.0;
        var archScore = archResult?.Score ?? 75.0;
        var estimatedMonthlyCost = (decimal)Math.Round((100.0 - finOpsScore) * 50, 2);
        var actionType = finOpsResult?.Recommendations.FirstOrDefault()?.Category ?? "cost_optimization";

        var proposal = new RecommendationProposal
        {
            Id = Guid.NewGuid(),
            ActionType = actionType,
            EstimatedMonthlyCost = estimatedMonthlyCost,
            RequiredSla = (decimal)(archScore < 60 ? 99.5 : 99.9),
            TargetRegion = "uksouth"
        };

        var constraints = new PolicyConstraints
        {
            MaxMonthlyCost = 10_000m,
            MaxSla = 99.99m,
            RequiredDataResidency = "UK"
        };

        var outcome = await governanceAgent.NegotiateAsync(proposal, constraints, cancellationToken);

        var score = outcome.Status switch
        {
            "approved" => 90.0,
            "approved_with_conditions" => 70.0,
            "escalated" => 55.0,
            "blocked" => 30.0,
            _ => 50.0
        };

        return new AgentAnalysisResult
        {
            Score = score,
            Confidence = 0.8,
            Findings = outcome.MediationResults.Select(m => new Finding
            {
                Severity = m.RequiresEscalation ? "high" : "low",
                Category = "governance",
                Description = m.Conflict?.PolicyRule ?? "Policy constraint evaluation",
                Impact = m.Severity
            }).ToList(),
            Recommendations = outcome.MediationResults
                .Where(m => !string.IsNullOrWhiteSpace(m.Resolution))
                .Select(m => new Recommendation
                {
                    Priority = "high",
                    Category = "governance",
                    Title = "Governance Resolution",
                    Description = m.Resolution,
                    EstimatedEffort = "low"
                }).ToList(),
            EvidenceReferences = new List<string>
            {
                $"outcome_status:{outcome.Status}",
                $"proposal_id:{proposal.Id}",
                $"mediation_count:{outcome.MediationResults.Count}"
            }
        };
    }

    private static async Task<object> AdaptHierarchyAgentAsync(
        ServiceHierarchyAnalyzer analyzer,
        AnalysisContext context,
        Dictionary<string, object> previousResults,
        CancellationToken cancellationToken)
    {
        // Build DriftScoreInfo from prior DriftDetection result if available
        DriftScoreInfo? driftScore = null;
        if (previousResults.TryGetValue("DriftDetection", out var rawDrift) &&
            rawDrift is DriftAnalysisResult driftResult)
        {
            driftScore = new DriftScoreInfo
            {
                ServiceGroupId = driftResult.ServiceGroupId,
                CriticalViolations = driftResult.CriticalViolations,
                HighViolations = driftResult.HighViolations,
                DriftScore = driftResult.DriftScore,
                ViolationsByCategory = driftResult.CategoryBreakdown
            };
        }

        // Build ServiceAssessment from prior ServiceIntelligence result if available
        ServiceAssessment? assessment = null;
        if (previousResults.TryGetValue("ServiceIntelligence", out var rawIntel) &&
            rawIntel is ServiceGroupAssessment sgAssessment)
        {
            assessment = new ServiceAssessment
            {
                ServiceGroupId = sgAssessment.ServiceGroupId,
                ArchitectureScore = sgAssessment.Architecture?.Score ?? 0,
                FinOpsScore = sgAssessment.FinOps?.Score ?? 0,
                ReliabilityScore = sgAssessment.Reliability?.Score ?? 0,
                SustainabilityScore = sgAssessment.Sustainability?.Score ?? 0,
                ResourceCount = context.Snapshot.ResourceCount
            };
        }

        var hierarchyContext = new ServiceGroupHierarchyContext
        {
            RootServiceGroupId = context.ServiceGroupId,
            MaxDepth = 1,
            ServiceGroups = new List<ServiceGroupInfo>
            {
                new ServiceGroupInfo
                {
                    Id = context.ServiceGroupId,
                    Name = context.ServiceGroupId.ToString(),
                    ParentServiceGroupId = null,
                    HierarchyLevel = 0
                }
            },
            Assessments = assessment is not null ? new List<ServiceAssessment> { assessment } : new List<ServiceAssessment>(),
            DriftScores = driftScore is not null ? new List<DriftScoreInfo> { driftScore } : new List<DriftScoreInfo>()
        };

        return await analyzer.AnalyzeHierarchyAsync(hierarchyContext, cancellationToken);
    }

    private static async Task<object> AdaptWafAgentAsync(
        WellArchitectedAssessmentAgent waa,
        AnalysisContext context,
        Dictionary<string, object> previousResults,
        CancellationToken cancellationToken)
    {
        _ = previousResults;
        Dictionary<string, decimal>? scores = null;
        return await waa.ConductAssessmentAsync(context.Snapshot, scores, cancellationToken);
    }

    private sealed class WorkflowExecutionState
    {
        public WorkflowExecutionState(AnalysisContext context, bool includePriorResults)
        {
            Context = context;
            IncludePriorResults = includePriorResults;
        }

        public AnalysisContext Context { get; }

        public bool IncludePriorResults { get; }

        public Dictionary<string, object> AgentResults { get; } = new();

        public List<AgentMessage> Messages { get; } = new();
    }

    private sealed class WorkflowAgentExecutor : Executor<WorkflowExecutionState, WorkflowExecutionState>
    {
        private readonly string _agentName;
        private readonly AIAgent _agent;
        private readonly ILogger _logger;

        public WorkflowAgentExecutor(string id, string agentName, AIAgent agent, ILogger logger)
            : base(id)
        {
            _agentName = agentName;
            _agent = agent;
            _logger = logger;
        }

        public override async ValueTask<WorkflowExecutionState> HandleAsync(
            WorkflowExecutionState message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var startedAt = DateTime.UtcNow;

            try
            {
                var previousResults = message.IncludePriorResults
                    ? new Dictionary<string, object>(message.AgentResults)
                    : new Dictionary<string, object>();

                var agentSession = await _agent.CreateSessionAsync(cancellationToken);
                var stateBag = agentSession.StateBag
                    ?? throw new InvalidOperationException("Agent session state bag was not initialized.");

                stateBag.SetValue(SessionContextKey, message.Context, SessionSerializerOptions);
                stateBag.SetValue(SessionPreviousResultsKey, previousResults, SessionSerializerOptions);

                message.Messages.Add(new AgentMessage
                {
                    Id = Guid.NewGuid(),
                    AnalysisRunId = message.Context.AnalysisRunId,
                    FromAgent = _agentName,
                    ToAgent = "orchestrator",
                    AgentName = _agentName,
                    MessageType = "agent.started",
                    Content = new
                    {
                        agentName = _agentName,
                        toolName = GetToolNameForAgent(_agentName),
                        analysisRunId = message.Context.AnalysisRunId,
                        serviceGroupId = message.Context.ServiceGroupId
                    },
                    Metadata = JsonSerializer.Serialize(new
                    {
                        toolName = GetToolNameForAgent(_agentName),
                        workflowExecutor = Id
                    }),
                    Timestamp = startedAt,
                    CreatedAt = startedAt
                });

                var prompt = $"Analyze service group {message.Context.ServiceGroupId:D} for run {message.Context.AnalysisRunId:D}.";
                var agentResponse = await _agent.RunAsync(prompt, agentSession, new AgentRunOptions(), cancellationToken);

                var result = agentResponse.RawRepresentation ?? (object)(agentResponse.Text ?? string.Empty);
                message.AgentResults[_agentName] = result;

                message.Messages.Add(new AgentMessage
                {
                    Id = Guid.NewGuid(),
                    AnalysisRunId = message.Context.AnalysisRunId,
                    FromAgent = _agentName,
                    ToAgent = "orchestrator",
                    AgentName = _agentName,
                    MessageType = "result",
                    Content = result,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        toolName = GetToolNameForAgent(_agentName),
                        durationMs = (DateTime.UtcNow - startedAt).TotalMilliseconds,
                        workflowExecutor = Id,
                        responseId = agentResponse.ResponseId
                    }),
                    Timestamp = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                });

                return message;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent {AgentName} execution failed", _agentName);

                message.Messages.Add(new AgentMessage
                {
                    Id = Guid.NewGuid(),
                    AnalysisRunId = message.Context.AnalysisRunId,
                    FromAgent = _agentName,
                    ToAgent = "orchestrator",
                    AgentName = _agentName,
                    MessageType = "error",
                    Content = ex.Message,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        toolName = GetToolNameForAgent(_agentName),
                        durationMs = (DateTime.UtcNow - startedAt).TotalMilliseconds,
                        workflowExecutor = Id,
                        error = true
                    }),
                    Timestamp = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                });

                return message;
            }
        }
    }

    private sealed class DeterministicAnalysisAIAgent : AIAgent
    {
        private readonly string _id;
        private readonly string _name;
        private readonly string _description;
        private readonly Func<AnalysisContext, Dictionary<string, object>, CancellationToken, Task<object>> _executeAsync;

        public DeterministicAnalysisAIAgent(
            string id,
            string name,
            string description,
            Func<AnalysisContext, Dictionary<string, object>, CancellationToken, Task<object>> executeAsync)
        {
            _id = id;
            _name = name;
            _description = description;
            _executeAsync = executeAsync;
        }

        public override string Name => _name;

        public override string Description => _description;

        protected override string IdCore => _id;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<AgentSession>(new DeterministicAgentSession(new AgentSessionStateBag()));
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? serializerOptions,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<AgentSession>(
                new DeterministicAgentSession(AgentSessionStateBag.Deserialize(sessionState)));
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession? session,
            JsonSerializerOptions? serializerOptions,
            CancellationToken cancellationToken)
        {
            var state = session?.StateBag ?? new AgentSessionStateBag();
            return ValueTask.FromResult(state.Serialize());
        }

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            _ = messages;
            _ = options;

            var stateBag = session?.StateBag
                ?? throw new InvalidOperationException("Agent session state bag was not initialized.");

            if (!stateBag.TryGetValue(SessionContextKey, out AnalysisContext? context, SessionSerializerOptions) || context is null)
            {
                throw new InvalidOperationException($"Session state '{SessionContextKey}' is missing.");
            }

            Dictionary<string, object>? previousResults;
            if (!stateBag.TryGetValue(SessionPreviousResultsKey, out previousResults, SessionSerializerOptions) || previousResults is null)
            {
                previousResults = new Dictionary<string, object>();
            }

            var result = await _executeAsync(context, previousResults, cancellationToken);
            var responseText = JsonSerializer.Serialize(result);

            return new AgentResponse(new ChatMessage(ChatRole.Assistant, responseText))
            {
                AgentId = Id,
                RawRepresentation = result
            };
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var response = await RunCoreAsync(messages, session, options, cancellationToken);
            foreach (var update in response.ToAgentResponseUpdates())
            {
                yield return update;
            }
        }

        private sealed class DeterministicAgentSession : AgentSession
        {
            public DeterministicAgentSession(AgentSessionStateBag stateBag)
                : base(stateBag)
            {
            }
        }
    }
}

public enum CollaborationProtocol
{
    Sequential,
    Leader,
    ConcurrentMediator
}

public class AnalysisContext
{
    public Guid ServiceGroupId { get; set; }
    public Guid AnalysisRunId { get; set; }
    public Guid CorrelationId { get; set; }
    public required DiscoverySnapshot Snapshot { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class AgentCollaborationSession
{
    public Guid Id { get; set; }
    public Guid AnalysisRunId { get; set; }
    public required string SessionId { get; set; }
    public required CollaborationProtocol Protocol { get; set; }
    public string SessionType { get; set; } = "analysis";
    public string? PrimaryAgent { get; set; }
    public List<string> ParticipatingAgents { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = CollaborationSessionStatus.Active;
    public string? Outcome { get; set; }
}

public class AgentMessage
{
    public Guid Id { get; set; }
    public Guid AnalysisRunId { get; set; }
    public required string FromAgent { get; set; }
    public required string ToAgent { get; set; }
    public string? AgentName { get; set; }
    public required string MessageType { get; set; }
    public required object Content { get; set; }
    public string? Metadata { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AgentCollaborationResult
{
    public required AgentCollaborationSession Session { get; set; }
    public required List<AgentMessage> Messages { get; set; }
    public required Dictionary<string, object> AgentResults { get; set; }
    public required object FinalOutcome { get; set; }
    public double DurationMs { get; set; }
}

// Minimal DTO for deserializing ResourceInventory JSON written by AnalysisOrchestrationService
file sealed class ResourceInventoryItem
{
    public string? Id { get; set; }
    public string? ArmId { get; set; }
    public string? AzureResourceId { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceName { get; set; }
    public string? Sku { get; set; }
    public string? Region { get; set; }
}
