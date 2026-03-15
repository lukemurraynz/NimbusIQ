using Atlas.ControlPlane.Infrastructure.Data;
using Atlas.ControlPlane.Infrastructure.Auth;
using Atlas.ControlPlane.Infrastructure.Telemetry;
using Atlas.ControlPlane.Application.Recommendations;
using Azure.Core;
using Azure.Identity;
using Atlas.AgentOrchestrator.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Identity.Web;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddScoped<Atlas.ControlPlane.Application.Services.IAnalysisEventPublisher,
    Atlas.ControlPlane.Api.Services.SignalRAnalysisEventPublisher>();

// Database - PostgreSQL with Azure AD authentication
// SECURITY: Connection string must be provided via configuration (appsettings.json, environment variable, or secret manager).
// Never hardcode credentials. For local development, inject via docker-compose .env file.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Database connection string 'DefaultConnection' must be configured via: " +
        "1. appsettings.json GetConnectionString('DefaultConnection'), " +
        "2. DefaultConnection environment variable, or " +
        "3. Azure Key Vault (in production). " +
        "For local development, set via docker-compose .env file.");
}

// Configure Npgsql to use Azure AD authentication with managed identity
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
if (!builder.Environment.IsDevelopment())
{
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
}
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<AtlasDbContext>(options =>
    options.UseNpgsql(dataSource)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
// IDbContextFactory is required by scoped background services
builder.Services.AddDbContextFactory<AtlasDbContext>(options =>
    options.UseNpgsql(dataSource)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)),
    ServiceLifetime.Scoped);

builder.Services.AddScoped<DecisionService>();
builder.Services.AddScoped<Atlas.ControlPlane.Application.Recommendations.IacGuardrailLinterService>();
builder.Services.AddScoped<Atlas.ControlPlane.Application.Recommendations.AvmIacExampleService>();
builder.Services.AddScoped<Atlas.ControlPlane.Application.Release.ReleaseAttestationService>();
builder.Services.AddScoped<Atlas.ControlPlane.Application.Services.IacGenerationService>(sp =>
    new Atlas.ControlPlane.Application.Services.IacGenerationService(
        sp.GetRequiredService<AtlasDbContext>(),
        sp.GetRequiredService<ILogger<Atlas.ControlPlane.Application.Services.IacGenerationService>>(),
        sp.GetService<Atlas.ControlPlane.Application.Services.AIChatService>()));
builder.Services.AddScoped<Atlas.ControlPlane.Application.ServiceGraph.ServiceGraphBuilder>();
builder.Services.AddScoped<Atlas.ControlPlane.Application.ServiceGraph.ComponentTypeDetector>();
builder.Services.AddScoped<Atlas.ControlPlane.Application.Services.ExecutiveNarrativeService>(sp =>
    new Atlas.ControlPlane.Application.Services.ExecutiveNarrativeService(
        sp.GetRequiredService<AtlasDbContext>(),
        sp.GetRequiredService<ILogger<Atlas.ControlPlane.Application.Services.ExecutiveNarrativeService>>(),
        sp.GetService<Atlas.ControlPlane.Application.Services.AIChatService>()));
builder.Services.AddScoped<Atlas.ControlPlane.Application.Services.ScoreSimulationService>();
builder.Services.AddScoped<Atlas.ControlPlane.Application.Services.CompletenessAnalyzer>();
builder.Services.AddScoped<Atlas.ControlPlane.Application.Services.IImpactFactorInsightService>(sp =>
    new Atlas.ControlPlane.Application.Services.ImpactFactorInsightService(
        sp.GetRequiredService<ILogger<Atlas.ControlPlane.Application.Services.ImpactFactorInsightService>>(),
        sp.GetService<Atlas.ControlPlane.Application.Services.AIChatService>()));

// Feature #1: ROI & Value Tracking
builder.Services.AddScoped<Atlas.ControlPlane.Application.ValueTracking.ValueRealizationService>();

// Feature #2: Automated Remediation
builder.Services.AddScoped<Atlas.ControlPlane.Application.Automation.AutomationService>();

// Feature #4: Recommendation Templates & Playbooks
builder.Services.AddScoped<Atlas.ControlPlane.Application.Templates.TemplateService>();

// Feature #5: GitOps Auto-PR Integration
builder.Services.AddScoped<Atlas.ControlPlane.Application.GitOps.GitOpsPrService>();

// Managed Identity
builder.Services.Configure<ManagedIdentityOptions>(
    builder.Configuration.GetSection(ManagedIdentityOptions.SectionName));
builder.Services.AddSingleton<ManagedIdentityCredentialProvider>();
builder.Services.Configure<Atlas.ControlPlane.Api.Services.McpLearnGroundingOptions>(
    builder.Configuration.GetSection(Atlas.ControlPlane.Api.Services.McpLearnGroundingOptions.SectionName));
builder.Services.AddSingleton<Atlas.ControlPlane.Application.Recommendations.IRecommendationGroundingClient, Atlas.ControlPlane.Api.Services.McpLearnGroundingClient>();
builder.Services.Configure<Atlas.ControlPlane.Api.Services.McpAzureValueEvidenceOptions>(
    builder.Configuration.GetSection(Atlas.ControlPlane.Api.Services.McpAzureValueEvidenceOptions.SectionName));
builder.Services.AddSingleton<Atlas.ControlPlane.Application.ValueTracking.IAzureMcpValueEvidenceClient, Atlas.ControlPlane.Api.Services.McpAzureValueEvidenceClient>();

// Azure clients using managed identity
builder.Services.AddHttpClient(); // enables IHttpClientFactory for AzureCostManagementClient
builder.Services.AddSingleton<Atlas.ControlPlane.Infrastructure.Azure.AzureResourceGraphClient>();
builder.Services.AddSingleton<Atlas.ControlPlane.Infrastructure.Azure.AzureMonitorClient>();
builder.Services.AddSingleton<Atlas.ControlPlane.Infrastructure.Azure.AzureCostManagementClient>();
builder.Services.AddSingleton<Atlas.ControlPlane.Infrastructure.Azure.IAzureCostManagementClient>(sp =>
    sp.GetRequiredService<Atlas.ControlPlane.Infrastructure.Azure.AzureCostManagementClient>());
builder.Services.AddSingleton<Atlas.ControlPlane.Infrastructure.Azure.ActivityLogClient>();

// Analysis pipeline services — use factory registrations so AzureResourceGraphClient is optional.
// When the client is not registered (e.g., in tests), the services still resolve with null.
builder.Services.AddScoped<Atlas.ControlPlane.Application.Services.AzureDiscoveryService>(sp =>
    new Atlas.ControlPlane.Application.Services.AzureDiscoveryService(
        sp.GetService<Atlas.ControlPlane.Infrastructure.Azure.AzureResourceGraphClient>(),
        sp.GetRequiredService<ILogger<Atlas.ControlPlane.Application.Services.AzureDiscoveryService>>()));
builder.Services.AddScoped<Atlas.ControlPlane.Application.Services.ScoringService>();

// AI Chat Service — Azure AI Foundry GPT-4 via Microsoft Agent Framework
builder.Services.Configure<Atlas.ControlPlane.Application.Services.AIChatOptions>(
    builder.Configuration.GetSection(Atlas.ControlPlane.Application.Services.AIChatOptions.SectionName));
builder.Services.AddSingleton<Atlas.ControlPlane.Application.Services.AIChatService>();
builder.Services.AddScoped<Atlas.ControlPlane.Application.Services.GovernanceAgentReasoningService>();

builder.Services.AddScoped<Atlas.ControlPlane.Application.Services.AnalysisOrchestrationService>(sp =>
    new Atlas.ControlPlane.Application.Services.AnalysisOrchestrationService(
        sp.GetRequiredService<IDbContextFactory<Atlas.ControlPlane.Infrastructure.Data.AtlasDbContext>>(),
        sp.GetRequiredService<Atlas.ControlPlane.Application.Services.AzureDiscoveryService>(),
        sp.GetRequiredService<Atlas.ControlPlane.Application.Services.ScoringService>(),
        sp.GetRequiredService<ILogger<Atlas.ControlPlane.Application.Services.AnalysisOrchestrationService>>(),
        sp.GetRequiredService<Atlas.ControlPlane.Application.Services.IAnalysisEventPublisher>(),
        sp.GetService<Atlas.ControlPlane.Application.Services.AIChatService>(),
        sp.GetService<Atlas.ControlPlane.Application.Services.IImpactFactorInsightService>(),
        sp.GetService<Atlas.ControlPlane.Application.Recommendations.IRecommendationGroundingClient>(),
        sp.GetService<Atlas.ControlPlane.Application.ValueTracking.IAzureMcpValueEvidenceClient>()));
builder.Services.AddHostedService<Atlas.ControlPlane.Application.Services.BackgroundAnalysisService>();
builder.Services.AddSingleton<A2AMessageValidator>();

// OpenTelemetry
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

builder.Logging.AddOpenTelemetry(options =>
{
    options.SetResourceBuilder(OpenTelemetryConfiguration.CreateResourceBuilder());
});

// Authentication - Entra ID JWT Bearer
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

// Authorization policies
var allowAnonymousRequested = EnvFlag("NIMBUSIQ_ALLOW_ANONYMOUS");
var allowAnonymousInProdFull =
    allowAnonymousRequested
    && builder.Environment.IsProduction()
    && EnvFlag("NIMBUSIQ_ALLOW_ANONYMOUS_IN_PROD_FULL");
var allowAnonymousReadOnlyInProduction =
    allowAnonymousRequested
    && builder.Environment.IsProduction()
    && EnvFlag("NIMBUSIQ_ALLOW_ANONYMOUS_IN_PROD_READONLY")
    && !allowAnonymousInProdFull;
var allowAnonymous = allowAnonymousRequested &&
    (!builder.Environment.IsProduction() || allowAnonymousInProdFull);

builder.Services.AddAuthorization(options =>
{
    // Allow a temporary dev-only bypass for read-only policies when the
    // environment variable `NIMBUSIQ_ALLOW_ANONYMOUS` is set to "true".
    // This is intended for quick end-to-end verification only — do NOT
    // enable in production.
    if (allowAnonymous)
    {
        // Bypass all auth policies for unauthenticated demo/dev access.
        // NIMBUSIQ_ALLOW_ANONYMOUS is intended for short-lived end-to-end verification only.
        // Do NOT enable in production with real customer data.
        options.AddPolicy("AnalysisRead", p => p.RequireAssertion(_ => true));
        options.AddPolicy("AnalysisWrite", p => p.RequireAssertion(_ => true));
        options.AddPolicy("RecommendationRead", p => p.RequireAssertion(_ => true));
        options.AddPolicy("RecommendationApprove", p => p.RequireAssertion(_ => true));
        options.AddPolicy("Admin", p => p.RequireAssertion(_ => true));
        options.AddPolicy("ServiceGroupDiscovery", p => p.RequireAssertion(_ => true));
    }
    else if (allowAnonymousReadOnlyInProduction)
    {
        // Emergency read-only production mode.
        // Allows dashboard/read APIs while keeping write/admin policies authenticated.
        options.AddPolicy("AnalysisRead", p => p.RequireAssertion(_ => true));
        options.AddPolicy("RecommendationRead", p => p.RequireAssertion(_ => true));
        options.AddPolicy("ServiceGroupDiscovery", p => p.RequireAssertion(_ => true));

        options.AddPolicy("AnalysisWrite", policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim("roles", "Atlas.Analysis.Write", "Atlas.Admin"));

        options.AddPolicy("RecommendationApprove", policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim("roles", "Atlas.Recommendation.Approve", "Atlas.Admin"));

        options.AddPolicy("Admin", policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim("roles", "Atlas.Admin"));
    }
    else
    {
        options.AddPolicy("AnalysisRead", policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim("roles", "Atlas.Analysis.Read", "Atlas.Admin"));

        options.AddPolicy("RecommendationRead", policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim("roles", "Atlas.Recommendation.Read", "Atlas.Admin"));

        // Write/admin policies require authentication when not in anonymous demo mode.
        options.AddPolicy("AnalysisWrite", policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim("roles", "Atlas.Analysis.Write", "Atlas.Admin"));

        options.AddPolicy("RecommendationApprove", policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim("roles", "Atlas.Recommendation.Approve", "Atlas.Admin"));

        options.AddPolicy("Admin", policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim("roles", "Atlas.Admin"));

        options.AddPolicy("ServiceGroupDiscovery", policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim("roles", "Atlas.Admin"));
    }
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(
            builder.Configuration["Frontend:Url"] ?? "http://localhost:5173",
            builder.Configuration["Frontend:ProductionUrl"] ?? "https://atlas.example.com"
        )
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials();
    });
});

// Health checks
builder.Services.AddSingleton<AiServiceHealthCheck>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AtlasDbContext>("database")
    .AddCheck<AiServiceHealthCheck>("ai_service", tags: ["ready"]);

// Rate limiting — per-user fixed-window policy to protect against abuse
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Per-user rate limiting: partition by authenticated user ID
    options.AddPolicy("per-user", httpContext =>
    {
        // Extract user identifier from claims (sub claim is standard for JWT)
        var userIdentifier = httpContext.User?.FindFirst("sub")?.Value
            ?? httpContext.User?.FindFirst("oid")?.Value  // Azure AD object ID
            ?? httpContext.User?.Identity?.Name
            ?? httpContext.Connection.RemoteIpAddress?.ToString()  // Fallback to IP for anonymous
            ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(userIdentifier, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 10
        });
    });
});

var app = builder.Build();

if (allowAnonymousRequested && !allowAnonymous)
{
    if (allowAnonymousReadOnlyInProduction)
    {
        app.Logger.LogWarning(
            "NIMBUSIQ_ALLOW_ANONYMOUS_IN_PROD_READONLY=true enabled: allowing read-only anonymous policies in production.");
    }
    else
    {
        app.Logger.LogWarning("Ignoring NIMBUSIQ_ALLOW_ANONYMOUS=true in production environment.");
    }
}
else if (allowAnonymousInProdFull)
{
    app.Logger.LogWarning(
        "NIMBUSIQ_ALLOW_ANONYMOUS_IN_PROD_FULL=true enabled: allowing full anonymous policies in production for demo mode.");
}

app.UseMiddleware<Atlas.ControlPlane.Api.Middleware.CorrelationIdMiddleware>();
app.UseMiddleware<Atlas.ControlPlane.Api.Middleware.AuditLogMiddleware>();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler(error => error.Run(async context =>
    {
        var ex = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledException");
        logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Internal Server Error",
            Detail = "An unexpected error occurred. Check server logs for details.",
            Extensions = { ["traceId"] = context.TraceIdentifier }
        });
    }));
}

app.UseHttpsRedirection();

// CORS must be before auth
app.UseCors("AllowFrontend");

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Health endpoints (bypass auth)
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live");
app.MapGet("/health/foundry", async (
    Atlas.ControlPlane.Application.Services.AIChatService aiService,
    CancellationToken cancellationToken) =>
{
    var status = await aiService.CheckConnectivityAsync(cancellationToken);
    return status.OverallState switch
    {
        "healthy" => Results.Ok(status),
        "degraded" => Results.Ok(status),
        _ => Results.Json(status, statusCode: StatusCodes.Status503ServiceUnavailable)
    };
});

// SignalR hub: register BEFORE RequireRateLimiting so it doesn't inherit the per-user policy.
// Rate limiting on SignalR connections would interfere with long-polling.
// Instead, the hub relies on [Authorize] at the class level for auth checks.
app.MapHub<Atlas.ControlPlane.Api.Hubs.AnalysisHub>("/hubs/analysis");

// All other controllers get per-user rate limiting
app.MapControllers().RequireRateLimiting("per-user");

static bool EnvFlag(string name) =>
    string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

// Optional: apply migrations on startup (default false)
if (EnvFlag("NIMBUSIQ_APPLY_MIGRATIONS_ON_STARTUP"))
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        // PendingModelChangesWarning or other migration issues should not block startup.
        app.Logger.LogWarning(ex, "Startup migration failed — app will continue with existing schema");
    }
}

// Optional: seed a default service group if DB is empty (default false)
if (EnvFlag("NIMBUSIQ_SEED_DEFAULT_SERVICE_GROUP"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();

    if (!await db.ServiceGroups.AnyAsync())
    {
        var subscriptionId = Environment.GetEnvironmentVariable("ATLAS_DEFAULT_SUBSCRIPTION_ID");
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            var sg = new Atlas.ControlPlane.Domain.Entities.ServiceGroup
            {
                Id = Guid.NewGuid(),
                ExternalKey = $"sg-{Guid.NewGuid():N}",
                Name = Environment.GetEnvironmentVariable("ATLAS_DEFAULT_SERVICE_GROUP_NAME") ?? "Default Service Group",
                Description = "Seeded service group scope for initial end-to-end validation.",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.ServiceGroups.Add(sg);
            db.ServiceGroupScopes.Add(new Atlas.ControlPlane.Domain.Entities.ServiceGroupScope
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = sg.Id,
                SubscriptionId = subscriptionId,
                ResourceGroup = Environment.GetEnvironmentVariable("ATLAS_DEFAULT_RESOURCE_GROUP_NAME"),
                ScopeFilter = Environment.GetEnvironmentVariable("ATLAS_DEFAULT_SCOPE_FILTER"),
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }
    }
}

app.Run();

/// <summary>
/// Reports AI Foundry availability as a degraded (not unhealthy) health signal,
/// since the system falls back to rule-based processing when AI is unavailable.
/// </summary>
internal sealed class AiServiceHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly Atlas.ControlPlane.Application.Services.AIChatService? _aiService;

    public AiServiceHealthCheck(IServiceProvider sp)
    {
        _aiService = sp.GetService<Atlas.ControlPlane.Application.Services.AIChatService>();
    }

    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var result = _aiService?.IsAIAvailable == true
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("AI Foundry connected")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded("AI Foundry unavailable — using rule-based fallback");

        return Task.FromResult(result);
    }
}
