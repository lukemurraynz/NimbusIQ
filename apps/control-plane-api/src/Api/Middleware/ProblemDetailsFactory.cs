using Microsoft.AspNetCore.Mvc;

namespace Atlas.ControlPlane.Api.Middleware;

/// <summary>
/// RFC 9457 Problem Details factory for consistent error responses
/// </summary>
public static class ProblemDetailsFactory
{
    public static ProblemDetails CreateNotFound(string errorCode, string title, string detail, string? instance = null, string? traceId = null)
    {
        var problem = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9457",
            Title = title,
            Status = StatusCodes.Status404NotFound,
            Detail = detail,
            Instance = instance
        };

        problem.Extensions["errorCode"] = errorCode;
        if (!string.IsNullOrEmpty(traceId))
        {
            problem.Extensions["traceId"] = traceId;
        }

        return problem;
    }

    public static ProblemDetails CreateBadRequest(string errorCode, string title, string detail, string? instance = null, string? traceId = null)
    {
        var problem = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9457",
            Title = title,
            Status = StatusCodes.Status400BadRequest,
            Detail = detail,
            Instance = instance
        };

        problem.Extensions["errorCode"] = errorCode;
        if (!string.IsNullOrEmpty(traceId))
        {
            problem.Extensions["traceId"] = traceId;
        }

        return problem;
    }

    public static ProblemDetails CreateConflict(string errorCode, string title, string detail, string? instance = null, string? traceId = null)
    {
        var problem = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9457",
            Title = title,
            Status = StatusCodes.Status409Conflict,
            Detail = detail,
            Instance = instance
        };

        problem.Extensions["errorCode"] = errorCode;
        if (!string.IsNullOrEmpty(traceId))
        {
            problem.Extensions["traceId"] = traceId;
        }

        return problem;
    }

    public static ProblemDetails CreateServiceUnavailable(string errorCode, string title, string detail, string? instance = null, string? traceId = null)
    {
        var problem = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9457",
            Title = title,
            Status = StatusCodes.Status503ServiceUnavailable,
            Detail = detail,
            Instance = instance
        };

        problem.Extensions["errorCode"] = errorCode;
        if (!string.IsNullOrEmpty(traceId))
        {
            problem.Extensions["traceId"] = traceId;
        }

        return problem;
    }
}

/// <summary>
/// Extension methods for ControllerBase to simplify RFC 9457 responses
/// </summary>
public static class ControllerExtensions
{
    public static ObjectResult ProblemNotFound(this ControllerBase controller, string errorCode, string detail, string? traceId = null)
    {
        var problem = ProblemDetailsFactory.CreateNotFound(
            errorCode,
            "Resource Not Found",
            detail,
            controller.HttpContext.Request.Path,
            traceId ?? System.Diagnostics.Activity.Current?.TraceId.ToString()
        );

        controller.HttpContext.Response.Headers["x-error-code"] = errorCode;
        return controller.NotFound(problem);
    }

    public static ObjectResult ProblemBadRequest(this ControllerBase controller, string errorCode, string detail, string? traceId = null)
    {
        var problem = ProblemDetailsFactory.CreateBadRequest(
            errorCode,
            "Bad Request",
            detail,
            controller.HttpContext.Request.Path,
            traceId ?? System.Diagnostics.Activity.Current?.TraceId.ToString()
        );

        controller.HttpContext.Response.Headers["x-error-code"] = errorCode;
        return controller.BadRequest(problem);
    }

    public static ObjectResult ProblemConflict(this ControllerBase controller, string errorCode, string detail, string? traceId = null)
    {
        var problem = ProblemDetailsFactory.CreateConflict(
            errorCode,
            "Conflict",
            detail,
            controller.HttpContext.Request.Path,
            traceId ?? System.Diagnostics.Activity.Current?.TraceId.ToString()
        );

        controller.HttpContext.Response.Headers["x-error-code"] = errorCode;
        return controller.Conflict(problem);
    }

    public static ObjectResult ProblemServiceUnavailable(this ControllerBase controller, string errorCode, string detail, string? traceId = null)
    {
        var problem = ProblemDetailsFactory.CreateServiceUnavailable(
            errorCode,
            "Service Unavailable",
            detail,
            controller.HttpContext.Request.Path,
            traceId ?? System.Diagnostics.Activity.Current?.TraceId.ToString()
        );

        controller.HttpContext.Response.Headers["x-error-code"] = errorCode;
        return controller.StatusCode(StatusCodes.Status503ServiceUnavailable, problem);
    }
}
