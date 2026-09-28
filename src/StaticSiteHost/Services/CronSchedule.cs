using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace StaticSiteHost.Services;

/// <summary>
/// A five-field cron schedule, in UTC, as <c>[Schedule("…")]</c> takes it: minute, hour, day of
/// month, month and day of week, each <c>*</c>, a number, a range <c>a-b</c>, a list
/// <c>a,b,c</c>, or a step <c>*/n</c> or <c>a-b/n</c>. Day of week runs 0-7 with both 0 and 7
/// for Sunday.
///
/// Deliberately a subset. Names (<c>MON</c>, <c>JAN</c>), seconds, <c>@daily</c> and the
/// <c>L</c>, <c>W</c> and <c>#</c> extensions mean different things to different schedulers, or
/// nothing at all, and a schedule that is quietly read differently from how it was meant runs
/// at the wrong time for months before anyone notices. So they are refused, with a message
/// saying what to write instead, and a schedule that could never run is refused as well.
///
/// The two day fields combine as Vixie cron (and cronie, and so most crontabs) combines them:
/// when both are restricted, a day matching either counts; otherwise both must match, which,
/// with one of them <c>*</c>, means just the other. A field counts as unrestricted when it
/// starts with <c>*</c>, so <c>*/2</c> in one of them is unrestricted here as it is there, and a
/// line copied from a crontab runs on the same days.
/// </summary>
public sealed class CronSchedule
{
    /// <summary>
    /// The Gregorian calendar repeats exactly every 400 years, weekdays included, so a schedule
    /// that does not come round in 400 years never does, and one that does always comes round
    /// again within 400 years of any moment.
    /// </summary>
    private const int CalendarCycleYears = 400;

    private sealed record Field(string Name, string Plural, int Min, int Max, string? NamesHint = null);

    private static readonly Field Minute = new("minute", "minutes", 0, 59);
    private static readonly Field Hour = new("hour", "hours", 0, 23);
    private static readonly Field DayOfMonth = new("day of month", "days of the month", 1, 31);
    private static readonly Field Month = new("month", "months", 1, 12,
        "Names such as JAN are not understood; use numbers, 1 for January to 12 for December.");
    private static readonly Field DayOfWeek = new("day of week", "days of the week", 0, 7,
        "Names such as MON are not understood; use numbers, 0 or 7 for Sunday, 1 for Monday and so on to 6 for Saturday.");

    private readonly string _text;
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _weekdays = new bool[7];
    private bool _anyDayOfMonth;
    private bool _anyDayOfWeek;

    private CronSchedule(string text) => _text = text;

    /// <summary>Reads a schedule, or says in a sentence or two what is wrong with it and what to write instead.</summary>
    public static bool TryParse(
        string? text, [NotNullWhen(true)] out CronSchedule? schedule, [NotNullWhen(false)] out string? error)
    {
        schedule = null;
        error = null;

        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            error = "A schedule is required, such as \"0 * * * *\" for the start of every hour.";
            return false;
        }

        if (trimmed.StartsWith('@'))
        {
            error = $"Shorthands such as {trimmed.Split(' ')[0]} are not understood. Write the five fields, as in " +
                    "\"0 0 * * *\" for midnight UTC every day.";
            return false;
        }

        var fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is 6 or 7)
        {
            error = $"\"{trimmed}\" has {fields.Length} fields, and seconds and years are not understood. Write five: " +
                    "minute, hour, day of month, month and day of week. For anything more often than once a " +
                    "minute, use [Every(\"30s\")] instead.";
            return false;
        }

        if (fields.Length != 5)
        {
            error = $"A schedule has five fields separated by spaces: minute, hour, day of month, month and day of " +
                    $"week, as in \"*/5 * * * *\" for every five minutes. \"{trimmed}\" has {fields.Length}.";
            return false;
        }

        var parsed = new CronSchedule(string.Join(' ', fields))
        {
            _anyDayOfMonth = fields[2].StartsWith('*'),
            _anyDayOfWeek = fields[4].StartsWith('*'),
        };

        error = ParseField(fields[0], Minute, parsed._minutes)
                ?? ParseField(fields[1], Hour, parsed._hours)
                ?? ParseField(fields[2], DayOfMonth, parsed._days)
                ?? ParseField(fields[3], Month, parsed._months)
                ?? ParseField(fields[4], DayOfWeek, parsed._weekdays);
        if (error is not null) return false;

        // Any starting point will do; see CalendarCycleYears.
        if (parsed.NextOccurrence(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)) is null)
        {
            error = $"\"{parsed}\" never runs: none of the months it names has a day it names. Check the day of " +
                    "month against the month, such as the 30th of February.";
            return false;
        }

        schedule = parsed;
        return true;
    }

    /// <summary>
    /// The first minute that matches strictly after <paramref name="after"/>, in UTC. Null only
    /// past the end of the calendar <see cref="DateTime"/> can hold.
    /// </summary>
    public DateTimeOffset? NextOccurrence(DateTimeOffset after)
    {
        var utc = after.UtcDateTime;
        if (utc >= DateTime.MaxValue.AddDays(-1)) return null;

        // The next whole minute: an occurrence exactly at "after" has already happened.
        var start = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
        var last = start.Year + CalendarCycleYears < DateTime.MaxValue.Year
            ? start.AddYears(CalendarCycleYears)
            : DateTime.MaxValue.Date.AddDays(-1);

        for (var day = start.Date; day <= last;)
        {
            if (!_months[day.Month])
            {
                // Nothing this month; go straight to the first of the next.
                day = new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
                continue;
            }

            if (DayMatches(day))
            {
                var firstDay = day == start.Date;

                for (var hour = firstDay ? start.Hour : 0; hour < 24; hour++)
                {
                    if (!_hours[hour]) continue;

                    for (var minute = firstDay && hour == start.Hour ? start.Minute : 0; minute < 60; minute++)
                    {
                        if (_minutes[minute]) return new DateTimeOffset(day.AddHours(hour).AddMinutes(minute), TimeSpan.Zero);
                    }
                }
            }

            day = day.AddDays(1);
        }

        return null;
    }

    /// <summary>Vixie cron's rule for the two day fields; see the class remarks.</summary>
    private bool DayMatches(DateTime day)
    {
        var dayOfMonth = _days[day.Day];
        var dayOfWeek = _weekdays[(int)day.DayOfWeek];

        return _anyDayOfMonth || _anyDayOfWeek ? dayOfMonth && dayOfWeek : dayOfMonth || dayOfWeek;
    }

    /// <summary>The schedule with its fields separated by single spaces.</summary>
    public override string ToString() => _text;

    /// <summary>Marks the values one field selects, or says why it cannot.</summary>
    private static string? ParseField(string text, Field field, bool[] selected)
    {
        foreach (var item in text.Split(','))
        {
            if (item.Length == 0)
            {
                return $"The {field.Name} \"{text}\" has an empty entry in its list. Separate values with single " +
                       "commas, as in 0,15,30.";
            }

            var slash = item.IndexOf('/');
            var range = slash < 0 ? item : item[..slash];
            var step = 1;

            if (slash >= 0 && (!TryNumber(item[(slash + 1)..], out step) || step == 0))
            {
                return $"The step in \"{item}\" ({field.Name}) must be a whole number of 1 or more, as in */5.";
            }

            int low, high;
            if (range == "*")
            {
                (low, high) = (field.Min, field.Max);
            }
            else
            {
                var dash = range.IndexOf('-');
                var lowText = dash < 0 ? range : range[..dash];
                var highText = dash < 0 ? range : range[(dash + 1)..];

                if (!TryNumber(lowText, out low)) return NotANumber(lowText, field);
                if (!TryNumber(highText, out high)) return NotANumber(highText, field);

                if (dash < 0 && slash >= 0)
                {
                    return $"\"{item}\" ({field.Name}) puts a step after a single number. Give the step a range, as " +
                           $"in {low}-{field.Max}/{step}, or use */{step} for every {field.Name}.";
                }

                foreach (var value in (int[])[low, high])
                {
                    if (value < field.Min || value > field.Max)
                    {
                        return $"The {field.Name} {value} is out of range: {field.Plural} run from {field.Min} to " +
                               $"{field.Max}{(field == DayOfWeek ? ", with both 0 and 7 for Sunday" : "")}.";
                    }
                }

                if (low > high)
                {
                    return $"The {field.Name} range {range} runs backwards. Write the lower number first; to wrap " +
                           $"past the end, list two ranges, as in {low}-{field.Max},{field.Min}-{high}.";
                }
            }

            for (var value = low; value <= high; value += step)
            {
                // Sunday is both 0 and 7.
                selected[field == DayOfWeek ? value % 7 : value] = true;
            }
        }

        return null;
    }

    private static string NotANumber(string text, Field field)
    {
        if (text.Length == 0) return $"A {field.Name} range needs a number on both sides of the dash, as in 1-5.";

        if (text.Any(char.IsAsciiLetter) && field.NamesHint is { } hint) return $"\"{text}\" ({field.Name}) is not a number. {hint}";

        return text.Any(c => c is '?' or 'L' or 'W' or '#')
            ? $"\"{text}\" ({field.Name}) uses an extension this server does not understand. Use only numbers, *, -, / and commas."
            : $"\"{text}\" ({field.Name}) is not a number. Use only numbers, *, -, / and commas.";
    }

    /// <summary>Digits only: no sign, no spaces, and short enough that it cannot overflow.</summary>
    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        return text.Length is > 0 and <= 4 && text.All(char.IsAsciiDigit) &&
               int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
