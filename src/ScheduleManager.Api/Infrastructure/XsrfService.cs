using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace ScheduleManager.Api.Infrastructure;

public static class XsrfService
{
    public const string RefreshCookie = "refresh_token";
    public const string XsrfCookie = "XSRF-TOKEN";
    public const string XsrfHeader = "X-XSRF-TOKEN";

    public static void SetSessionCookies(HttpResponse response, string refreshToken)
    {
        var xsrf = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        response.Cookies.Append(RefreshCookie, refreshToken, CookieOptions(httpOnly: true, path: "/api/v1/auth"));
        response.Cookies.Append(XsrfCookie, xsrf, CookieOptions(httpOnly: false, path: "/"));
    }

    public static void RequireValid(HttpRequest request)
    {
        var cookie = request.Cookies[XsrfCookie];
        var header = request.Headers[XsrfHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header) || cookie.Length > 200 || header.Length > 200 ||
            !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(cookie), System.Text.Encoding.UTF8.GetBytes(header)))
            throw ScheduleManager.Application.Errors.AppException.Forbidden();
    }

    public static void ClearSessionCookies(HttpResponse response)
    {
        response.Cookies.Delete(RefreshCookie, CookieOptions(httpOnly: true, path: "/api/v1/auth"));
        response.Cookies.Delete(XsrfCookie, CookieOptions(httpOnly: false, path: "/"));
    }

    private static CookieOptions CookieOptions(bool httpOnly, string path) => new()
    {
        HttpOnly = httpOnly,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = path,
        MaxAge = TimeSpan.FromDays(30),
        IsEssential = true
    };
}
