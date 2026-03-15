namespace Atlas.AgentOrchestrator.Integrations.Prompts;

public interface IPromptProvider
{
  string Render(string templateName, IReadOnlyDictionary<string, string>? placeholders = null);
}
