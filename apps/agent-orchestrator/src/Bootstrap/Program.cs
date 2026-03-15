using Azure;
using Atlas.AgentOrchestrator.Integrations.Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var loggerFactory = LoggerFactory.Create(builder =>
{
  builder.AddSimpleConsole(options =>
  {
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
  });
});

var logger = loggerFactory.CreateLogger("FoundryAgentBootstrap");

var projectEndpoint =
    Environment.GetEnvironmentVariable("AzureAIFoundry__ProjectEndpoint")
    ?? Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_CONNECTION_STRING")
    ?? Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT");

if (string.IsNullOrWhiteSpace(projectEndpoint))
{
  logger.LogWarning("No Azure AI Foundry project endpoint/connection string found. Skipping hosted agent bootstrap.");
  return;
}

var capabilityHostName =
    Environment.GetEnvironmentVariable("AzureAIFoundry__CapabilityHostName")
    ?? Environment.GetEnvironmentVariable("aiFoundryCapabilityHostName");

if (string.IsNullOrWhiteSpace(capabilityHostName))
{
  logger.LogWarning("No Azure AI Foundry capability host configured. Skipping hosted agent bootstrap.");
  return;
}

var modelDeployment =
    Environment.GetEnvironmentVariable("AzureAIFoundry__DefaultModelDeployment")
    ?? Environment.GetEnvironmentVariable("AZURE_AI_DEFAULT_MODEL_DEPLOYMENT")
    ?? "gpt-4o";

var options = Options.Create(new AzureAIFoundryOptions
{
  ProjectEndpoint = projectEndpoint,
  CapabilityHostName = capabilityHostName,
  DefaultModelDeployment = modelDeployment
});

var foundryLogger = loggerFactory.CreateLogger<AzureAIFoundryClient>();
var client = new AzureAIFoundryClient(options, foundryLogger);
var agents = new (string Name, string Instructions)[]
{
    (
        "governance-mediation-agent",
        "You are a cloud governance mediator. Reconcile cross-pillar trade-offs with concise, actionable recommendations grounded in Azure Well-Architected guidance."
    ),
    (
        "governance-negotiation-agent",
        "You mediate policy conflicts in Azure environments. Produce clear approve/escalate/block outcomes with rationale on cost, risk, SLA, and compliance."
    ),
    (
        "architecture-narrative-agent",
        "You are an Azure architecture advisor. Summarize architecture findings with explicit risk and prioritized remediation actions."
    ),
    (
        "reliability-narrative-agent",
        "You are an Azure reliability advisor. Explain SLA and resiliency risks and provide prioritized remediation steps."
    ),
    (
        "sustainability-narrative-agent",
        "You are an Azure sustainability advisor. Explain carbon and efficiency impacts and recommend high-impact remediations."
    ),
    (
        "finops-optimization-agent",
        "You are an Azure FinOps optimization advisor. Recommend concrete savings opportunities and quantify impact with assumptions."
    ),
    (
        "waf-assessment-agent",
        "You are an Azure Well-Architected assessor. Evaluate pillar posture and provide concise, evidence-based recommendations."
    ),
    (
        "iac-generation-agent",
        "You generate secure, maintainable IaC recommendations and snippets aligned to Azure best practices and policy constraints."
    )
};

var created = 0;
var existing = 0;

foreach (var agent in agents)
{
  try
  {
    var handle = await client.CreateAgentAsync(
      agentName: agent.Name,
      modelDeploymentName: modelDeployment,
      instructions: agent.Instructions,
      cancellationToken: CancellationToken.None);

    if (string.Equals(handle.Status, "created", StringComparison.OrdinalIgnoreCase))
    {
      created++;
      logger.LogInformation("Hosted agent created: {AgentName} ({AgentId})", handle.AgentName, handle.AgentId);
    }
    else
    {
      logger.LogWarning("Hosted agent not created: {AgentName} status={Status}", handle.AgentName, handle.Status);
    }
  }
  catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 400)
  {
    // Foundry may return conflict/bad request for duplicate names depending on API version.
    existing++;
    logger.LogInformation("Hosted agent already exists: {AgentName}", agent.Name);
  }
}

logger.LogInformation(
    "Foundry agent bootstrap complete. Existing={ExistingCount}, Created={CreatedCount}, Total={TotalCount}",
    existing,
    created,
    agents.Length);
