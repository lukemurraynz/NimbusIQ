namespace Atlas.AgentOrchestrator.Integrations.Prompts;

public sealed class PromptProviderOptions
{
  public const string SectionName = "Prompting";

  /// <summary>
  /// Optional absolute/relative directory containing prompt templates.
  /// If not set, provider uses "prompts" copied to output directory.
  /// </summary>
  public string? Directory { get; set; }
}
