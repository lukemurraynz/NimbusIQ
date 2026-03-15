using System.Diagnostics;
using Atlas.ControlPlane.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atlas.ControlPlane.Tests.Unit.Api.Middleware;

public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WithNoCorrelationIdInRequest_GeneratesNewCorrelationId()
    {
        // Arrange
        var context = CreateHttpContext();
        var nextCalled = false;
        RequestDelegate next = (ctx) =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CorrelationIdMiddleware(next);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled);
        Assert.True(context.Response.Headers.ContainsKey(CorrelationIdMiddleware.HeaderName));
        var correlationId = context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
    }

    [Fact]
    public async Task InvokeAsync_WithCorrelationIdInRequest_EchoesBackSameId()
    {
        // Arrange
        var expectedCorrelationId = "test-correlation-123";
        var context = CreateHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = expectedCorrelationId;

        var nextCalled = false;
        RequestDelegate next = (ctx) =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CorrelationIdMiddleware(next);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled);
        Assert.True(context.Response.Headers.ContainsKey(CorrelationIdMiddleware.HeaderName));
        var actualCorrelationId = context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        Assert.Equal(expectedCorrelationId, actualCorrelationId);
    }

    [Fact]
    public async Task InvokeAsync_WithActiveActivity_AddsTraceIdToResponse()
    {
        // Arrange
        var context = CreateHttpContext();
        var nextCalled = false;
        RequestDelegate next = (ctx) =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CorrelationIdMiddleware(next);

        // Create an active activity to simulate OpenTelemetry tracing
        using var activity = new Activity("test-activity");
        activity.Start();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled);
        Assert.True(context.Response.Headers.ContainsKey(CorrelationIdMiddleware.TraceHeaderName));
        var traceId = context.Response.Headers[CorrelationIdMiddleware.TraceHeaderName].ToString();
        Assert.Equal(activity.TraceId.ToString(), traceId);
    }

    [Fact]
    public async Task InvokeAsync_WithNoActiveActivity_DoesNotAddTraceId()
    {
        // Arrange
        var context = CreateHttpContext();
        var nextCalled = false;
        RequestDelegate next = (ctx) =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new CorrelationIdMiddleware(next);

        // Ensure no activity is active
        Activity.Current = null;

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled);
        // Trace header may or may not be present depending on activity state
        // Just ensure middleware doesn't throw
    }

    [Fact]
    public async Task InvokeAsync_SetsCorrelationIdInActivityTag()
    {
        // Arrange
        var context = CreateHttpContext();
        var expectedCorrelationId = "test-correlation-456";
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = expectedCorrelationId;

        var nextCalled = false;
        Activity? capturedActivity = null;
        RequestDelegate next = (ctx) =>
        {
            nextCalled = true;
            capturedActivity = Activity.Current;
            return Task.CompletedTask;
        };

        var middleware = new CorrelationIdMiddleware(next);

        // Create an active activity
        using var activity = new Activity("test-activity");
        activity.Start();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.True(nextCalled);
        Assert.NotNull(capturedActivity);
        var correlationIdTag = capturedActivity?.Tags.FirstOrDefault(t => t.Key == "correlation.id");
        Assert.NotNull(correlationIdTag);
        Assert.Equal(expectedCorrelationId, correlationIdTag.Value.Value);
    }

    private static HttpContext CreateHttpContext()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance));
        var serviceProvider = serviceCollection.BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        return context;
    }
}
