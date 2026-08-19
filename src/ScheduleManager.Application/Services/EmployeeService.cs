using ScheduleManager.Application.Abstractions;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Errors;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.Application.Services;

public sealed class EmployeeService(
    IApplicationDbContext db,
    ICurrentRequest current,
    IClock clock,
    IPasswordService passwords,
    ITokenService tokens,
    ITokenHasher tokenHasher) : IEmployeeService
{
    public Task<PagedResponse<EmployeeResponse>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var (_, organizationId) = RequireManager();
        (page, pageSize) = Validation.Page(page, pageSize);
        cancellationToken.ThrowIfCancellationRequested();
        var query = db.Employees.Where(x => x.OrganizationId == organizationId).OrderBy(x => x.EmployeeNumber);
        var total = query.LongCount();
        var employees = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var users = db.Users.Where(x => x.OrganizationId == organizationId).ToDictionary(x => x.Id);
        var items = employees.Select(x => Map(x, users[x.UserId])).ToArray();
        return Task.FromResult(new PagedResponse<EmployeeResponse>(items, page, pageSize, total, TotalPages(total, pageSize)));
    }

    public Task<EmployeeResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var (_, organizationId) = RequireManager();
        cancellationToken.ThrowIfCancellationRequested();
        var employee = db.Employees.SingleOrDefault(x => x.Id == id && x.OrganizationId == organizationId)
            ?? throw AppException.NotFound("EMPLOYEE_NOT_FOUND", "Colaborador não encontrado.");
        var user = db.Users.Single(x => x.Id == employee.UserId && x.OrganizationId == organizationId);
        return Task.FromResult(Map(employee, user));
    }

    public async Task<CreateEmployeeResponse> CreateAsync(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var (managerId, organizationId) = RequireManager();
        var input = Validate(request.Name, request.Phone, request.EmployeeNumber, request.Email, request.ProductivityLevel);
        if (db.Users.Any(x => x.NormalizedEmail == input.NormalizedEmail))
            throw AppException.Rule("EMAIL_ALREADY_EXISTS", "Já existe uma conta com este e-mail.");
        if (db.Employees.Any(x => x.OrganizationId == organizationId && x.EmployeeNumber == input.EmployeeNumber))
            throw AppException.Rule("EMPLOYEE_NUMBER_ALREADY_EXISTS", "Já existe um colaborador com esta matrícula.");

        var now = clock.UtcNow;
        var unusablePassword = tokens.GenerateOpaqueToken(48);
        var user = new UserAccount(
            organizationId,
            input.Name,
            input.NormalizedEmail,
            input.Phone,
            UserRole.Employee,
            passwords.Hash(unusablePassword),
            true,
            now);
        var employee = new Employee(organizationId, user.Id, input.EmployeeNumber, input.Productivity, now);
        var activationCode = tokens.GenerateOpaqueToken();
        var activation = new ActivationToken(organizationId, user.Id, tokenHasher.Hash(activationCode), now);

        try
        {
            await db.ExecuteInTransactionAsync(async ct =>
            {
                db.Add(user);
                db.Add(employee);
                db.Add(activation);
                db.Add(new AuditLog(
                    organizationId,
                    managerId,
                    "EmployeeCreated",
                    "Employee",
                    employee.Id,
                    "{\"fields\":[\"name\",\"phone\",\"employeeNumber\",\"email\",\"productivityLevel\"]}",
                    current.CorrelationId,
                    current.IpAddress,
                    now));
                await db.SaveChangesAsync(ct);
            }, cancellationToken);
        }
        catch (PersistenceConflictException exception)
        {
            throw DuplicateEmployee(exception);
        }
        catch (PersistenceSerializationException)
        {
            throw AppException.Conflict("EMPLOYEE_CONFLICT", "Não foi possível salvar o colaborador porque houve uma alteração simultânea.");
        }

        return new CreateEmployeeResponse(Map(employee, user), activationCode);
    }

    public async Task<EmployeeResponse> UpdateAsync(Guid id, UpdateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var (managerId, organizationId) = RequireManager();
        var input = Validate(request.Name, request.Phone, request.EmployeeNumber, request.Email, request.ProductivityLevel);
        var rowVersion = Validation.RowVersion(request.RowVersion);
        var employee = db.Employees.SingleOrDefault(x => x.Id == id && x.OrganizationId == organizationId)
            ?? throw AppException.NotFound("EMPLOYEE_NOT_FOUND", "Colaborador não encontrado.");
        var user = db.Users.Single(x => x.Id == employee.UserId && x.OrganizationId == organizationId);
        if (db.Users.Any(x => x.NormalizedEmail == input.NormalizedEmail && x.Id != user.Id))
            throw AppException.Rule("EMAIL_ALREADY_EXISTS", "Já existe uma conta com este e-mail.");
        if (db.Employees.Any(x => x.OrganizationId == organizationId && x.EmployeeNumber == input.EmployeeNumber && x.Id != id))
            throw AppException.Rule("EMPLOYEE_NUMBER_ALREADY_EXISTS", "Já existe um colaborador com esta matrícula.");

        var productivityChanged = employee.ProductivityLevel != input.Productivity;
        var now = clock.UtcNow;
        db.SetOriginalRowVersion(employee, rowVersion);
        employee.Update(input.EmployeeNumber, input.Productivity, now);
        user.UpdateProfile(input.Name, request.Email.Trim(), input.NormalizedEmail, input.Phone, now);
        db.Add(new AuditLog(
            organizationId,
            managerId,
            "EmployeeUpdated",
            "Employee",
            employee.Id,
            "{\"fields\":[\"name\",\"phone\",\"employeeNumber\",\"email\",\"productivityLevel\"]}",
            current.CorrelationId,
            current.IpAddress,
            now));
        if (productivityChanged)
            db.Add(new AuditLog(organizationId, managerId, "ProductivityChanged", "Employee", employee.Id,
                "{\"fields\":[\"productivityLevel\"]}", current.CorrelationId, current.IpAddress, now));

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (OptimisticConcurrencyException) { throw AppException.Conflict("CONCURRENCY_CONFLICT", "O colaborador foi alterado por outra operação."); }
        catch (PersistenceConflictException exception) { throw DuplicateEmployee(exception); }
        return Map(employee, user);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var (managerId, organizationId) = RequireManager();
        var employee = db.Employees.SingleOrDefault(x => x.Id == id && x.OrganizationId == organizationId)
            ?? throw AppException.NotFound("EMPLOYEE_NOT_FOUND", "Colaborador não encontrado.");
        var user = db.Users.Single(x => x.Id == employee.UserId && x.OrganizationId == organizationId);
        var now = clock.UtcNow;

        await db.ExecuteInTransactionAsync(async ct =>
        {
            employee.Deactivate(now);
            user.Deactivate(now);
            foreach (var session in db.UserSessions.Where(x => x.UserId == user.Id && x.RevokedAt == null).ToList())
            {
                session.Revoke(now, "ACCOUNT_DEACTIVATED");
                db.Add(new AuditLog(organizationId, managerId, "SessionRevoked", "UserSession", session.Id,
                    "{\"fields\":[\"revokedAt\",\"revocationReason\"]}", current.CorrelationId, current.IpAddress, now));
            }
            foreach (var token in db.RefreshTokens.Where(x => x.UserId == user.Id && x.RevokedAt == null).ToList())
                token.Revoke(now, "ACCOUNT_DEACTIVATED");
            db.Add(new AuditLog(organizationId, managerId, "EmployeeDeactivated", "Employee", employee.Id, "{}",
                current.CorrelationId, current.IpAddress, now));
            await db.SaveChangesAsync(ct);
        }, cancellationToken);
    }

    private (Guid UserId, Guid OrganizationId) RequireManager()
    {
        if (current.UserId is not Guid userId || current.OrganizationId is not Guid organizationId)
            throw AppException.Unauthorized("SESSION_REVOKED", "A sessão não é válida.");
        if (!string.Equals(current.Role, "MANAGER", StringComparison.OrdinalIgnoreCase)) throw AppException.Forbidden();
        return (userId, organizationId);
    }

    private static (string Name, string Phone, string EmployeeNumber, string NormalizedEmail, ProductivityLevel Productivity) Validate(
        string name, string phone, string employeeNumber, string email, int productivity)
    {
        var errors = new Dictionary<string, string[]>();
        var cleanName = name?.Trim() ?? string.Empty;
        var cleanNumber = employeeNumber?.Trim() ?? string.Empty;
        var cleanPhone = Validation.NormalizePhone(phone ?? string.Empty);
        if (cleanName.Length is < 2 or > 150) errors["name"] = ["Nome deve possuir entre 2 e 150 caracteres."];
        if (!Validation.IsEmail(email ?? string.Empty)) errors["email"] = ["E-mail inválido ou maior que 320 caracteres."];
        if (cleanPhone.Length is < 1 or > 20) errors["phone"] = ["Telefone é obrigatório e deve possuir no máximo 20 caracteres após normalização."];
        if (cleanNumber.Length is < 1 or > 50) errors["employeeNumber"] = ["Matrícula deve possuir entre 1 e 50 caracteres."];
        if (!Enum.IsDefined(typeof(ProductivityLevel), productivity)) errors["productivityLevel"] = ["Produtividade deve ser 0, 1 ou 2."];
        if (errors.Count > 0) throw AppException.Validation(errors);
        return (cleanName, cleanPhone, cleanNumber, Validation.NormalizeEmail(email!), (ProductivityLevel)productivity);
    }

    private static EmployeeResponse Map(Employee employee, UserAccount user) => new(
        employee.Id,
        user.Id,
        user.Name,
        user.Phone,
        employee.EmployeeNumber,
        user.Email,
        (int)employee.ProductivityLevel,
        employee.IsActive,
        employee.CreatedAt,
        employee.UpdatedAt,
        Convert.ToBase64String(employee.RowVersion));

    private static AppException DuplicateEmployee(PersistenceConflictException exception) => exception.ConflictKey switch
    {
        "EMAIL" => AppException.Rule("EMAIL_ALREADY_EXISTS", "Já existe uma conta com este e-mail."),
        "EMPLOYEE_NUMBER" => AppException.Rule("EMPLOYEE_NUMBER_ALREADY_EXISTS", "Já existe um colaborador com esta matrícula."),
        _ => AppException.Conflict("EMPLOYEE_CONFLICT", "Não foi possível salvar o colaborador porque os dados foram alterados por outra operação.")
    };

    private static int TotalPages(long total, int pageSize) => total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
}
