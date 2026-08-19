using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using ScheduleManager.Api.Hubs;
using ScheduleManager.Api.Infrastructure;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Infrastructure;
using ScheduleManager.Infrastructure.Bootstrap;
using ScheduleManager.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.Configure(options => options.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId | ActivityTrackingOptions.Baggage);
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentRequest, HttpCurrentRequest>();
builder.Services.AddScoped<IRealtimeNotifier, SignalRRealtimeNotifier>();
builder.Services.AddInfrastructure(builder.Configuration, "ScheduleManager.Api");
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 16 * 1024);
builder.Services.AddOpenApi();
builder.Services.AddControllers(options =>
{
    options.Filters.Add(new ProducesAttribute("application/json"));
}).ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context => ProblemResponses.Validation(context.HttpContext, context.ModelState);
});

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var signingKey = JwtTokenService.DecodeSigningKey(jwt.SigningKeyBase64);
if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
    throw new InvalidOperationException("Jwt:Issuer e Jwt:Audience são obrigatórios.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.SaveToken = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(signingKey),
        ValidateIssuer = true,
        ValidIssuer = jwt.Issuer,
        ValidateAudience = true,
        ValidAudience = jwt.Audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ClockSkew = TimeSpan.Zero,
        NameClaimType = "sub",
        RoleClaimType = "role"
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/notifications"))
                context.Token = token;
            return Task.CompletedTask;
        },
        OnChallenge = async context =>
        {
            context.HandleResponse();
            if (!context.Response.HasStarted)
            {
                var expired = context.AuthenticateFailure is SecurityTokenExpiredException;
                await ProblemResponses.WriteAsync(
                    context.HttpContext,
                    401,
                    "Unauthorized",
                    expired ? "ACCESS_TOKEN_EXPIRED" : "SESSION_REVOKED",
                    expired ? "O access token expirou." : "A autenticação é inválida.");
            }
        },
        OnForbidden = context => ProblemResponses.WriteAsync(context.HttpContext, 403, "Forbidden", "ACCESS_DENIED",
            "Você não possui permissão para esta operação.")
    };
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.ManageEmployees, policy => policy.RequireClaim("role", "MANAGER"));
    options.AddPolicy(Policies.ManageSchedules, policy => policy.RequireClaim("role", "MANAGER"));
    options.AddPolicy(Policies.ApproveTimeOff, policy => policy.RequireClaim("role", "MANAGER"));
    options.AddPolicy(Policies.ViewOwnSchedule, policy => policy.RequireAuthenticatedUser());
    options.AddPolicy(Policies.RequestShiftSwap, policy => policy.RequireClaim("role", "EMPLOYEE"));
    options.AddPolicy(Policies.Employee, policy => policy.RequireClaim("role", "EMPLOYEE"));
});
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.OnRejected = (context, cancellationToken) =>
        new ValueTask(ProblemResponses.WriteAsync(context.HttpContext, 429, "Too many requests", "RATE_LIMIT_EXCEEDED",
            "Muitas tentativas. Tente novamente mais tarde.", cancellationToken));
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length == 0 && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Cors:AllowedOrigins deve possuir allowlist explícita em produção.");
builder.Services.AddCors(options => options.AddPolicy("frontend", policy =>
{
    if (allowedOrigins.Length > 0)
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
});

var app = builder.Build();
_ = app.Services.GetRequiredService<IEncryptionKeyProvider>().GetCurrentKey();
if (!app.Environment.IsDevelopment() && !app.Services.GetRequiredService<IOptions<ScheduleManager.Infrastructure.Messaging.RabbitMqOptions>>().Value.UseTls)
    throw new InvalidOperationException("RabbitMq:UseTls deve ser true fora de Development.");

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseCors("frontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<SessionValidationMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapControllers();
app.MapHub<NotificationsHub>("/hubs/notifications").RequireAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();

await app.RunAsync();

public partial class Program;
