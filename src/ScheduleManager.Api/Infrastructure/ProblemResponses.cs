using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ScheduleManager.Api.Infrastructure;

public static class ProblemResponses
{
    public static IActionResult Validation(HttpContext httpContext, ModelStateDictionary modelState)
    {
        var errors = modelState
            .Where(x => x.Value?.Errors.Count > 0)
            .ToDictionary(
                x => ToCamelCase(x.Key),
                x => x.Value!.Errors.Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage) ? "Valor inválido." : error.ErrorMessage).ToArray());
        var problem = Create(httpContext, 400, "Validation failed", "VALIDATION_ERROR", "Um ou mais campos são inválidos.", errors);
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    }

    public static Task WriteAsync(
        HttpContext context,
        int status,
        string title,
        string code,
        string detail,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(Create(context, status, title, code, detail), cancellationToken);
    }

    public static ProblemDetails Create(
        HttpContext context,
        int status,
        string title,
        string code,
        string detail,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        problem.Extensions["errors"] = errors ?? new Dictionary<string, string[]>();
        return problem;
    }

    private static string ToCamelCase(string value) => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
