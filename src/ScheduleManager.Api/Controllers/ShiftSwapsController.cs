using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScheduleManager.Api.Infrastructure;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Services;

namespace ScheduleManager.Api.Controllers;

[ApiController]
[Route("api/v1/shift-swaps")]
[Authorize]
public sealed class ShiftSwapsController(IShiftSwapService swaps) : ControllerBase
{
    [HttpGet("candidates")]
    [Authorize(Policy = Policies.RequestShiftSwap)]
    public async Task<ActionResult<IReadOnlyList<ShiftSwapCandidateResponse>>> Candidates(
        [FromQuery] DateOnly date,
        CancellationToken cancellationToken) =>
        Ok(await swaps.CandidatesAsync(date, cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.RequestShiftSwap)]
    [ProducesResponseType<ShiftSwapResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ShiftSwapResponse>> Create(CreateShiftSwapRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await swaps.CreateAsync(request, cancellationToken));

    [HttpGet]
    public async Task<ActionResult<PagedResponse<ShiftSwapResponse>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default) =>
        Ok(await swaps.ListAsync(page, pageSize, status, cancellationToken));

    [HttpPost("{id:guid}/accept")]
    [Authorize(Policy = Policies.RequestShiftSwap)]
    public async Task<ActionResult<ShiftSwapResponse>> Accept(Guid id, CancellationToken cancellationToken) =>
        Ok(await swaps.AcceptAsync(id, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Policies.RequestShiftSwap)]
    public async Task<ActionResult<ShiftSwapResponse>> Reject(Guid id, CancellationToken cancellationToken) =>
        Ok(await swaps.RejectAsync(id, cancellationToken));
}
