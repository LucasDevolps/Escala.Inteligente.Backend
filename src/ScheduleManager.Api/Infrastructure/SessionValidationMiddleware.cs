using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Services;

namespace ScheduleManager.Api.Infrastructure;

public sealed class SessionValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuthService authService, ICurrentRequest currentRequest)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (currentRequest.UserId is not Guid userId || currentRequest.OrganizationId is not Guid organizationId ||
                currentRequest.SessionId is not Guid sessionId)
            {
                await ProblemResponses.WriteAsync(context, 401, "Unauthorized", "SESSION_REVOKED", "A sessão não é válida.", context.RequestAborted);
                return;
            }
            var validation = await authService.ValidateSessionAsync(userId, organizationId, sessionId, context.RequestAborted);
            if (!validation.IsValid)
            {
                await ProblemResponses.WriteAsync(context, 401, "Unauthorized", validation.ErrorCode ?? "SESSION_REVOKED",
                    "A sessão não é válida.", context.RequestAborted);
                return;
            }
        }
        await next(context);
    }
}
