using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// T5.1: Cloud Native Maturity Agent - Assesses containerization and modern architecture adoption
/// Evaluates Kubernetes best practices, microservices patterns, service mesh readiness, observability maturity
/// Inspired by CNCF Maturity Model and Azure Well-Architected Framework
/// </summary>
public class CloudNativeMaturityAgent
{
    private readonly ILogger<CloudNativeMaturityAgent> _logger;

    public CloudNativeMaturityAgent(ILogger<CloudNativeMaturityAgent> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Assess cloud-native maturity across multiple dimensions
    /// </summary>
    public async Task<CloudNativeMaturityResult> AssessAsync(
        CloudNativeContext context,
        CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        activity?.SetTag("cloudnative.serviceGroupId", context.ServiceGroupId);
        activity?.SetTag("cloudnative.resourceCount", context.Resources.Count);

        try
        {
            // Assess each dimension
            var containerization = AssessContainerization(context);
            var microservices = AssessMicroservicesAdoption(context);
            var observability = AssessObservability(context);
            var serviceMesh = AssessServiceMeshReadiness(context);
            var cicd = AssessCICDMaturity(context);
            var apiManagement = AssessAPIManagement(context);

            // Calculate overall maturity level
            var overallScore = CalculateOverallScore(
                containerization.Score,
                microservices.Score,
                observability.Score,
                serviceMesh.Score,
                cicd.Score,
                apiManagement.Score);

            var maturityLevel = DetermineMaturityLevel(overallScore);

            var result = new CloudNativeMaturityResult
            {
                ServiceGroupId = context.ServiceGroupId,
                AssessedAt = DateTime.UtcNow,
                OverallScore = overallScore,
                MaturityLevel = maturityLevel,
                Containerization = containerization,
                Microservices = microservices,
                Observability = observability,
                ServiceMesh = serviceMesh,
                CICD = cicd,
                APIManagement = apiManagement,
                RecommendedNextSteps = GenerateNextSteps(maturityLevel, containerization, microservices, observability)
            };

            _logger.LogInformation(
                "Cloud-native maturity assessment complete for service group {ServiceGroupId}: Overall score={Score:F1}, Level={Level}, Next steps={NextStepCount}",
                context.ServiceGroupId,
                overallScore,
                maturityLevel,
                result.RecommendedNextSteps.Count);

            activity?.SetTag("cloudnative.maturityLevel", maturityLevel);
            activity?.SetTag("cloudnative.overallScore", overallScore);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to assess cloud-native maturity for service group {ServiceGroupId}", context.ServiceGroupId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Assess containerization adoption
    /// Level 1: No containers
    /// Level 2: Some containers in dev/test
    /// Level 3: Containers in production with orchestration
    /// Level 4: Multi-cluster with advanced patterns
    /// Level 5: Full cloud-native with service mesh
    /// </summary>
    private MaturityDimension AssessContainerization(CloudNativeContext context)
    {
        var dimension = new MaturityDimension { Name = "Containerization" };

        var containerResources = context.Resources
            .Where(r => IsContainerResource(r.ResourceType))
            .ToList();

        var aksResources = context.Resources
            .Where(r => r.ResourceType.Contains("managedClusters", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var containerAppsResources = context.Resources
            .Where(r => r.ResourceType.Contains("containerApps", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!containerResources.Any())
        {
            dimension.Score = 0;
            dimension.Level = "Level 1: No Containerization";
            dimension.Findings.Add("No container-based resources detected");
            dimension.Recommendations.Add("Consider containerizing stateless applications");
        }
        else if (aksResources.Any() || containerAppsResources.Any())
        {
            var hasMultiCluster = aksResources.Count > 1;
            var hasAdvancedNetworking = aksResources.Any(r => HasAdvancedNetworking(r));

            if (hasMultiCluster && hasAdvancedNetworking)
            {
                dimension.Score = 90;
                dimension.Level = "Level 4: Advanced Container Orchestration";
                dimension.Findings.Add($"{aksResources.Count} AKS clusters with advanced networking");
            }
            else
            {
                dimension.Score = 70;
                dimension.Level = "Level 3: Production Container Orchestration";
                dimension.Findings.Add($"{containerResources.Count} container resources detected");
                dimension.Recommendations.Add("Consider multi-cluster for high availability");
            }
        }
        else
        {
            dimension.Score = 40;
            dimension.Level = "Level 2: Basic Containerization";
            dimension.Findings.Add("Containers in use but no orchestration platform");
            dimension.Recommendations.Add("Adopt AKS or Container Apps for orchestration");
        }

        return dimension;
    }

    /// <summary>
    /// Assess microservices adoption patterns
    /// </summary>
    private MaturityDimension AssessMicroservicesAdoption(CloudNativeContext context)
    {
        var dimension = new MaturityDimension { Name = "Microservices Architecture" };

        var apiResources = context.Resources
            .Where(r => r.ResourceType.Contains("sites", StringComparison.OrdinalIgnoreCase) ||
                       r.ResourceType.Contains("containerApps", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hasAPIGateway = context.Resources
            .Any(r => r.ResourceType.Contains("ApiManagement", StringComparison.OrdinalIgnoreCase) ||
                     r.ResourceType.Contains("ApplicationGateway", StringComparison.OrdinalIgnoreCase));

        var hasServiceBus = context.Resources
            .Any(r => r.ResourceType.Contains("ServiceBus", StringComparison.OrdinalIgnoreCase));

        if (apiResources.Count == 0)
        {
            dimension.Score = 0;
            dimension.Level = "Level 1: Monolithic";
            dimension.Findings.Add("No API or microservice resources detected");
        }
        else if (apiResources.Count >= 5 && hasAPIGateway && hasServiceBus)
        {
            dimension.Score = 85;
            dimension.Level = "Level 4: Mature Microservices";
            dimension.Findings.Add($"{apiResources.Count} microservices with API gateway and async messaging");
            dimension.Recommendations.Add("Consider event-driven patterns and CQRS");
        }
        else if (apiResources.Count >= 3 && hasAPIGateway)
        {
            dimension.Score = 65;
            dimension.Level = "Level 3: Evolving Microservices";
            dimension.Findings.Add($"{apiResources.Count} services with API management");
            dimension.Recommendations.Add("Add async messaging with Service Bus or Event Grid");
        }
        else
        {
            dimension.Score = 40;
            dimension.Level = "Level 2: Service-Oriented";
            dimension.Findings.Add($"{apiResources.Count} independent services");
            dimension.Recommendations.Add("Implement API gateway for unified entry point");
        }

        return dimension;
    }

    /// <summary>
    /// Assess observability maturity
    /// Metrics, Logs, Traces (OpenTelemetry), Distributed tracing
    /// </summary>
    private MaturityDimension AssessObservability(CloudNativeContext context)
    {
        var dimension = new MaturityDimension { Name = "Observability" };

        var hasAppInsights = context.Resources
            .Any(r => r.ResourceType.Contains("microsoft.insights/components", StringComparison.OrdinalIgnoreCase));

        var hasLogAnalytics = context.Resources
            .Any(r => r.ResourceType.Contains("Microsoft.OperationalInsights", StringComparison.OrdinalIgnoreCase));

        var hasPrometheus = context.Resources
            .Any(r => r.Tags.ContainsKey("monitoring") && r.Tags["monitoring"].Contains("prometheus", StringComparison.OrdinalIgnoreCase));

        var hasGrafana = context.Resources
            .Any(r => r.ResourceType.Contains("Grafana", StringComparison.OrdinalIgnoreCase));

        var observabilityCount = new[] { hasAppInsights, hasLogAnalytics, hasPrometheus, hasGrafana }.Count(x => x);

        if (observabilityCount == 0)
        {
            dimension.Score = 10;
            dimension.Level = "Level 1: Limited Observability";
            dimension.Findings.Add("No observability platform detected");
            dimension.Recommendations.Add("Implement Application Insights for basic monitoring");
        }
        else if (observabilityCount >= 3 && hasAppInsights)
        {
            dimension.Score = 90;
            dimension.Level = "Level 5: Comprehensive Observability";
            dimension.Findings.Add("Multiple observability tools with distributed tracing");
            dimension.Recommendations.Add("Implement OpenTelemetry for vendor-neutral instrumentation");
        }
        else if (hasAppInsights && hasLogAnalytics)
        {
            dimension.Score = 70;
            dimension.Level = "Level 3: Structured Observability";
            dimension.Findings.Add("Application Insights and Log Analytics configured");
            dimension.Recommendations.Add("Add Prometheus for Kubernetes metrics");
        }
        else
        {
            dimension.Score = 40;
            dimension.Level = "Level 2: Basic Monitoring";
            dimension.Findings.Add("Basic monitoring in place");
            dimension.Recommendations.Add("Centralize logs with Log Analytics");
        }

        return dimension;
    }

    /// <summary>
    /// Assess service mesh readiness
    /// Istio, Linkerd, OSM patterns
    /// </summary>
    private MaturityDimension AssessServiceMeshReadiness(CloudNativeContext context)
    {
        var dimension = new MaturityDimension { Name = "Service Mesh" };

        var aksResources = context.Resources
            .Where(r => r.ResourceType.Contains("managedClusters", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Check for service mesh indicators
        var hasServiceMesh = aksResources.Any(r =>
            r.Tags.ContainsKey("serviceMesh") ||
            r.Properties.ContainsKey("serviceMeshProfile"));

        if (!aksResources.Any())
        {
            dimension.Score = 0;
            dimension.Level = "N/A: No Kubernetes Clusters";
            dimension.Findings.Add("Service mesh requires Kubernetes");
        }
        else if (hasServiceMesh)
        {
            dimension.Score = 80;
            dimension.Level = "Level 4: Service Mesh Adopted";
            dimension.Findings.Add("Service mesh detected in AKS cluster");
            dimension.Recommendations.Add("Implement mTLS for service-to-service communication");
        }
        else if (aksResources.Count > 0 && aksResources.All(r => HasAdvancedNetworking(r)))
        {
            dimension.Score = 50;
            dimension.Level = "Level 2: Ready for Service Mesh";
            dimension.Findings.Add("Advanced networking configured, ready for service mesh");
            dimension.Recommendations.Add("Evaluate Istio or Open Service Mesh for traffic management");
        }
        else
        {
            dimension.Score = 20;
            dimension.Level = "Level 1: Not Ready";
            dimension.Findings.Add("Basic AKS configuration, service mesh not recommended yet");
            dimension.Recommendations.Add("Focus on microservices patterns first");
        }

        return dimension;
    }

    /// <summary>
    /// Assess CI/CD maturity
    /// </summary>
    private MaturityDimension AssessCICDMaturity(CloudNativeContext context)
    {
        var dimension = new MaturityDimension { Name = "CI/CD Pipeline Maturity" };

        // Check for CI/CD indicators in tags
        var hasCICD = context.Resources.Any(r =>
            r.Tags.ContainsKey("deploymentMethod") &&
            (r.Tags["deploymentMethod"].Contains("pipeline", StringComparison.OrdinalIgnoreCase) ||
             r.Tags["deploymentMethod"].Contains("github-actions", StringComparison.OrdinalIgnoreCase)));

        var hasGitOps = context.Resources.Any(r =>
            r.Tags.ContainsKey("gitops") || r.Tags.ContainsKey("flux"));

        if (hasGitOps)
        {
            dimension.Score = 95;
            dimension.Level = "Level 5: GitOps";
            dimension.Findings.Add("GitOps patterns detected");
            dimension.Recommendations.Add("Ensure policy-as-code with OPA or Kyverno");
        }
        else if (hasCICD)
        {
            dimension.Score = 70;
            dimension.Level = "Level 3: Automated CI/CD";
            dimension.Findings.Add("Automated deployment pipelines in use");
            dimension.Recommendations.Add("Consider GitOps for declarative deployments");
        }
        else
        {
            dimension.Score = 30;
            dimension.Level = "Level 2: Manual or Basic Automation";
            dimension.Findings.Add("Limited CI/CD automation detected");
            dimension.Recommendations.Add("Implement GitHub Actions or Azure Pipelines");
        }

        return dimension;
    }

    /// <summary>
    /// Assess API management maturity
    /// </summary>
    private MaturityDimension AssessAPIManagement(CloudNativeContext context)
    {
        var dimension = new MaturityDimension { Name = "API Management" };

        var hasAPIManagement = context.Resources
            .Any(r => r.ResourceType.Contains("ApiManagement", StringComparison.OrdinalIgnoreCase));

        var hasApplicationGateway = context.Resources
            .Any(r => r.ResourceType.Contains("ApplicationGateway", StringComparison.OrdinalIgnoreCase));

        if (hasAPIManagement)
        {
            dimension.Score = 85;
            dimension.Level = "Level 4: Enterprise API Management";
            dimension.Findings.Add("Azure API Management configured");
            dimension.Recommendations.Add("Implement API versioning and rate limiting policies");
        }
        else if (hasApplicationGateway)
        {
            dimension.Score = 60;
            dimension.Level = "Level 3: Gateway with Basic Routing";
            dimension.Findings.Add("Application Gateway providing routing");
            dimension.Recommendations.Add("Consider Azure API Management for advanced features");
        }
        else
        {
            dimension.Score = 20;
            dimension.Level = "Level 1: Direct API Exposure";
            dimension.Findings.Add("No centralized API gateway");
            dimension.Recommendations.Add("Implement Application Gateway or Azure API Management");
        }

        return dimension;
    }

    private decimal CalculateOverallScore(params decimal[] scores)
    {
        // Weighted average: Containerization and Microservices weighted higher
        var weights = new[] { 1.5m, 1.5m, 1.2m, 0.8m, 1.0m, 1.0m }; // Matching order of dimensions

        var weightedSum = scores.Zip(weights, (score, weight) => score * weight).Sum();
        var totalWeight = weights.Sum();

        return weightedSum / totalWeight;
    }

    private string DetermineMaturityLevel(decimal overallScore)
    {
        return overallScore switch
        {
            >= 80 => "Level 5: Cloud Native",
            >= 60 => "Level 4: Cloud Optimized",
            >= 40 => "Level 3: Cloud Friendly",
            >= 20 => "Level 2: Cloud Ready",
            _ => "Level 1: Traditional"
        };
    }

    private List<NextStep> GenerateNextSteps(
        string maturityLevel,
        MaturityDimension containerization,
        MaturityDimension microservices,
        MaturityDimension observability)
    {
        var steps = new List<NextStep>();

        // Prioritize based on current maturity
        if (containerization.Score < 50)
        {
            steps.Add(new NextStep
            {
                Priority = 1,
                Title = "Adopt Container Orchestration",
                Description = "Migrate workloads to AKS or Azure Container Apps",
                EstimatedEffort = "High",
                ExpectedImpact = "Improved scalability and resource efficiency"
            });
        }

        if (observability.Score < 60)
        {
            steps.Add(new NextStep
            {
                Priority = 2,
                Title = "Implement Comprehensive Observability",
                Description = "Deploy Application Insights with distributed tracing",
                EstimatedEffort = "Medium",
                ExpectedImpact = "Faster incident resolution and better performance insights"
            });
        }

        if (microservices.Score < 50 && containerization.Score >= 50)
        {
            steps.Add(new NextStep
            {
                Priority = 3,
                Title = "Decompose into Microservices",
                Description = "Identify domain boundaries and extract services",
                EstimatedEffort = "High",
                ExpectedImpact = "Improved agility and independent scaling"
            });
        }

        return steps.OrderBy(s => s.Priority).ToList();
    }

    private bool IsContainerResource(string resourceType)
    {
        var containerTypes = new[] { "containerApps", "managedClusters", "containerInstances", "containerRegistry" };
        return containerTypes.Any(t => resourceType.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private bool HasAdvancedNetworking(CloudNativeResourceInfo resource)
    {
        // Check for CNI, network policies, private clusters
        return resource.Properties.ContainsKey("networkProfile") ||
               resource.Tags.ContainsKey("networkPolicy");
    }
}

// DTOs
public class CloudNativeContext
{
    public Guid ServiceGroupId { get; set; }
    public List<CloudNativeResourceInfo> Resources { get; set; } = new();
}

public class CloudNativeResourceInfo
{
    public required string ResourceId { get; set; }
    public required string ResourceType { get; set; }
    public required string ResourceName { get; set; }
    public Dictionary<string, object> Properties { get; set; } = new();
    public Dictionary<string, string> Tags { get; set; } = new();
}

public class CloudNativeMaturityResult
{
    public Guid ServiceGroupId { get; set; }
    public DateTime AssessedAt { get; set; }
    public decimal OverallScore { get; set; }
    public required string MaturityLevel { get; set; }
    public MaturityDimension Containerization { get; set; } = null!;
    public MaturityDimension Microservices { get; set; } = null!;
    public MaturityDimension Observability { get; set; } = null!;
    public MaturityDimension ServiceMesh { get; set; } = null!;
    public MaturityDimension CICD { get; set; } = null!;
    public MaturityDimension APIManagement { get; set; } = null!;
    public List<NextStep> RecommendedNextSteps { get; set; } = new();
}

public class MaturityDimension
{
    public required string Name { get; set; }
    public decimal Score { get; set; }
    public string Level { get; set; } = "Not Assessed";
    public List<string> Findings { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

public class NextStep
{
    public int Priority { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string EstimatedEffort { get; set; }
    public required string ExpectedImpact { get; set; }
}
