using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NJsonSchema;

namespace Atlas.AgentOrchestrator.Contracts;

/// <summary>
/// Validates Agent-to-Agent (A2A) messages against the JSON schema.
/// Schema location: specs/001-service-group-scoped/contracts/a2a-message.schema.json
/// </summary>
public class A2AMessageValidator
{
    private readonly JsonSchema _schema;
    private readonly ILogger<A2AMessageValidator> _logger;
    private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.A2AValidation");

    public A2AMessageValidator(ILogger<A2AMessageValidator> logger)
    {
        _logger = logger;
        var (schemaJson, resourceName) = LoadEmbeddedSchema();
        _schema = JsonSchema.FromJsonAsync(schemaJson).GetAwaiter().GetResult();
        _logger.LogInformation("A2A message schema loaded from embedded resource: {SchemaResource}", resourceName);
    }

    private static (string Json, string ResourceName) LoadEmbeddedSchema()
    {
        var assembly = typeof(A2AMessageValidator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("a2a-message.schema.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException(
                "A2A schema embedded resource not found. Ensure specs/001-service-group-scoped/contracts/a2a-message.schema.json is embedded.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Failed to load embedded A2A schema resource: {resourceName}");
        }

        using var reader = new StreamReader(stream);
        return (reader.ReadToEnd(), resourceName);
    }

    /// <summary>
    /// Validates an A2A message against the schema.
    /// </summary>
    /// <param name="message">The message object to validate</param>
    /// <returns>Validation result with errors if any</returns>
    public A2AValidationResult Validate(A2AMessage message)
    {
        using var activity = ActivitySource.StartActivity("A2A.Validate");
        activity?.SetTag("message.type", message.MessageType);
        activity?.SetTag("sender.agent", message.SenderAgent);

        try
        {
            var messageJson = JsonSerializer.Serialize(message);
            var result = ValidateJson(messageJson);

            activity?.SetTag("validation.is_valid", result.IsValid);
            activity?.SetTag("validation.error_count", result.Errors.Count);

            if (!result.IsValid)
            {
                _logger.LogWarning(
                    "A2A message validation failed. MessageId: {MessageId}, Errors: {ErrorCount}",
                    message.MessageId,
                    result.Errors.Count);

                foreach (var error in result.Errors)
                {
                    _logger.LogDebug("Validation error: {Path} - {Kind}: {Message}",
                        error.Path,
                        error.Kind,
                        error.Message);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "A2A message validation threw exception. MessageId: {MessageId}", message.MessageId);

            return new A2AValidationResult
            {
                IsValid = false,
                Errors = new List<A2AValidationError>
                {
                    new()
                    {
                        Path = string.Empty,
                        Message = $"Validation exception: {ex.Message}",
                        Kind = "Exception"
                    }
                }
            };
        }
    }

    /// <summary>
    /// Validates a raw JSON string against the A2A schema.
    /// </summary>
    public A2AValidationResult ValidateJson(string messageJson)
    {
        using var activity = ActivitySource.StartActivity("A2A.ValidateJson");

        try
        {
            var errors = _schema.Validate(messageJson);

            var isValid = errors.Count == 0;
            activity?.SetTag("validation.is_valid", isValid);
            activity?.SetTag("validation.error_count", errors.Count);

            return new A2AValidationResult
            {
                IsValid = isValid,
                Errors = errors.Select(e => new A2AValidationError
                {
                    Path = e.Path ?? string.Empty,
                    Message = e.ToString(),
                    Kind = e.Kind.ToString()
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "A2A JSON validation threw exception");

            return new A2AValidationResult
            {
                IsValid = false,
                Errors = new List<A2AValidationError>
                {
                    new()
                    {
                        Path = string.Empty,
                        Message = $"Validation exception: {ex.Message}",
                        Kind = "Exception"
                    }
                }
            };
        }
    }

}

/// <summary>
/// Result of A2A message validation.
/// </summary>
public class A2AValidationResult
{
    public required bool IsValid { get; init; }
    public required List<A2AValidationError> Errors { get; init; }
}

/// <summary>
/// Individual validation error.
/// </summary>
public class A2AValidationError
{
    public required string Path { get; init; }
    public required string Message { get; init; }
    public required string Kind { get; init; }
}
