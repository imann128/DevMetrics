using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace DevMetrics.Api.Middleware;

/// <summary>
/// ASP.NET Core middleware that catches all unhandled exceptions and returns
/// a <a href="https://www.rfc-editor.org/rfc/rfc7807">RFC 7807 ProblemDetails</a>
/// JSON response, keeping error shapes consistent across all endpoints.
/// </summary>
/// <remarks>
/// Register early in the pipeline — before <c>UseRouting</c> — so it wraps
/// every subsequent middleware:
/// <code>app.UseMiddleware&lt;ErrorHandlingMiddleware&gt;();</code>
/// Stack traces are included in development environments only.
/// In production, only the <c>title</c> and <c>status</c> fields are returned.
/// </remarks>
public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false
    };

    /// <summary>Initialises the middleware.</summary>
    public ErrorHandlingMiddleware(
        RequestDelegate                   next,
        ILogger<ErrorHandlingMiddleware>  logger,
        IHostEnvironment                  env)
    {
        _next   = next   ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _env    = env    ?? throw new ArgumentNullException(nameof(env));
    }

    /// <summary>Invokes the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // Map known exception types to appropriate HTTP status codes.
        var (status, title) = exception switch
        {
            ArgumentNullException or ArgumentException
                => (HttpStatusCode.BadRequest,         "Invalid request argument"),

            DirectoryNotFoundException or FileNotFoundException
                => (HttpStatusCode.BadRequest,         "Path not found"),

            InvalidOperationException
                => (HttpStatusCode.Conflict,           "Operation conflict"),

            UnauthorizedAccessException
                => (HttpStatusCode.Forbidden,          "Access denied"),

            OperationCanceledException
                => (HttpStatusCode.ServiceUnavailable, "Request cancelled"),

            _   => (HttpStatusCode.InternalServerError, "An unexpected error occurred")
        };

        var statusCode = (int)status;

        _logger.LogError(exception,
            "HTTP {Method} {Path} → {StatusCode} {Title}",
            context.Request.Method,
            context.Request.Path,
            statusCode,
            title);

        var problem = new ProblemDetails
        {
            Status   = statusCode,
            Title    = title,
            Detail   = _env.IsDevelopment() ? exception.Message : null,
            Instance = context.Request.Path
        };

        // Include stack trace only in development to avoid leaking internals.
        if (_env.IsDevelopment())
        {
            problem.Extensions["stackTrace"] = exception.StackTrace;
            problem.Extensions["exceptionType"] = exception.GetType().FullName;
        }

        context.Response.StatusCode  = statusCode;
        context.Response.ContentType = "application/problem+json";

        // Avoid writing to a response that's already started (e.g., streaming response).
        if (!context.Response.HasStarted)
        {
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(problem, JsonOptions));
        }
    }
}
