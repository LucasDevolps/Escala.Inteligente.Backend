using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScheduleManager.Application.Contracts;
using ScheduleManager.Application.Services;

namespace ScheduleManager.Api.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public sealed class NotificationsController(INotificationService notifications) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<NotificationResponse>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool unreadOnly = false,
        CancellationToken cancellationToken = default) =>
        Ok(await notifications.ListAsync(page, pageSize, unreadOnly, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<NotificationResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await notifications.GetAsync(id, cancellationToken));

    [HttpPost("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        await notifications.MarkReadAsync(id, cancellationToken);
        return NoContent();
    }
}
