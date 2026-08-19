using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScheduleManager.Api.Infrastructure;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Services;

namespace ScheduleManager.Api.Controllers;

[ApiController]
[Route("api/v1/employees")]
[Authorize(Policy = Policies.ManageEmployees)]
public sealed class EmployeesController(IEmployeeService employees) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<EmployeeResponse>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await employees.ListAsync(page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EmployeeResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await employees.GetAsync(id, cancellationToken));

    [HttpPost]
    [ProducesResponseType<CreateEmployeeResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreateEmployeeResponse>> Create(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var result = await employees.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = result.Employee.Id }, result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<EmployeeResponse>> Update(Guid id, UpdateEmployeeRequest request, CancellationToken cancellationToken) =>
        Ok(await employees.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        await employees.DeactivateAsync(id, cancellationToken);
        return NoContent();
    }
}
