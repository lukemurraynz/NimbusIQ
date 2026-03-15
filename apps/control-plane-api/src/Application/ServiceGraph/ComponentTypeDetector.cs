namespace Atlas.ControlPlane.Application.ServiceGraph;

public class ComponentTypeDetector
{
    private static readonly (string Type, string[] Patterns)[] TypePatterns =
    {
        ("database", new[] { "sql", "postgres", "mysql", "mariadb", "cosmos", "mongo", "redis", "db" }),
        ("cache", new[] { "redis", "cache" }),
        ("messaging", new[] { "servicebus", "eventhub", "eventgrid", "queue", "topic" }),
        ("gateway", new[] { "apim", "frontdoor", "applicationgateway", "gateway", "ingress" }),
        ("frontend", new[] { "frontend", "web", "ui", "spa", "portal" }),
        ("backend", new[] { "api", "worker", "function", "service", "orchestrator" }),
        ("compute", new[] { "containerapp", "kubernetes", "aks", "vm", "compute", "appservice" }),
        ("storage", new[] { "storage", "blob", "file", "disk", "datalake" }),
        ("network", new[] { "vnet", "subnet", "firewall", "privateendpoint", "loadbalancer", "dns" }),
        ("security", new[] { "keyvault", "defender", "policy", "identity", "entra" }),
        ("monitoring", new[] { "monitor", "loganalytics", "appinsights", "telemetry", "alerts" })
    };

    public string Detect(string? resourceType, string? resourceName)
    {
        var haystack = $"{resourceType ?? string.Empty} {resourceName ?? string.Empty}".ToLowerInvariant();

        foreach (var (type, patterns) in TypePatterns)
        {
            if (patterns.Any(pattern => haystack.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return type;
            }
        }

        return "unknown";
    }
}
