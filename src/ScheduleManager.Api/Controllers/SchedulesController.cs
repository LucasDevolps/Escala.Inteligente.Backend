using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScheduleManager.Api.Infrastructure;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Services;

namespace ScheduleManager.Api.Controllers;

[ApiController]
[Route("api/v1/schedules")]
[Authorize]
public sealed class SchedulesController(IScheduleService schedules) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = Policies.ManageSchedules)]
    [ProducesResponseType<ScheduleResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ScheduleResponse>> Create(CreateScheduleRequest request, CancellationToken cancellationToken)
    {
        var result = await schedules.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { year = result.Year, month = result.Month }, result);
    }

    [HttpGet("{year:int}/{month:int}")]
    [Authorize(Policy = Policies.ViewOwnSchedule)]
    public async Task<ActionResult<ScheduleResponse>> Get(int year, int month, CancellationToken cancellationToken) =>
        Ok(await schedules.GetAsync(year, month, cancellationToken));

    [HttpPost("{id:guid}/generate")]
    [Authorize(Policy = Policies.ManageSchedules)]
    public async Task<ActionResult<ScheduleResponse>> Generate(Guid id, CancellationToken cancellationToken) =>
        Ok(await schedules.GenerateAsync(id, cancellationToken));

    [HttpPut("{id:guid}/days/{date}")]
    [Authorize(Policy = Policies.ManageSchedules)]
    public async Task<ActionResult<ScheduleResponse>> UpdateDay(
        Guid id,
        DateOnly date,
        UpdateScheduleDayRequest request,
        CancellationToken cancellationToken) =>
        Ok(await schedules.UpdateDayAsync(id, date, request, cancellationToken));

    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = Policies.ManageSchedules)]
    public async Task<ActionResult<ScheduleResponse>> Publish(
        Guid id,
        PublishScheduleRequest request,
        CancellationToken cancellationToken) =>
        Ok(await schedules.PublishAsync(id, request, cancellationToken));
}
