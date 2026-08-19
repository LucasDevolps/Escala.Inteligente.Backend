using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace ScheduleManager.Api.Infrastructure;

public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            await ProblemResponses.WriteAsync(context, 403, "Forbidden", "ACCESS_DENIED", "Você não possui permissão para esta operação.");
            return;
        }
        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
