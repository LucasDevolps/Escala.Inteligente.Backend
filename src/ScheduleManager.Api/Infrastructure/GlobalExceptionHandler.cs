using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Errors;
using ScheduleManager.Domain.Common;
using ScheduleManager.Domain.Entities;

namespace ScheduleManager.Api.Infrastructure;

public sealed partial class GlobalExceptionHandler(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    [GeneratedRegex(@"\s+in\s+.*?:line\s+\d+", RegexOptions.CultureInvariant)]
    private static partial Regex FileLocation();

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code, detail, errors) = Map(exception);
        if (status == 500)
        {
            logger.LogError("Unhandled application exception {ExceptionType} with correlation {CorrelationId}",
                exception.GetType().FullName, httpContext.Items[CorrelationIdMiddleware.ItemKey]);
            await PersistAsync(httpContext, exception, cancellationToken);
        }

        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(
            ProblemResponses.Create(httpContext, status, title, code, detail, errors),
            cancellationToken);
        return true;
    }

    private static (int Status, string Title, string Code, string Detail, IReadOnlyDictionary<string, string[]> Errors) Map(Exception exception)
    {
        if (exception is AppException app)
        {
            var status = app.Kind switch
            {
                ErrorKind.Validation => 422,
                ErrorKind.Unauthorized => 401,
                ErrorKind.Forbidden => 403,
                ErrorKind.NotFound => 404,
                ErrorKind.Conflict => 409,
                ErrorKind.Unprocessable => 422,
                _ => 500
            };
            return (status, app.Kind.ToString(), app.Code, app.Message, app.Errors);
        }
        if (exception is DomainRuleException domain)
            return (422, "Business rule violation", domain.Code, domain.Message, new Dictionary<string, string[]>());
        if (exception is OptimisticConcurrencyException)
            return (409, "Conflict", "CONCURRENCY_CONFLICT", "O recurso foi alterado por outra operação.", new Dictionary<string, string[]>());
        return (500, "Internal Server Error", "INTERNAL_ERROR", "Ocorreu um erro inesperado.", new Dictionary<string, string[]>());
    }

    private async Task PersistAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var current = scope.ServiceProvider.GetRequiredService<ICurrentRequest>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var stack = FileLocation().Replace(exception.StackTrace ?? string.Empty, string.Empty);
            if (stack.Length > 8000) stack = stack[..8000];
            db.Add(new ApplicationError(
                clock.UtcNow,
                TruncateRequired(exception.GetType().FullName ?? exception.GetType().Name, 500),
                "Unhandled application error",
                stack,
                current.CorrelationId,
                Activity.Current?.TraceId.ToString(),
                null,
                Truncate(httpContext.Request.Path.Value, 500),
                Truncate(httpContext.Request.Method, 20),
                current.UserId,
                current.OrganizationId,
                TruncateRequired(environment.EnvironmentName, 100)));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception persistenceException)
        {
            logger.LogError("ApplicationError persistence failed with {ExceptionType}; recursion suppressed",
                persistenceException.GetType().FullName);
        }
    }

    private static string TruncateRequired(string value, int max) => value.Length <= max ? value : value[..max];
    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
