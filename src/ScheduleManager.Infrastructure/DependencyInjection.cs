using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Scheduling;
using ScheduleManager.Application.Services;
using ScheduleManager.Infrastructure.Bootstrap;
using ScheduleManager.Infrastructure.Messaging;
using ScheduleManager.Infrastructure.Persistence;
using ScheduleManager.Infrastructure.Security;

namespace ScheduleManager.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection deve ser fornecida por secret/env.");

        // Business transactions are explicitly serializable and must never be replayed implicitly.
        // Worker-level retries handle transient messaging/database failures without duplicating mutations.
        services.AddDbContext<ScheduleManagerDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ScheduleManagerDbContext>());
        services.TryAddScoped<ICurrentRequest, NullCurrentRequest>();
        services.TryAddScoped<IRealtimeNotifier, NullRealtimeNotifier>();

        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName));
        services.AddOptions<EncryptionOptions>().Bind(configuration.GetSection(EncryptionOptions.SectionName));
        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection(RabbitMqOptions.SectionName));
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddOptions<BootstrapOptions>().Bind(configuration.GetSection(BootstrapOptions.SectionName));
        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            .Validate(x => x.NotificationsDays > 0 && x.ApplicationErrorsDays > 0 && x.RevokedSessionsDays > 0,
                "Todos os períodos de retenção devem ser maiores que zero.")
            .ValidateOnStart();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordService, AspNetPasswordService>();
        services.AddSingleton<ITokenHasher, Sha256TokenHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IEncryptionKeyProvider, ConfigurationEncryptionKeyProvider>();
        services.AddSingleton<INotificationCipher, AesGcmNotificationCipher>();
        services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
        services.AddSingleton<ScheduleEngine>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<ITimeOffService, TimeOffService>();
        services.AddScoped<IShiftSwapService, ShiftSwapService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<DatabaseInitializer>();

        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation();
                tracing.AddHttpClientInstrumentation();
                tracing.AddSource(
                    "ScheduleManager",
                    "ScheduleManager.Worker.Outbox",
                    "ScheduleManager.Worker.Notifications");
                if (Uri.TryCreate(configuration["OpenTelemetry:OtlpEndpoint"], UriKind.Absolute, out var endpoint))
                    tracing.AddOtlpExporter(options => options.Endpoint = endpoint);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddHttpClientInstrumentation();
                metrics.AddRuntimeInstrumentation();
                if (Uri.TryCreate(configuration["OpenTelemetry:OtlpEndpoint"], UriKind.Absolute, out var endpoint))
                    metrics.AddOtlpExporter(options => options.Endpoint = endpoint);
            });
        _ = telemetry;
        return services;
    }
}
