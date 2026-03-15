using Atlas.AgentOrchestrator.Agents;
using Atlas.AgentOrchestrator.Contracts;
using Atlas.AgentOrchestrator.Integrations.Auth;
using Atlas.AgentOrchestrator.Integrations.Azure;
using Atlas.AgentOrchestrator.Integrations.MCP;
using Atlas.AgentOrchestrator.Integrations.Prompts;
using Atlas.AgentOrchestrator.Orchestration;
using Atlas.AgentOrchestrator.Orchestration.Telemetry;
using Azure.Core;
using Azure.Identity;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured for the agent orchestrator.");
}

builder.Services.AddSingleton(_ =>
{
    // Configure Npgsql to use Azure AD authentication with managed identity
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    var tenantId = builder.Configuration[$"{ManagedIdentityOptions.SectionName}:TenantId"];
    var userAssignedClientId =
        builder.Configuration[$"{ManagedIdentityOptions.SectionName}:UserAssignedClientId"]
        ?? Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
    var enforceManagedIdentityOnly =
        builder.Configuration.GetValue<bool?>($"{ManagedIdentityOptions.SectionName}:EnforceManagedIdentityOnly")
        ?? (builder.Configuration.GetValue<bool?>($"{ManagedIdentityOptions.SectionName}:EnforceManagedIdentityOnlyInProduction") ?? true)
            && builder.Environment.IsProduction();

    TokenCredential dbCredential = enforceManagedIdentityOnly
        ? new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeManagedIdentityCredential = false,
            ExcludeAzureCliCredential = true,
            ExcludeEnvironmentCredential = true,
            ManagedIdentityClientId = userAssignedClientId,
            TenantId = tenantId
        })
        : new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeManagedIdentityCredential = false,
            ExcludeAzureCliCredential = false,
            ExcludeEnvironmentCredential = false,
            ManagedIdentityClientId = userAssignedClientId,
            TenantId = tenantId
        });

    dataSourceBuilder.UsePeriodicPasswordProvider(async (_, ct) =>
    {
        var token = await dbCredential.GetTokenAsync(
            new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]),
            ct);
        return token.Token;
    }, TimeSpan.FromHours(1), TimeSpan.FromSeconds(10));
    return dataSourceBuilder.Build();
});

// Managed Identity
builder.Services.Configure<ManagedIdentityOptions>(
    builder.Configuration.GetSection(ManagedIdentityOptions.SectionName));
builder.Services.AddSingleton<ManagedIdentityCredentialProvider>();
builder.Services.AddSingleton<IMcpToolCallAuditor, McpToolCallAuditor>();

builder.Services.Configure<AzureAIFoundryOptions>(
    builder.Configuration.GetSection(AzureAIFoundryOptions.SectionName));

var foundryProjectEndpoint = builder.Configuration[$"{AzureAIFoundryOptions.SectionName}:ProjectEndpoint"];

var useFoundryOrchestration =
    builder.Configuration.GetValue<bool?>("AgentFramework:UseFoundryOrchestration")
    ?? builder.Configuration.GetValue<bool?>("AgentFramework__UseFoundryOrchestration")
    ?? !string.IsNullOrWhiteSpace(foundryProjectEndpoint);

builder.Services.Configure<PromptProviderOptions>(
    builder.Configuration.GetSection(PromptProviderOptions.SectionName));
builder.Services.AddSingleton<IPromptProvider, FilePromptProvider>();

// Azure AI Foundry client is only available when project endpoint/config is configured
if (useFoundryOrchestration && !string.IsNullOrWhiteSpace(foundryProjectEndpoint))
{
    builder.Services.AddSingleton<Atlas.AgentOrchestrator.Integrations.Azure.AzureAIFoundryClient>();
}

// Official Azure MCP tool client — uses ModelContextProtocol SDK with SSE transport
builder.Services.Configure<AzureMcpOptions>(
    builder.Configuration.GetSection(AzureMcpOptions.SectionName));
var azureMcpEnabled = builder.Configuration.GetValue<bool?>($"{AzureMcpOptions.SectionName}:Enabled") ?? true;
if (azureMcpEnabled)
{
    builder.Services.AddSingleton<AzureMcpToolClient>();
}

// A2A message validation
builder.Services.AddSingleton<A2AMessageValidator>();

builder.Services.AddHttpClient<AzureResourceGraphClient>();
builder.Services.AddSingleton<IResourceGraphClient>(sp =>
    sp.GetRequiredService<AzureResourceGraphClient>());
builder.Services.AddHttpClient<AzureCostManagementClient>();
builder.Services.AddHttpClient<AzureCarbonClient>();
builder.Services.AddSingleton<LogAnalyticsWasteAnalyzer>(sp =>
{
    var credential = sp.GetRequiredService<ManagedIdentityCredentialProvider>().GetCredential();
    var logger = sp.GetRequiredService<ILogger<LogAnalyticsWasteAnalyzer>>();
    return new LogAnalyticsWasteAnalyzer(credential, logger);
});
builder.Services.AddSingleton<OrphanDetectionService>(sp => new OrphanDetectionService(
    sp.GetRequiredService<IResourceGraphClient>(),
    sp.GetRequiredService<ILogger<OrphanDetectionService>>(),
    sp.GetService<LogAnalyticsWasteAnalyzer>()));

// Microsoft Agent Framework orchestration
builder.Services.AddSingleton<DriftSnapshotPersistenceService>();

// Agent registrations — Gen-1
builder.Services.AddSingleton<ServiceIntelligenceAgent>();
builder.Services.AddSingleton<BestPracticeEngine>();
builder.Services.AddSingleton<DriftDetectionAgent>();
builder.Services.AddSingleton<WellArchitectedAssessmentAgent>();
builder.Services.AddSingleton<CloudNativeMaturityAgent>();
builder.Services.AddSingleton<FinOpsOptimizerAgent>(sp => new FinOpsOptimizerAgent(
    sp.GetRequiredService<ILogger<FinOpsOptimizerAgent>>(),
    sp.GetService<Atlas.AgentOrchestrator.Integrations.Azure.AzureAIFoundryClient>(),
    sp.GetService<OrphanDetectionService>(),
    sp.GetService<AzureMcpToolClient>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Atlas.AgentOrchestrator.Integrations.MCP.AzureMcpOptions>>().Value.CostLookbackDays,
    sp.GetService<IPromptProvider>()));
builder.Services.AddSingleton<ServiceHierarchyAnalyzer>();
// Gen-2 agents
builder.Services.AddSingleton<ArchitectureAgent>();
builder.Services.AddSingleton<ReliabilityAgent>();
builder.Services.AddSingleton<SustainabilityAgent>();
builder.Services.AddSingleton<GovernanceNegotiationAgent>(sp => new GovernanceNegotiationAgent(
    sp.GetRequiredService<ILogger<GovernanceNegotiationAgent>>(),
    sp.GetService<Atlas.AgentOrchestrator.Integrations.Azure.AzureAIFoundryClient>()));

// Learn MCP client — grounding agents with current WAF guidance
builder.Services.Configure<LearnMcpOptions>(
    builder.Configuration.GetSection(LearnMcpOptions.SectionName));
var learnMcpEnabled = builder.Configuration.GetValue<bool?>($"{LearnMcpOptions.SectionName}:Enabled") ?? true;
if (learnMcpEnabled)
{
    builder.Services.AddSingleton<LearnMcpClient>();
}

builder.Services.AddSingleton<MafGroundingSkill>();

// Governance Mediator — Concurrent+Mediator MAF pattern
builder.Services.AddSingleton<GovernanceMediatorAgent>(sp => new GovernanceMediatorAgent(
    sp.GetRequiredService<ILogger<GovernanceMediatorAgent>>(),
    sp.GetService<Atlas.AgentOrchestrator.Integrations.Azure.AzureAIFoundryClient>(),
    sp.GetService<LearnMcpClient>()));
builder.Services.AddSingleton<ConcurrentMediatorOrchestrator>();

builder.Services.AddSingleton<MultiAgentOrchestrator>();

// Azure integration clients (data plane integrations)
builder.Services.AddSingleton<IAzureMonitorClient, AzureMonitorClient>();
builder.Services.AddSingleton<IAzureCostManagementClient>(sp =>
    new AzureCostManagementClientAdapter(sp.GetRequiredService<AzureCostManagementClient>()));

// Discovery, IaC generation, and timeline projection
builder.Services.AddSingleton<DiscoveryWorkflow>();
builder.Services.AddSingleton<IacGenerationWorkflow>(sp => new IacGenerationWorkflow(
    sp.GetRequiredService<ILogger<IacGenerationWorkflow>>(),
    sp.GetService<Atlas.AgentOrchestrator.Integrations.Azure.AzureAIFoundryClient>(),
    sp.GetService<IPromptProvider>(),
    sp.GetService<AzureMcpToolClient>()));

builder.Services.AddSingleton<AtlasDbRepository>();
builder.Services.AddSingleton<AnalysisRunProcessor>();
builder.Services.AddHostedService<Worker>();

// OpenTelemetry with improved configuration
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerBuilder =>
    {
        OpenTelemetryConfiguration.ConfigureTracing(tracerBuilder, otlpEndpoint);
    })
    .WithMetrics(meterBuilder =>
    {
        OpenTelemetryConfiguration.ConfigureMetrics(meterBuilder, otlpEndpoint);
    });

var host = builder.Build();
host.Run();
