using ScheduleManager.Application.Errors;
using ScheduleManager.Application.Services;
using ScheduleManager.Domain.Common;
using ScheduleManager.Domain.Entities;
using ScheduleManager.Domain.Enums;

namespace ScheduleManager.UnitTests;

public sealed class DomainRuleTests
{
    [Fact]
    public void Session_is_refreshable_for_at_most_five_minutes_and_rotation_changes_hash()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var session = new UserSession(Guid.NewGuid(), Guid.NewGuid(), [1], Guid.NewGuid(), now, now.AddDays(30), null, null);
        Assert.True(session.CanRefresh(now.AddMinutes(5)));
        Assert.False(session.CanRefresh(now.AddMinutes(5).AddTicks(1)));
        session.Rotate([2], now.AddMinutes(4));
        Assert.Equal(new byte[] { 2 }, session.RefreshTokenHash);
        Assert.Equal(now.AddMinutes(4), session.LastRefreshAt);
    }

    [Fact]
    public void Used_refresh_token_is_recognized_as_replay_material()
    {
        var now = DateTimeOffset.UtcNow;
        var token = new RefreshTokenRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [1], now, now.AddDays(1));
        Assert.False(token.WasConsumed);
        token.MarkRotated(now.AddMinutes(1));
        Assert.True(token.WasConsumed);
    }

    [Fact]
    public void Time_off_and_swap_cannot_be_processed_twice()
    {
        var now = DateTimeOffset.UtcNow;
        var timeOff = new TimeOffRequest(Guid.NewGuid(), Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
            TimeOffReasonCategory.Personal, null, now);
        timeOff.Approve(Guid.NewGuid(), now);
        Assert.Equal("TIME_OFF_ALREADY_PROCESSED", Assert.Throws<DomainRuleException>(() => timeOff.Approve(Guid.NewGuid(), now)).Code);

        var swap = new ShiftSwapRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.Today.AddDays(1)), now);
        swap.Accept(now);
        Assert.Equal("SHIFT_SWAP_ALREADY_PROCESSED", Assert.Throws<DomainRuleException>(() => swap.Reject(now)).Code);
    }

    [Fact]
    public void Input_validation_rejects_bad_pagination_and_weak_password()
    {
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => Validation.Page(0, 101)).Code);
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<AppException>(() => Validation.Password("short")).Code);
    }

    [Fact]
    public void Schedule_settings_validate_limits()
    {
        var settings = new OrganizationScheduleSettings(Guid.NewGuid());
        Assert.Throws<DomainRuleException>(() => settings.Configure(2, 1, 0, -1, 0, true, 21));
    }

    [Fact]
    public void Outbox_failure_backoff_is_persistent_and_bounded()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var message = new OutboxMessage(null, "test", "{}", now, "test");
        message.RegisterFailure(now, "failure");
        Assert.Equal(now.AddSeconds(5), message.NextAttemptAt);
        message.RegisterFailure(now, "failure");
        Assert.Equal(now.AddSeconds(30), message.NextAttemptAt);
        message.RegisterFailure(now, "failure");
        Assert.Equal(now.AddMinutes(2), message.NextAttemptAt);
    }
}
