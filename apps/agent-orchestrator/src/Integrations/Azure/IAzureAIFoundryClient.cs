namespace Atlas.AgentOrchestrator.Integrations.Azure;

/// <summary>
/// Abstraction over Azure AI Foundry so consuming agents and orchestrators can be
/// tested without a live Foundry project. Register the concrete
/// <see cref="AzureAIFoundryClient"/> in production DI and a test double in unit tests.
/// </summary>
public interface IAzureAIFoundryClient
{
    /// <summary>
    /// Sends a single free-form prompt to the configured model deployment.
    /// Returns a fallback message (non-throwing) when the model is not configured.
    /// </summary>
    Task<string> SendPromptAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a prompt using a stable thread identifier so multi-turn context can be reused
    /// across related calls.
    /// </summary>
    Task<string> SendPromptAsync(string prompt, string? threadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes a named deployed prompt flow with the supplied input dictionary.
    /// </summary>
    Task<Dictionary<string, object>> InvokePromptFlowAsync(
        string flowName,
        Dictionary<string, object> inputs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a remote AI agent in the Foundry project and returns a handle.
    /// Returns an <c>unconfigured</c> handle when hosted agents are not enabled.
    /// </summary>
    Task<AgentHandle> CreateAgentAsync(
        string agentName,
        string modelDeploymentName,
        string instructions,
        CancellationToken cancellationToken = default);
}
