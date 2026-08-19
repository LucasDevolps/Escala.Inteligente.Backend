using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ScheduleManager.Api.Infrastructure;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Errors;
using ScheduleManager.Application.Services;

namespace ScheduleManager.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(IAuthService authService) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, cancellationToken);
        XsrfService.SetSessionCookies(Response, result.RefreshToken);
        return Ok(result.Response);
    }

    [HttpPost("activate")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Activate(ActivateRequest request, CancellationToken cancellationToken)
    {
        await authService.ActivateAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken cancellationToken)
    {
        XsrfService.RequireValid(Request);
        var refreshToken = Request.Cookies[XsrfService.RefreshCookie]
            ?? throw AppException.Unauthorized("INVALID_REFRESH_TOKEN", "Refresh token inválido.");
        var result = await authService.RefreshAsync(refreshToken, cancellationToken);
        XsrfService.SetSessionCookies(Response, result.RefreshToken);
        return Ok(result.Response);
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        XsrfService.RequireValid(Request);
        await authService.LogoutAsync(cancellationToken);
        XsrfService.ClearSessionCookies(Response);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken) =>
        Ok(await authService.MeAsync(cancellationToken));
}
