using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Atlas.AgentOrchestrator.Integrations.Prompts;

public sealed class FilePromptProvider : IPromptProvider
{
  private static readonly Regex PlaceholderRegex = new("\\{\\{\\s*([A-Za-z0-9_]+)\\s*\\}\\}", RegexOptions.Compiled);

  private readonly ILogger<FilePromptProvider> _logger;
  private readonly string _promptDirectory;
  private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

  public FilePromptProvider(
      IOptions<PromptProviderOptions> options,
      ILogger<FilePromptProvider> logger)
  {
    _logger = logger;
    _promptDirectory = ResolvePromptDirectory(options.Value.Directory);

    _logger.LogInformation("Prompt provider initialized. Directory: {PromptDirectory}", _promptDirectory);
  }

  public string Render(string templateName, IReadOnlyDictionary<string, string>? placeholders = null)
  {
    if (string.IsNullOrWhiteSpace(templateName))
    {
      throw new ArgumentException("Template name is required.", nameof(templateName));
    }

    var normalizedName = templateName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        ? templateName
        : $"{templateName}.md";

    var template = _cache.GetOrAdd(normalizedName, LoadTemplate);
    if (placeholders is null || placeholders.Count == 0)
    {
      return template;
    }

    return PlaceholderRegex.Replace(template, match =>
    {
      var key = match.Groups[1].Value;
      return placeholders.TryGetValue(key, out var value) ? value : string.Empty;
    });
  }

  private string LoadTemplate(string templateName)
  {
    var path = Path.Combine(_promptDirectory, templateName);
    if (!File.Exists(path))
    {
      throw new FileNotFoundException($"Prompt template not found: {templateName}", path);
    }

    return File.ReadAllText(path);
  }

  private static string ResolvePromptDirectory(string? configuredPath)
  {
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
      var configuredFullPath = Path.GetFullPath(configuredPath);
      if (Directory.Exists(configuredFullPath))
      {
        return configuredFullPath;
      }
    }

    var outputPromptsPath = Path.Combine(AppContext.BaseDirectory, "prompts");
    if (Directory.Exists(outputPromptsPath))
    {
      return outputPromptsPath;
    }

    throw new DirectoryNotFoundException(
        "Prompt directory was not found. Configure Prompting:Directory or ensure prompts are copied to output.");
  }
}
