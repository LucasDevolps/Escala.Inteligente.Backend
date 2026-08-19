using System.Security.Claims;
using ScheduleManager.Application.Abstractions;

namespace ScheduleManager.Api.Infrastructure;

public sealed class HttpCurrentRequest(IHttpContextAccessor accessor) : ICurrentRequest
{
    private HttpContext? Context => accessor.HttpContext;
    public Guid? UserId => ClaimGuid("sub") ?? ClaimGuid(ClaimTypes.NameIdentifier);
    public Guid? OrganizationId => ClaimGuid("organization_id");
    public Guid? SessionId => ClaimGuid("sid");
    public string? Role => Context?.User.FindFirstValue("role");
    public string CorrelationId => Context?.Items[CorrelationIdMiddleware.ItemKey]?.ToString()
        ?? System.Diagnostics.Activity.Current?.TraceId.ToString()
        ?? Guid.CreateVersion7().ToString("N");
    public string? IpAddress => Context?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => Context?.Request.Headers.UserAgent.ToString();

    private Guid? ClaimGuid(string type) => Guid.TryParse(Context?.User.FindFirstValue(type), out var value) ? value : null;
}
