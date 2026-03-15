using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Atlas.AgentOrchestrator.Integrations.MCP;

/// <summary>
/// Resilience wrapper for MCP client calls with timeout budgets, exponential backoff retries, and circuit-breaker patterns.
/// Ensures graceful degradation when MCP servers are slow, unavailable, or experiencing transient failures.
/// </summary>
public sealed class MafMcpResilienceWrapper
{
    private readonly ILogger<MafMcpResilienceWrapper> _logger;
    private readonly ResilienceOptions _learnMcpOptions;
    private readonly ResilienceOptions _azureMcpOptions;

    /// <summary>
    /// Configuration options for resilience behavior.
    /// </summary>
    public sealed class ResilienceOptions
    {
        /// <summary>
        /// Per-call timeout in milliseconds. Default: 5000 (5 seconds).
        /// </summary>
        public int TimeoutMilliseconds { get; set; } = 5000;

        /// <summary>
        /// Number of retry attempts before circuit-breaker opens. Default: 3.
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 3;

        /// <summary>
        /// Initial retry delay in milliseconds (exponential backoff multiplier). Default: 200.
        /// </summary>
        public int InitialRetryDelayMilliseconds { get; set; } = 200;

        /// <summary>
        /// Number of failures before circuit opens. Default: 5.
        /// </summary>
        public int CircuitBreakerFailureThreshold { get; set; } = 5;

        /// <summary>
        /// Time in milliseconds to keep circuit open before attempting half-open. Default: 10000 (10 seconds).
        /// </summary>
        public int CircuitBreakerOpenDurationMilliseconds { get; set; } = 10000;
    }

    public MafMcpResilienceWrapper(
        ILogger<MafMcpResilienceWrapper> logger,
        ResilienceOptions? learnMcpOptions = null,
        ResilienceOptions? azureMcpOptions = null)
    {
        _logger = logger;
        _learnMcpOptions = learnMcpOptions ?? new ResilienceOptions();
        _azureMcpOptions = azureMcpOptions ?? new ResilienceOptions();
    }

    /// <summary>
    /// Executes a Learn MCP operation with resilience.
    /// </summary>
    public async Task<T> ExecuteLearnMcpAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(operationName);

        _logger.LogDebug("Executing Learn MCP operation: {OperationName}", operationName);

        var policy = BuildPolicy<T>("Learn MCP", _learnMcpOptions, _logger);
        try
        {
            return await policy.ExecuteAsync(
                async _ => await operation(cancellationToken),
                cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogError(
                ex,
                "Learn MCP circuit breaker is open for operation {OperationName}. " +
                "Service may be experiencing sustained issues.",
                operationName);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Learn MCP operation {OperationName} was canceled.",
                operationName);
            throw;
        }
    }

    /// <summary>
    /// Executes an Azure MCP operation with resilience.
    /// </summary>
    public async Task<T> ExecuteAzureMcpAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(operationName);

        _logger.LogDebug("Executing Azure MCP operation: {OperationName}", operationName);

        var policy = BuildPolicy<T>("Azure MCP", _azureMcpOptions, _logger);
        try
        {
            return await policy.ExecuteAsync(
                async _ => await operation(cancellationToken),
                cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogError(
                ex,
                "Azure MCP circuit breaker is open for operation {OperationName}. " +
                "Service may be experiencing sustained issues.",
                operationName);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Azure MCP operation {OperationName} was canceled.",
                operationName);
            throw;
        }
    }

    private static AsyncPolicy<T> BuildPolicy<T>(
        string mcpServerName,
        ResilienceOptions options,
        ILogger<MafMcpResilienceWrapper> logger)
    {
        // Timeout policy: fail-fast if operation takes too long
        var timeoutPolicy = Policy.TimeoutAsync<T>(
            TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
            TimeoutStrategy.Pessimistic);

        // Retry policy: exponential backoff on transient failures
        var retryPolicy = Policy<T>
            .Handle<HttpRequestException>()
            .Or<OperationCanceledException>()
            .OrResult(static r => r == null)
            .WaitAndRetryAsync(
                retryCount: options.MaxRetryAttempts,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromMilliseconds(
                        options.InitialRetryDelayMilliseconds * Math.Pow(2, attempt - 1)),
                onRetry: (outcome, duration, attempt, context) =>
                {
                    logger.LogWarning(
                        "Retry {Attempt} of {MaxAttempts} for {Mcp} after {DelayMs}ms. " +
                        "Error: {Error}",
                        attempt,
                        options.MaxRetryAttempts,
                        mcpServerName,
                        (int)duration.TotalMilliseconds,
                        outcome.Exception?.Message ?? "Transient failure");
                });

        // Circuit breaker policy: protect against sustained service degradation
        var circuitBreakerPolicy = Policy<T>
            .Handle<HttpRequestException>()
            .OrResult(static r => r == null)
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: options.CircuitBreakerFailureThreshold,
                durationOfBreak: TimeSpan.FromMilliseconds(options.CircuitBreakerOpenDurationMilliseconds),
                onBreak: (outcome, duration, context) =>
                {
                    logger.LogError(
                        "Circuit breaker opened for {Mcp}. " +
                        "Will retry after {DurationSeconds}s. " +
                        "Last error: {Error}",
                        mcpServerName,
                        (int)duration.TotalSeconds,
                        outcome.Exception?.Message ?? "Transient failure");
                },
                onReset: context =>
                {
                    logger.LogInformation(
                        "Circuit breaker reset for {Mcp}. " +
                        "Service appears healthy; resuming calls.",
                        mcpServerName);
                });

        // Combine policies: timeout → circuit breaker → retry
        return Policy.WrapAsync(timeoutPolicy, circuitBreakerPolicy, retryPolicy);
    }
}
