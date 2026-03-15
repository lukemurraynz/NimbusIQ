using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Collections.Concurrent;
using Azure.AI.Inference;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Atlas.AgentOrchestrator.Integrations.Azure;

/// <summary>
/// Client for Azure AI Foundry (previously Azure AI Studio) using managed identity.
/// Supports agent operations, prompt flow, and model deployment.
/// </summary>
public class AzureAIFoundryClient : IAzureAIFoundryClient
{
    private readonly AIProjectClient _projectClient;
    private readonly ILogger<AzureAIFoundryClient> _logger;
    private readonly AzureAIFoundryOptions _options;
    private readonly Lazy<AIAgent>? _promptAgent;
    private readonly bool _hostedAgentsEnabled;
    private readonly ConcurrentDictionary<string, Lazy<Task<AgentSession>>> _threadSessions = new();
    private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.Azure.AIFoundry");

    public AzureAIFoundryClient(
        IOptions<AzureAIFoundryOptions> options,
        ILogger<AzureAIFoundryClient> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.ProjectEndpoint))
        {
            throw new InvalidOperationException("Azure AI Foundry project endpoint is not configured");
        }

        // Build credential chain: Managed Identity -> Azure CLI (dev).
        // Explicitly target UAMI when AZURE_CLIENT_ID is set; without ManagedIdentityClientId,
        // DefaultAzureCredential picks up the SAMI on containers that have both SAMI and UAMI.
        var uamiClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeManagedIdentityCredential = false,
            ExcludeAzureCliCredential = false,
            ExcludeEnvironmentCredential = false,
            ManagedIdentityClientId = uamiClientId  // explicit UAMI; null falls back to SAMI or CLI
        });

        // ProjectEndpoint may be a plain URL or a semicolon-delimited connection string:
        // <endpoint>;<subscription_id>;<resource_group>;<project_name>
        var endpointUrl = _options.ProjectEndpoint.Contains(';')
            ? _options.ProjectEndpoint.Split(';')[0]
            : _options.ProjectEndpoint;

        _projectClient = new AIProjectClient(
            new Uri(endpointUrl),
            credential);

        _hostedAgentsEnabled = !string.IsNullOrWhiteSpace(_options.CapabilityHostName);

        if (!string.IsNullOrWhiteSpace(_options.DefaultModelDeployment))
        {
            // AIProjectClient.GetChatCompletionsClient() acquires tokens with the AI Foundry project
            // audience (https://ai.azure.com) which the inference endpoint rejects.
            // Create ChatCompletionsClient directly so Azure.AI.Inference uses its default
            // https://cognitiveservices.azure.com/.default scope, which is required by services.ai.azure.com.
            var projectEndpoint = new Uri(endpointUrl);
            var inferenceEndpoint = new Uri($"{projectEndpoint.GetLeftPart(UriPartial.Authority)}/models");
            var chatClient = new ChatCompletionsClient(inferenceEndpoint, credential);
            _promptAgent = new Lazy<AIAgent>(() =>
                new FoundryPromptAgent(chatClient, _options.DefaultModelDeployment!)
                    .AsBuilder()
                    .UseOpenTelemetry("Atlas.AgentOrchestrator.Azure.AIFoundry.PromptAgent")
                    .Build(null));
        }
        else
        {
            _logger.LogWarning(
                "AzureAIFoundry:DefaultModelDeployment is not configured. Prompt-based AI recommendations will run in fallback mode.");
        }

        _logger.LogInformation(
            "Azure AI Foundry client initialized. Endpoint: {Endpoint}, ModelDeploymentConfigured: {ModelConfigured}, HostedAgentsEnabled: {HostedAgentsEnabled}",
            _options.ProjectEndpoint,
            !string.IsNullOrWhiteSpace(_options.DefaultModelDeployment),
            _hostedAgentsEnabled);
    }

    /// <summary>
    /// Gets the configured project client for direct SDK operations.
    /// </summary>
    public AIProjectClient GetProjectClient() => _projectClient;

    /// <summary>
    /// Sends a single prompt to a model deployment through a MAF-native agent wrapper.
    /// </summary>
    public async Task<string> SendPromptAsync(
        string prompt,
        CancellationToken cancellationToken = default)
        => await SendPromptAsync(prompt, threadId: null, cancellationToken);

    /// <summary>
    /// Sends a prompt to a model deployment through a MAF-native agent wrapper,
    /// optionally reusing a stable thread/session for multi-turn context.
    /// </summary>
    public async Task<string> SendPromptAsync(
        string prompt,
        string? threadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt must be provided.", nameof(prompt));
        }

        using var activity = ActivitySource.StartActivity("AIFoundry.SendPrompt");
        activity?.SetTag("prompt.length", prompt.Length);
        activity?.SetTag("thread.id", threadId ?? "ephemeral");
        activity?.SetTag("model.deployment", _options.DefaultModelDeployment ?? "unconfigured");

        if (_promptAgent is null)
        {
            activity?.SetTag("result.status", "fallback");
            return "AI model deployment is not configured for Azure AI Foundry prompt execution.";
        }

        try
        {
            var promptAgent = _promptAgent.Value;
            var session = await GetOrCreateSessionAsync(promptAgent, threadId, cancellationToken);
            var response = await promptAgent.RunAsync(prompt, session, new AgentRunOptions(), cancellationToken);

            activity?.SetTag("result.status", "success");
            activity?.SetTag("response.id", response.ResponseId);

            return response.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Prompt execution failed using Azure AI Foundry model deployment.");
            throw;
        }
    }

    private async Task<AgentSession> GetOrCreateSessionAsync(
        AIAgent promptAgent,
        string? threadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return await promptAgent.CreateSessionAsync(cancellationToken);
        }

        var lazySession = _threadSessions.GetOrAdd(
            threadId,
            _ => new Lazy<Task<AgentSession>>(
                () => promptAgent.CreateSessionAsync(CancellationToken.None).AsTask(),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return await lazySession.Value.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Invokes a deployed prompt flow with input data.
    /// </summary>
    /// <param name="flowName">Name of the deployed prompt flow</param>
    /// <param name="inputs">Input parameters for the flow</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<Dictionary<string, object>> InvokePromptFlowAsync(
        string flowName,
        Dictionary<string, object> inputs,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AIFoundry.InvokePromptFlow");
        activity?.SetTag("flow.name", flowName);
        activity?.SetTag("input.count", inputs.Count);

        try
        {
            _logger.LogInformation(
                "Invoking hosted Foundry agent via legacy prompt-flow entrypoint: {FlowName}",
                flowName);

            var response = await InvokeHostedAgentFlowAsync(flowName, inputs, cancellationToken);

            activity?.SetTag("result.status", "success");

            _logger.LogInformation("Prompt flow invoked successfully: {FlowName}", flowName);

            return response;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Prompt flow invocation failed: {FlowName}", flowName);
            throw;
        }
    }

    /// <summary>
    /// Creates an agent with specified configuration.
    /// </summary>
    public async Task<AgentHandle> CreateAgentAsync(
        string agentName,
        string modelDeploymentName,
        string instructions,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AIFoundry.CreateAgent");
        activity?.SetTag("agent.name", agentName);
        activity?.SetTag("model.deployment", modelDeploymentName);

        try
        {
            if (!_hostedAgentsEnabled)
            {
                _logger.LogWarning(
                    "Hosted Azure AI Foundry agents are not configured. Set AzureAIFoundry:CapabilityHostName to enable remote agent creation.");

                return new AgentHandle(agentName, modelDeploymentName)
                {
                    Status = "unconfigured",
                    Description = "Hosted agent capability is not configured for this environment."
                };
            }

            _logger.LogInformation("Creating agent: {AgentName} with model: {ModelDeployment}",
                agentName, modelDeploymentName);

            var agent = await _projectClient.CreateAIAgentAsync(
                agentName,
                modelDeploymentName,
                instructions,
                $"NimbusIQ hosted agent '{agentName}'",
                [],
                null,
                null,
                cancellationToken);

            var agentHandle = new AgentHandle(agentName, modelDeploymentName)
            {
                AgentId = agent.Id,
                Status = "created",
                Description = agent.Description
            };

            activity?.SetTag("agent.id", agentHandle.AgentId);

            _logger.LogInformation("Agent created successfully: {AgentName}, ID: {AgentId}",
                agentName, agentHandle.AgentId);

            return agentHandle;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Agent creation failed: {AgentName}", agentName);
            throw;
        }
    }

    /// <summary>
    /// Internal implementation: resolves a hosted Azure AI Foundry agent by name and executes it
    /// against the supplied structured input payload. Falls back to an "unconfigured" result when
    /// hosted-agent capability is not configured.
    /// </summary>
    private async Task<Dictionary<string, object>> InvokeHostedAgentFlowAsync(
        string flowName,
        Dictionary<string, object> inputs,
        CancellationToken cancellationToken)
    {
        if (!_hostedAgentsEnabled)
        {
            _logger.LogWarning(
                "Prompt flow '{FlowName}' invoked but AzureAIFoundry:CapabilityHostName is not configured.",
                flowName);

            return new Dictionary<string, object>
            {
                ["status"] = "unconfigured",
                ["flowName"] = flowName,
                ["message"] = $"Flow '{flowName}' requires AzureAIFoundry:CapabilityHostName to be configured."
            };
        }

        var agent = await _projectClient.GetAIAgentAsync(flowName, [], null, null, cancellationToken);
        var session = await agent.CreateSessionAsync(cancellationToken);
        var payloadJson = JsonSerializer.Serialize(inputs, new JsonSerializerOptions { WriteIndented = false });
        var response = await agent.RunAsync(payloadJson, session, new AgentRunOptions(), cancellationToken);

        return new Dictionary<string, object>
        {
            ["status"] = "success",
            ["flowName"] = flowName,
            ["invocationKind"] = "hosted-agent",
            ["agentId"] = agent.Id,
            ["responseId"] = response.ResponseId ?? string.Empty,
            ["response"] = response.Text ?? string.Empty
        };
    }

    private sealed class FoundryPromptAgent : AIAgent
    {
        private readonly ChatCompletionsClient _chatCompletionsClient;
        private readonly string _modelDeploymentName;

        public FoundryPromptAgent(ChatCompletionsClient chatCompletionsClient, string modelDeploymentName)
        {
            _chatCompletionsClient = chatCompletionsClient;
            _modelDeploymentName = modelDeploymentName;
        }

        public override string Name => "Azure AI Foundry Prompt Agent";

        public override string Description => "Runs model prompts against Azure AI Foundry deployments.";

        protected override string IdCore => "azure-ai-foundry-prompt-agent";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<AgentSession>(new FoundryPromptAgentSession(new AgentSessionStateBag()));
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? serializerOptions,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<AgentSession>(
                new FoundryPromptAgentSession(AgentSessionStateBag.Deserialize(sessionState)));
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
            _ = session;
            _ = options;

            var prompt = messages.LastOrDefault(message => message.Role == MChatRole.User)?.Text
                ?? messages.LastOrDefault()?.Text
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new AgentResponse(new ChatMessage(MChatRole.Assistant, string.Empty))
                {
                    AgentId = Id
                };
            }

            var completionOptions = new ChatCompletionsOptions(new ChatRequestMessage[]
            {
                new ChatRequestSystemMessage(
                    "You are an Azure architecture and FinOps expert. Respond with concise, actionable recommendations."),
                new ChatRequestUserMessage(prompt)
            })
            {
                Model = _modelDeploymentName,
                Temperature = 0.2f,
                MaxTokens = 800
            };

            var completionResponse = await _chatCompletionsClient.CompleteAsync(completionOptions, cancellationToken);
            var completion = completionResponse.Value;
            var content = completion.Content ?? string.Empty;

            return new AgentResponse(new ChatMessage(MChatRole.Assistant, content))
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
            {
                yield return update;
            }
        }

        private sealed class FoundryPromptAgentSession : AgentSession
        {
            public FoundryPromptAgentSession(AgentSessionStateBag stateBag)
                : base(stateBag)
            {
            }
        }
    }
}

/// <summary>
/// Configuration options for Azure AI Foundry client.
/// </summary>
public class AzureAIFoundryOptions
{
    public const string SectionName = "AzureAIFoundry";

    /// <summary>
    /// Azure AI Foundry project endpoint. Accepts either:
    /// <list type="bullet">
    ///   <item><description>
    ///     Plain URL: <c>https://{project-name}.{region}.api.azureml.ms</c>
    ///   </description></item>
    ///   <item><description>
    ///     Semicolon-delimited connection string (Azure AI Foundry portal format):
    ///     <c>&lt;endpoint&gt;;&lt;subscription_id&gt;;&lt;resource_group&gt;;&lt;project_name&gt;</c>
    ///     — the URL segment before the first semicolon is extracted automatically.
    ///   </description></item>
    /// </list>
    /// </summary>
    public string? ProjectEndpoint { get; set; }

    /// <summary>
    /// Azure AI Foundry project connection string. Optional, but retained for parity with deployment config.
    /// </summary>
    public string? ProjectConnectionString { get; set; }

    /// <summary>
    /// Azure AI Foundry public project API endpoint.
    /// </summary>
    public string? ProjectApiEndpoint { get; set; }

    /// <summary>
    /// Azure AI Foundry project name.
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// Hosted agent capability host name. When omitted, hosted-agent operations are disabled.
    /// </summary>
    public string? CapabilityHostName { get; set; }

    /// <summary>
    /// Default model deployment name for agent operations.
    /// </summary>
    public string? DefaultModelDeployment { get; set; }
}

/// <summary>
/// Handle representing a created agent in Azure AI Foundry.
/// </summary>
public record AgentHandle(string AgentName, string ModelDeployment)
{
    public string AgentId { get; init; } = Guid.NewGuid().ToString();
    public string Status { get; init; } = "created";
    public string? Description { get; init; }
}
