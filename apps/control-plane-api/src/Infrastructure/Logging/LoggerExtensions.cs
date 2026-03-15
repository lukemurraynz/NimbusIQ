using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Infrastructure.Logging;

public static partial class LoggerExtensions
{
    private static readonly string[] SensitiveParameters =
    [
        "password",
        "token",
        "secret",
        "key",
        "credential",
        "connectionstring",
        "apikey",
        "bearer",
        "authorization",
        "sas"
    ];

    [GeneratedRegex(@"([?&])([^=&]+)=([^&]*)", RegexOptions.Compiled)]
    private static partial Regex QueryParamRegex();

    public static string SanitizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return QueryParamRegex().Replace(url, static match =>
        {
            var separator = match.Groups[1].Value;
            var parameterName = match.Groups[2].Value;

            foreach (var sensitiveParameter in SensitiveParameters)
            {
                if (parameterName.Contains(sensitiveParameter, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{separator}{parameterName}=***REDACTED***";
                }
            }

            return match.Value;
        });
    }

    public static void LogHttpRequest(this ILogger logger, string method, string url, LogLevel level = LogLevel.Information)
    {
        var sanitizedUrl = SanitizeUrl(url);
        logger.Log(level, "HTTP {Method} {Url}", method, sanitizedUrl);
    }
}
