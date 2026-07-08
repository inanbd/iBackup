using iBackup.Shared;
using iBackup.Shared.Contracts;

namespace iBackup.Client.Core.Engine;

/// <summary>
/// Pure schedule math (fully unit-testable): computes the next run time of a
/// folder's schedule after a given reference time, in local device time.
/// </summary>
public static class BackupScheduler
{
    /// <summary>
    /// Next due time strictly after <paramref name="after"/>, or null for manual folders.
    /// <paramref name="lastRun"/> anchors interval-based schedules.
    /// </summary>
    public static DateTime? GetNextRun(ScheduleSettings schedule, DateTime after, DateTime? lastRun)
    {
        switch (schedule.Type)
        {
            case ScheduleType.Manual:
                return null;

            case ScheduleType.EveryXMinutes:
            {
                var interval = TimeSpan.FromMinutes(Math.Max(1, schedule.IntervalMinutes ?? 60));
                var anchor = lastRun ?? after;
                var next = anchor + interval;
                return next > after ? next : after + interval;
            }

            case ScheduleType.Hourly:
            {
                var anchor = lastRun ?? after;
                var next = anchor.AddHours(1);
                return next > after ? next : after.AddHours(1);
            }

            case ScheduleType.Daily:
            {
                var timeOfDay = schedule.TimeOfDay ?? TimeSpan.FromHours(2);
                var candidate = after.Date + timeOfDay;
                return candidate > after ? candidate : candidate.AddDays(1);
            }

            case ScheduleType.Weekly:
            {
                var timeOfDay = schedule.TimeOfDay ?? TimeSpan.FromHours(2);
                var targetDay = schedule.DayOfWeek ?? DayOfWeek.Sunday;
                var candidate = after.Date + timeOfDay;
                var daysUntil = ((int)targetDay - (int)candidate.DayOfWeek + 7) % 7;
                candidate = candidate.AddDays(daysUntil);
                return candidate > after ? candidate : candidate.AddDays(7);
            }

            case ScheduleType.Monthly:
            {
                var timeOfDay = schedule.TimeOfDay ?? TimeSpan.FromHours(2);
                var day = Math.Clamp(schedule.DayOfMonth ?? 1, 1, 31);

                var candidate = MonthlyCandidate(after.Year, after.Month, day, timeOfDay);
                if (candidate > after)
                {
                    return candidate;
                }
                var nextMonth = after.Date.AddMonths(1);
                return MonthlyCandidate(nextMonth.Year, nextMonth.Month, day, timeOfDay);
            }

            default:
                return null;
        }
    }

    private static DateTime MonthlyCandidate(int year, int month, int day, TimeSpan timeOfDay)
    {
        var clampedDay = Math.Min(day, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, clampedDay) + timeOfDay;
    }
}
