namespace ScheduleManager.Domain.Common;

public abstract class Entity
{
    protected Entity() { }

    protected Entity(Guid id) => Id = id;

    public Guid Id { get; protected set; }
}

public interface ITenantEntity
{
    Guid OrganizationId { get; }
}

public static class DomainIds
{
    public static Guid New() => Guid.CreateVersion7();
}

public sealed class DomainRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
