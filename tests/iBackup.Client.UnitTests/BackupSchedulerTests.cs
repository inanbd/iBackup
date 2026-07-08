using iBackup.Client.Core.Engine;
using iBackup.Shared;
using iBackup.Shared.Contracts;
using Xunit;

namespace iBackup.Client.UnitTests;

public class BackupSchedulerTests
{
    private static readonly DateTime Reference = new(2026, 7, 8, 10, 30, 0); // Wednesday

    [Fact]
    public void Manual_schedule_never_runs()
    {
        Assert.Null(BackupScheduler.GetNextRun(new ScheduleSettings(ScheduleType.Manual), Reference, null));
    }

    [Fact]
    public void EveryXMinutes_anchors_on_last_run()
    {
        var schedule = new ScheduleSettings(ScheduleType.EveryXMinutes, IntervalMinutes: 30);
        var lastRun = Reference.AddMinutes(-10);
        Assert.Equal(lastRun.AddMinutes(30), BackupScheduler.GetNextRun(schedule, Reference, lastRun));
    }

    [Fact]
    public void EveryXMinutes_overdue_runs_after_reference()
    {
        var schedule = new ScheduleSettings(ScheduleType.EveryXMinutes, IntervalMinutes: 30);
        var lastRun = Reference.AddHours(-5);
        var next = BackupScheduler.GetNextRun(schedule, Reference, lastRun);
        Assert.NotNull(next);
        Assert.True(next > Reference);
    }

    [Fact]
    public void Daily_runs_today_when_time_is_ahead()
    {
        var schedule = new ScheduleSettings(ScheduleType.Daily, TimeOfDay: new TimeSpan(23, 0, 0));
        Assert.Equal(Reference.Date.AddHours(23), BackupScheduler.GetNextRun(schedule, Reference, null));
    }

    [Fact]
    public void Daily_runs_tomorrow_when_time_has_passed()
    {
        var schedule = new ScheduleSettings(ScheduleType.Daily, TimeOfDay: new TimeSpan(2, 0, 0));
        Assert.Equal(Reference.Date.AddDays(1).AddHours(2), BackupScheduler.GetNextRun(schedule, Reference, null));
    }

    [Fact]
    public void Weekly_targets_requested_weekday()
    {
        var schedule = new ScheduleSettings(ScheduleType.Weekly, TimeOfDay: new TimeSpan(3, 0, 0), DayOfWeek: DayOfWeek.Friday);
        var next = BackupScheduler.GetNextRun(schedule, Reference, null);
        Assert.NotNull(next);
        Assert.Equal(DayOfWeek.Friday, next.Value.DayOfWeek);
        Assert.Equal(new TimeSpan(3, 0, 0), next.Value.TimeOfDay);
        Assert.True(next > Reference);
        Assert.True(next <= Reference.AddDays(7));
    }

    [Fact]
    public void Monthly_clamps_day_to_month_length()
    {
        // Requesting day 31 in a 30-day month must clamp, not throw.
        var schedule = new ScheduleSettings(ScheduleType.Monthly, TimeOfDay: TimeSpan.Zero, DayOfMonth: 31);
        var june30 = new DateTime(2026, 6, 29, 12, 0, 0);
        var next = BackupScheduler.GetNextRun(schedule, june30, null);
        Assert.Equal(new DateTime(2026, 6, 30), next);
    }

    [Fact]
    public void Monthly_rolls_to_next_month_when_day_passed()
    {
        var schedule = new ScheduleSettings(ScheduleType.Monthly, TimeOfDay: TimeSpan.Zero, DayOfMonth: 1);
        var next = BackupScheduler.GetNextRun(schedule, Reference, null);
        Assert.Equal(new DateTime(2026, 8, 1), next);
    }

    [Fact]
    public void Hourly_runs_one_hour_after_last_run()
    {
        var lastRun = Reference.AddMinutes(-20);
        var next = BackupScheduler.GetNextRun(new ScheduleSettings(ScheduleType.Hourly), Reference, lastRun);
        Assert.Equal(lastRun.AddHours(1), next);
    }
}
