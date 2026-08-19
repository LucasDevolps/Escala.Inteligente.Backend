using Microsoft.EntityFrameworkCore;
using ScheduleManager.Application.Abstractions;
using ScheduleManager.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace ScheduleManager.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql-server";
}

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext(new TestCurrentRequest());
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public ScheduleManagerDbContext CreateContext(ICurrentRequest current)
    {
        var options = new DbContextOptionsBuilder<ScheduleManagerDbContext>()
            .UseSqlServer(ConnectionString)
            .EnableSensitiveDataLogging(false)
            .Options;
        return new ScheduleManagerDbContext(options, current);
    }
}

public sealed class TestCurrentRequest(
    Guid? organizationId = null,
    Guid? userId = null,
    string? role = null,
    Guid? sessionId = null) : ICurrentRequest
{
    public Guid? UserId { get; } = userId;
    public Guid? OrganizationId { get; } = organizationId;
    public Guid? SessionId { get; } = sessionId;
    public string? Role { get; } = role;
    public string CorrelationId => "integration-test";
    public string? IpAddress => "127.0.0.1";
    public string? UserAgent => "integration-test";
}
