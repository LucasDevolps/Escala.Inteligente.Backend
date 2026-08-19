using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ScheduleManager.Api.Infrastructure;

public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string ItemKey = "CorrelationId";
    private const string HeaderName = "X-Correlation-ID";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidCorrelationId();

    public async Task InvokeAsync(HttpContext context)
    {
        var candidate = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = candidate is not null && ValidCorrelationId().IsMatch(candidate)
            ? candidate
            : Guid.CreateVersion7().ToString("N");
        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);
        Activity.Current?.AddBaggage("correlation.id", correlationId);
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            await next(context);
    }
}
