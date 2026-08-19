using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ScheduleManager.Application.Abstractions;

namespace ScheduleManager.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ScheduleManagerDbContext>
{
    public ScheduleManagerDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=(localdb)\\mssqllocaldb;Database=ScheduleManagerDesign;Trusted_Connection=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<ScheduleManagerDbContext>().UseSqlServer(connection).Options;
        return new ScheduleManagerDbContext(options, new NullCurrentRequest());
    }
}

public sealed class NullCurrentRequest : ICurrentRequest
{
    public Guid? UserId => null;
    public Guid? OrganizationId => null;
    public Guid? SessionId => null;
    public string? Role => null;
    public string CorrelationId => System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.CreateVersion7().ToString("N");
    public string? IpAddress => null;
    public string? UserAgent => null;
}
