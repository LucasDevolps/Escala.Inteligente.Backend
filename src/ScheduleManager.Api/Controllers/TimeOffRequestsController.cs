using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScheduleManager.Api.Infrastructure;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Services;

namespace ScheduleManager.Api.Controllers;

[ApiController]
[Route("api/v1/time-off-requests")]
[Authorize]
public sealed class TimeOffRequestsController(ITimeOffService timeOff) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = Policies.Employee)]
    [ProducesResponseType<TimeOffResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TimeOffResponse>> Create(CreateTimeOffRequest request, CancellationToken cancellationToken)
    {
        var result = await timeOff.CreateAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<TimeOffResponse>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default) =>
        Ok(await timeOff.ListAsync(page, pageSize, status, cancellationToken));

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Policies.ApproveTimeOff)]
    public async Task<ActionResult<TimeOffResponse>> Approve(
        Guid id,
        ApproveTimeOffRequest request,
        CancellationToken cancellationToken) =>
        Ok(await timeOff.ApproveAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Policies.ApproveTimeOff)]
    public async Task<ActionResult<TimeOffResponse>> Reject(
        Guid id,
        RejectTimeOffRequest request,
        CancellationToken cancellationToken) =>
        Ok(await timeOff.RejectAsync(id, request, cancellationToken));
}
