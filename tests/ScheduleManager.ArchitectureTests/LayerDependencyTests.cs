using Microsoft.AspNetCore.Mvc;
using ScheduleManager.Api.Controllers;
using ScheduleManager.Application.Services;
using ScheduleManager.Domain.Entities;

namespace ScheduleManager.ArchitectureTests;

public sealed class LayerDependencyTests
{
    [Fact]
    public void Domain_does_not_reference_framework_or_outer_layers()
    {
        var references = typeof(Organization).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.DoesNotContain("ScheduleManager.Infrastructure", references);
        Assert.DoesNotContain("ScheduleManager.Api", references);
        Assert.DoesNotContain(references, x => x.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain("RabbitMQ.Client", references);
        Assert.DoesNotContain(references, x => x.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_does_not_reference_api_or_infrastructure()
    {
        var references = typeof(IAuthService).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.DoesNotContain("ScheduleManager.Api", references);
        Assert.DoesNotContain("ScheduleManager.Infrastructure", references);
        Assert.DoesNotContain(references, x => x.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Controllers_depend_on_use_cases_and_never_on_db_context()
    {
        var controllers = typeof(AuthController).Assembly.GetTypes()
            .Where(x => !x.IsAbstract && typeof(ControllerBase).IsAssignableFrom(x))
            .ToArray();
        Assert.NotEmpty(controllers);
        foreach (var controller in controllers)
        {
            var constructorDependencies = controller.GetConstructors().SelectMany(x => x.GetParameters()).Select(x => x.ParameterType).ToArray();
            var fields = controller.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Select(x => x.FieldType).ToArray();
            Assert.DoesNotContain(constructorDependencies, IsPersistenceType);
            Assert.DoesNotContain(fields, IsPersistenceType);
        }
    }

    private static bool IsPersistenceType(Type type) =>
        type.Name.Contains("DbContext", StringComparison.Ordinal) ||
        string.Equals(type.FullName, "ScheduleManager.Application.Abstractions.IApplicationDbContext", StringComparison.Ordinal);
}
