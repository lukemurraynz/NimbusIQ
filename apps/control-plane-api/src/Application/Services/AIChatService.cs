using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.AI.Inference;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using MChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Provides GPT-4 powered chat responses via Azure AI Foundry and Microsoft Agent Framework.
/// Falls back to a structured summary when AI is not configured.
/// </summary>
public class AIChatService
{
    private readonly ILogger<AIChatService> _logger;
    private readonly AIChatOptions _options;
    private readonly Lazy<AIAgent>? _chatAgent;
    private readonly Lazy<AIAgent>? _chatAgentFallbackAudience;
    private readonly Lazy<AtlasChatAgent>? _rawChatAgent;
    private readonly Lazy<AtlasChatAgent>? _rawChatAgentFallback;
    private readonly ResiliencePipeline? _resiliencePipeline;
    private readonly bool _useManagedIdentityOnlyCredentialChain;
    private readonly string _primaryTokenScope;
    private readonly string? _fallbackTokenScope;
    private static readonly ActivitySource ActivitySource = new("Atlas.ControlPlane.AIChatService");

    public AIChatService(
        IOptions<AIChatOptions> options,
        ILogger<AIChatService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _useManagedIdentityOnlyCredentialChain =
            string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                "Production",
                StringComparison.OrdinalIgnoreCase);
        _primaryTokenScope =
            Environment.GetEnvironmentVariable("AzureAIFoundry__TokenScope")
            ?? "https://cognitiveservices.azure.com/.default";
        _fallbackTokenScope =
            string.Equals(_primaryTokenScope, "https://cognitiveservices.azure.com/.default", StringComparison.OrdinalIgnoreCase)
                ? "https://ai.azure.com/.default"
                : "https://cognitiveservices.azure.com/.default";
        var modelDeployment = ResolveModelDeployment(_options);

        if (!string.IsNullOrWhiteSpace(_options.ProjectEndpoint) &&
            !string.IsNullOrWhiteSpace(modelDeployment))
        {
            // Initialize resilience pipeline with retry + circuit breaker
            _resiliencePipeline = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    OnRetry = args =>
                    {
                        _logger.LogWarning(
                            args.Outcome.Exception,
                            "AI Foundry request failed (attempt {Attempt}/{MaxAttempts}). Retrying after {Delay}ms...",
                            args.AttemptNumber + 1,
                            3,
                            args.RetryDelay.TotalMilliseconds);
                        return ValueTask.CompletedTask;
                    }
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(60),
                    OnOpened = args =>
                    {
                        _logger.LogError(
                            args.Outcome.Exception,
                            "AI Foundry circuit breaker opened due to {FailureCount} failures. Requests will fail-fast for {BreakDuration}s.",
                            5,
                            60);
                        return ValueTask.CompletedTask;
                    },
                    OnClosed = args =>
                    {
                        _logger.LogInformation("AI Foundry circuit breaker closed. Requests will be attempted normally.");
                        return ValueTask.CompletedTask;
                    },
                    OnHalfOpened = args =>
                    {
                        _logger.LogInformation("AI Foundry circuit breaker half-open. Testing connectivity...");
                        return ValueTask.CompletedTask;
                    }
                })
                .Build();

            _rawChatAgent = new Lazy<AtlasChatAgent>(() => BuildRawAgent(modelDeployment!, _primaryTokenScope));
            _rawChatAgentFallback = new Lazy<AtlasChatAgent>(() => BuildRawAgent(modelDeployment!, _fallbackTokenScope));
            _chatAgent = new Lazy<AIAgent>(() => _rawChatAgent.Value
                .AsBuilder()
                .UseOpenTelemetry("Atlas.ControlPlane.AIChatService.Agent")
                .Build(null));
            _chatAgentFallbackAudience = new Lazy<AIAgent>(() => _rawChatAgentFallback.Value
                .AsBuilder()
                .UseOpenTelemetry("Atlas.ControlPlane.AIChatService.Agent")
                .Build(null));

            _logger.LogInformation(
                "AI Chat Service initialized with Azure AI Foundry. Endpoint: {Endpoint}, Model: {Model}, ManagedIdentityOnlyCredentialChain: {ManagedIdentityOnly}",
                _options.ProjectEndpoint,
                modelDeployment,
                _useManagedIdentityOnlyCredentialChain);
        }
        else
        {
            _logger.LogWarning(
                "AzureAIFoundry is not fully configured (ProjectEndpoint or ModelDeployment/DefaultModelDeployment missing). " +
                "Chat responses will use structured summaries instead of AI.");
        }
    }

    public bool IsAIAvailable => _chatAgent is not null;

    /// <summary>
    /// Gets the current circuit breaker state. Useful for health checks and monitoring.
    /// Returns null if AI is not configured or resilience pipeline not initialized.
    /// </summary>
    public CircuitState? CircuitBreakerState
    {
        get
        {
            if (_resiliencePipeline is null)
                return null;

            try
            {
                // Attempt to get circuit state from pipeline
                // Note: Polly v8 doesn't expose state directly, so we return null for now
                // In production, consider using Polly.Extensions.Telemetry for circuit state monitoring
                return null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Generates a chat response using GPT-4 via Azure AI Foundry + MAF.
    /// The system prompt grounds the model in infrastructure context from the database.
    /// </summary>
    public async Task<AIChatResponse> GenerateResponseAsync(
        string userQuestion,
        InfrastructureContext context,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AIChatService.GenerateResponse");
        activity?.SetTag("ai.available", IsAIAvailable);

        if (_chatAgent is null)
        {
            activity?.SetTag("result.source", "fallback");
            return new AIChatResponse(
                BuildFallbackResponse(userQuestion, context, aiConfigured: false),
                ConfidenceSource: "structured_summary");
        }

        try
        {
            var agent = _chatAgent.Value;

            // Execute AI call through resilience pipeline (retry + circuit breaker)
            var response = await _resiliencePipeline!.ExecuteAsync(async ct =>
            {
                try
                {
                    var session = await agent.CreateSessionAsync(ct);
                    var grounded = BuildGroundedPrompt(userQuestion, context);
                    return await agent.RunAsync(grounded, session, new AgentRunOptions(), ct);
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 401 && _chatAgentFallbackAudience is not null)
                {
                    _logger.LogWarning(
                        ex,
                        "Primary Foundry token audience failed with 401. Retrying once with fallback token scope {FallbackTokenScope}",
                        _fallbackTokenScope);

                    var fallbackAgent = _chatAgentFallbackAudience.Value;
                    var fallbackSession = await fallbackAgent.CreateSessionAsync(ct);
                    var grounded = BuildGroundedPrompt(userQuestion, context);
                    return await fallbackAgent.RunAsync(grounded, fallbackSession, new AgentRunOptions(), ct);
                }
            }, cancellationToken);

            activity?.SetTag("result.source", "ai_foundry");
            activity?.SetTag("response.id", response.ResponseId);

            _logger.LogInformation(
                "AI chat response generated via Azure AI Foundry. ResponseId: {ResponseId}",
                response.ResponseId);

            return new AIChatResponse(
                response.Text ?? BuildFallbackResponse(userQuestion, context, aiConfigured: true),
                ConfidenceSource: "ai_foundry");
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning(ex, "AI Foundry circuit breaker is open; falling back to structured summary.");
            activity?.SetTag("result.source", "fallback_circuit_open");

            return new AIChatResponse(
                BuildFallbackResponse(userQuestion, context, aiConfigured: true) +
                "\n\n*Azure AI Foundry is experiencing connectivity issues. The circuit breaker has temporarily disabled AI-powered responses. Retrying in " +
                $"{(ex.RetryAfter?.TotalSeconds ?? 60):F0} seconds...*",
                ConfidenceSource: "structured_summary_circuit_breaker");
        }
        catch (Azure.RequestFailedException ex)
        {
            _logger.LogError(
                ex,
                "Azure AI Foundry request failed; falling back to structured summary. Status={Status}, ErrorCode={ErrorCode}",
                ex.Status,
                ex.ErrorCode ?? "(none)");
            activity?.SetTag("result.source", "fallback_after_request_failed");
            activity?.SetTag("result.error_type", "Azure.RequestFailedException");
            activity?.SetTag("result.error_status", ex.Status);
            activity?.SetTag("result.error_code", ex.ErrorCode ?? string.Empty);

            return new AIChatResponse(
                BuildFallbackResponse(userQuestion, context, aiConfigured: true),
                ConfidenceSource: "structured_summary_fallback");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure AI Foundry chat failed; falling back to structured summary.");
            activity?.SetTag("result.source", "fallback_after_error");

            return new AIChatResponse(
                BuildFallbackResponse(userQuestion, context, aiConfigured: true),
                ConfidenceSource: "structured_summary_fallback");
        }
    }

    /// <summary>
    /// Calls Azure AI Foundry with an explicit system prompt and returns the raw text response.
    /// Use this for structured output (e.g., JSON) where chat framing and markdown formatting
    /// instructions from <see cref="GenerateResponseAsync"/> would corrupt the output.
    /// </summary>
    /// <param name="systemPrompt">System-level instruction for the model.</param>
    /// <param name="userPrompt">User-turn content.</param>
    /// <param name="maxTokens">Maximum tokens in the completion. Defaults to 4096.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw model response text, or null if AI is unavailable or the call fails.</returns>
    public async Task<string?> GenerateStructuredJsonAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens = 4096,
        CancellationToken cancellationToken = default)
    {
        if (_rawChatAgent is null)
        {
            return null;
        }

        try
        {
            return await _resiliencePipeline!.ExecuteAsync(async ct =>
            {
                try
                {
                    return await _rawChatAgent.Value.CompleteStructuredAsync(systemPrompt, userPrompt, maxTokens, ct);
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 401 && _rawChatAgentFallback is not null)
                {
                    _logger.LogWarning(
                        ex,
                        "Primary Foundry token audience failed with 401 during structured generation. Retrying with fallback scope {FallbackTokenScope}.",
                        _fallbackTokenScope);
                    return await _rawChatAgentFallback.Value.CompleteStructuredAsync(systemPrompt, userPrompt, maxTokens, ct);
                }
            }, cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning(ex, "AI Foundry circuit breaker is open; structured JSON generation skipped.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AI Foundry structured JSON generation failed; deterministic fallback will be used.");
            return null;
        }
    }


    /// <summary>
    /// Performs an active Azure AI Foundry chat probe and returns structured health state.
    /// </summary>
    public async Task<FoundryConnectivityStatus> CheckConnectivityAsync(CancellationToken cancellationToken = default)
    {
        if (_chatAgent is null)
        {
            return new FoundryConnectivityStatus
            {
                OverallState = "unconfigured",
                IsAiConfigured = false,
                Message = "Azure AI Foundry is not configured."
            };
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));

            var response = await _resiliencePipeline!.ExecuteAsync(async ct =>
            {
                var agent = _chatAgent.Value;
                try
                {
                    var session = await agent.CreateSessionAsync(ct);
                    return await agent.RunAsync("Health probe: reply with the single word OK.", session, new AgentRunOptions(), ct);
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 401 && _chatAgentFallbackAudience is not null)
                {
                    _logger.LogWarning(
                        ex,
                        "Foundry health probe failed with primary token audience. Retrying once with fallback token scope {FallbackTokenScope}",
                        _fallbackTokenScope);

                    var fallbackAgent = _chatAgentFallbackAudience.Value;
                    var fallbackSession = await fallbackAgent.CreateSessionAsync(ct);
                    return await fallbackAgent.RunAsync("Health probe: reply with the single word OK.", fallbackSession, new AgentRunOptions(), ct);
                }
            }, timeoutCts.Token);

            return new FoundryConnectivityStatus
            {
                OverallState = "healthy",
                IsAiConfigured = true,
                Message = "Azure AI Foundry connectivity probe succeeded.",
                ResponseId = response.ResponseId
            };
        }
        catch (BrokenCircuitException ex)
        {
            return new FoundryConnectivityStatus
            {
                OverallState = "degraded",
                IsAiConfigured = true,
                Message = "Azure AI Foundry circuit breaker is open.",
                RetryAfterSeconds = (int)Math.Ceiling((ex.RetryAfter?.TotalSeconds) ?? 60)
            };
        }
        catch (Azure.RequestFailedException ex)
        {
            return new FoundryConnectivityStatus
            {
                OverallState = "unhealthy",
                IsAiConfigured = true,
                Message = "Azure AI Foundry returned a request failure.",
                HttpStatus = ex.Status,
                ErrorCode = ex.ErrorCode
            };
        }
        catch (Exception ex)
        {
            return new FoundryConnectivityStatus
            {
                OverallState = "unhealthy",
                IsAiConfigured = true,
                Message = "Azure AI Foundry connectivity probe failed.",
                ErrorCode = ex.GetType().Name
            };
        }
    }

    private static string BuildGroundedPrompt(string userQuestion, InfrastructureContext context)
    {
        return $"""
            You are NimbusIQ, an Azure infrastructure governance AI assistant powered by Azure AI Foundry.
            You help cloud architects and platform engineers understand and improve their Azure infrastructure.

            Answer the user's question using ONLY the infrastructure context provided below.
            Be concise, specific, and actionable. Use markdown formatting.
            If the data doesn't contain enough information to answer, say so honestly.

            ## Infrastructure Context

            **Service Groups**: {context.ServiceGroupCount} configured
            **Service Group Names**: {string.Join(", ", context.ServiceGroupNames)}
            **Recent Analysis Runs**: {context.RecentRunCount} ({context.CompletedRunCount} completed, {context.PendingRunCount} in progress)

            **Findings ({context.Findings.Count} total)**:
            {FormatFindings(context.Findings)}

            **Detailed Data**:
            {context.DetailedDataJson}

            ## User Question
            {userQuestion}
            """;
    }

    private static string FormatFindings(IReadOnlyList<FindingSummary> findings)
    {
        if (findings.Count == 0)
            return "No findings available.";

        return string.Join("\n", findings.Select(f => $"- {f.Label}"));
    }

    private static string BuildFallbackResponse(string question, InfrastructureContext context, bool aiConfigured)
    {
        _ = question;

        var fallbackNote = aiConfigured
            ? "*Note: Azure AI Foundry is configured, but this request fell back to deterministic analysis (for example, due to a transient model or permission issue).*"
            : "*Note: AI-powered analysis is not currently configured. Connect Azure AI Foundry for deeper cross-resource correlation, root-cause analysis, and natural-language remediation plans that rule-based analysis cannot provide.*";

        if (context.Findings.Count > 0)
        {
            var lines = context.Findings.Take(5).Select(f => $"- {f.Label}");
            var findingList = string.Join("\n", lines);
            var moreNote = context.Findings.Count > 5
                ? $"\n\n…and {context.Findings.Count - 5} more."
                : "";

            return $"Based on your infrastructure data, here are the key findings:\n\n{findingList}{moreNote}\n\n" +
                   fallbackNote;
        }

        return $"Your Atlas environment has **{context.ServiceGroupCount} service group{(context.ServiceGroupCount == 1 ? "" : "s")}** configured " +
               $"with {context.RecentRunCount} recent analysis runs.\n\n" +
               $"*AI-powered chat requires Azure AI Foundry configuration. " +
               $"Set `AzureAIFoundry__ProjectEndpoint` and `AzureAIFoundry__ModelDeployment` (or `AzureAIFoundry__DefaultModelDeployment`) " +
               $"to enable GPT-4 deep analysis — including cross-resource correlation, root-cause identification, and prioritised remediation plans.*";
    }

    private static string? ResolveModelDeployment(AIChatOptions options)
        => !string.IsNullOrWhiteSpace(options.ModelDeployment)
            ? options.ModelDeployment
            : options.DefaultModelDeployment;

    private AtlasChatAgent BuildRawAgent(string modelDeployment, string? tokenScope)
    {
        var uamiClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var baseCredential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeManagedIdentityCredential = false,
            ExcludeAzureCliCredential = _useManagedIdentityOnlyCredentialChain,
            ExcludeEnvironmentCredential = _useManagedIdentityOnlyCredentialChain,
            ManagedIdentityClientId = uamiClientId
        });

        var scopedCredential = new ScopedTokenCredential(baseCredential, tokenScope ?? _primaryTokenScope);
        var projectEndpoint = new Uri(_options.ProjectEndpoint!);
        var inferenceEndpoint = new Uri($"{projectEndpoint.GetLeftPart(UriPartial.Authority)}/models");
        var clientOptions = new AzureAIInferenceClientOptions(AzureAIInferenceClientOptions.ServiceVersion.V2024_05_01_Preview);
        var chatClient = new ChatCompletionsClient(inferenceEndpoint, scopedCredential, clientOptions);

        return new AtlasChatAgent(chatClient, modelDeployment);
    }

    private sealed class ScopedTokenCredential(TokenCredential inner, string scope) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => inner.GetToken(new TokenRequestContext([scope]), cancellationToken);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => inner.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
    }

    /// <summary>
    /// MAF-native AIAgent that wraps Azure AI Foundry ChatCompletionsClient for the control-plane chat.
    /// Uses a specialized system prompt for infrastructure governance context.
    /// </summary>
    private sealed class AtlasChatAgent : AIAgent
    {
        private readonly ChatCompletionsClient _chatClient;
        private readonly string _modelDeployment;

        public AtlasChatAgent(ChatCompletionsClient chatClient, string modelDeployment)
        {
            _chatClient = chatClient;
            _modelDeployment = modelDeployment;
        }

        public override string Name => "NimbusIQ Chat Agent";
        public override string Description => "Infrastructure governance chat powered by Azure AI Foundry — cross-resource correlation, root-cause analysis, and remediation planning.";
        protected override string IdCore => "nimbusiq-chat-agent";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(new AtlasChatSession(new AgentSessionStateBag()));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState, JsonSerializerOptions? serializerOptions, CancellationToken cancellationToken)
            => ValueTask.FromResult<AgentSession>(
                new AtlasChatSession(AgentSessionStateBag.Deserialize(sessionState)));

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession? session, JsonSerializerOptions? serializerOptions, CancellationToken cancellationToken)
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
            var prompt = messages.LastOrDefault(m => m.Role == MChatRole.User)?.Text
                ?? messages.LastOrDefault()?.Text
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new AgentResponse(new ChatMessage(MChatRole.Assistant, string.Empty))
                { AgentId = Id };
            }

            var completionOptions = new ChatCompletionsOptions(new ChatRequestMessage[]
            {
                new ChatRequestSystemMessage(
                    "You are Atlas, an Azure infrastructure governance AI assistant. " +
                    "Provide concise, actionable analysis grounded in the infrastructure data provided. " +
                    "Use markdown formatting. Be honest about confidence levels."),
                new ChatRequestUserMessage(prompt)
            })
            {
                Model = _modelDeployment,
                Temperature = 0.3f,
                MaxTokens = 1200
            };

            var response = await _chatClient.CompleteAsync(completionOptions, cancellationToken);
            var completion = response.Value;

            return new AgentResponse(new ChatMessage(MChatRole.Assistant, completion.Content ?? string.Empty))
            {
                AgentId = Id,
                ResponseId = completion.Id,
                CreatedAt = completion.Created,
                RawRepresentation = completion
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
                yield return update;
        }

        /// <summary>
        /// Calls the underlying ChatCompletionsClient directly with an explicit system prompt,
        /// bypassing the MAF session and chat-framing wrapper. Used for structured JSON output
        /// where the standard chat system prompt would corrupt the response format.
        /// </summary>
        internal async Task<string?> CompleteStructuredAsync(
            string systemPrompt,
            string userPrompt,
            int maxTokens,
            CancellationToken cancellationToken)
        {
            var completionOptions = new ChatCompletionsOptions(new ChatRequestMessage[]
            {
                new ChatRequestSystemMessage(systemPrompt),
                new ChatRequestUserMessage(userPrompt)
            })
            {
                Model = _modelDeployment,
                Temperature = 0.1f,
                MaxTokens = maxTokens
            };

            var response = await _chatClient.CompleteAsync(completionOptions, cancellationToken);
            return response.Value.Content;
        }

        private sealed class AtlasChatSession : AgentSession
        {
            public AtlasChatSession(AgentSessionStateBag stateBag) : base(stateBag) { }
        }
    }
}

public record AIChatResponse(string Text, string ConfidenceSource);

public sealed record FoundryConnectivityStatus
{
    public string OverallState { get; init; } = "unknown";
    public bool IsAiConfigured { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? HttpStatus { get; init; }
    public string? ErrorCode { get; init; }
    public string? ResponseId { get; init; }
    public int? RetryAfterSeconds { get; init; }
}

public record AIChatOptions
{
    public const string SectionName = "AzureAIFoundry";
    public string? ProjectEndpoint { get; set; }
    public string? ModelDeployment { get; set; }
    public string? DefaultModelDeployment { get; set; }
}

public record InfrastructureContext
{
    public int ServiceGroupCount { get; init; }
    public IReadOnlyList<string> ServiceGroupNames { get; init; } = [];
    public int RecentRunCount { get; init; }
    public int CompletedRunCount { get; init; }
    public int PendingRunCount { get; init; }
    public IReadOnlyList<FindingSummary> Findings { get; init; } = [];
    public string DetailedDataJson { get; init; } = "{}";
}

public record FindingSummary(string Label, string? Category = null, DateTime? Timestamp = null);
