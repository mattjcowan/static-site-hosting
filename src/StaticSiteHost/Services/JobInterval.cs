using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace StaticSiteHost.Services;

/// <summary>
/// The interval <c>[Every("…")]</c> takes: a whole number with a unit (<c>30s</c>, <c>5m</c>,
/// <c>2h</c>, <c>1d</c>), or a <see cref="TimeSpan"/> written with colons (<c>01:30:00</c>,
/// <c>1.00:00:00</c>).
///
/// A bare number is refused rather than read as TimeSpan reads it, which is as days: nobody who
/// writes <c>[Every("30")]</c> means a month. The limits keep a job from becoming a busy loop at
/// one end, and from overflowing a timer at the other; anything more often than every ten
/// seconds is a <c>[BackgroundService]</c> with a loop of its own.
/// </summary>
public static class JobInterval
{
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Maximum = TimeSpan.FromDays(365);

    /// <summary>Reads an interval, or says in a sentence or two what is wrong with it and what to write instead.</summary>
    public static bool TryParse(string? text, out TimeSpan interval, [NotNullWhen(false)] out string? error)
    {
        interval = default;
        error = null;

        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            error = "An interval is required, such as \"5m\" for every five minutes.";
            return false;
        }

        if (!TryRead(trimmed, out interval))
        {
            error = trimmed.All(char.IsAsciiDigit)
                ? $"\"{trimmed}\" has no unit. Write {trimmed}s, {trimmed}m, {trimmed}h or {trimmed}d."
                : $"\"{trimmed}\" is not an interval this server understands. Write a whole number and a unit, as in " +
                  "30s, 5m, 2h or 1d, or a TimeSpan with colons, as in 01:30:00.";
            return false;
        }

        if (interval < Minimum)
        {
            error = $"An interval must be at least {Format(Minimum)}, and \"{trimmed}\" is shorter. For something that has " +
                    "to happen more often, use a [BackgroundService] with a loop of its own.";
            return false;
        }

        if (interval > Maximum)
        {
            error = $"An interval can be at most {Format(Maximum)}, and \"{trimmed}\" is longer. For something that " +
                    "rare, use a [Schedule] instead, such as \"0 0 1 1 *\" for once a year.";
            return false;
        }

        return true;
    }

    /// <summary>The largest whole unit that fits exactly: <c>90m</c>, <c>2h</c>, <c>1d</c>; otherwise TimeSpan's own form.</summary>
    public static string Format(TimeSpan interval)
    {
        foreach (var (ticks, unit) in (ReadOnlySpan<(long, char)>)
                 [(TimeSpan.TicksPerDay, 'd'), (TimeSpan.TicksPerHour, 'h'), (TimeSpan.TicksPerMinute, 'm'), (TimeSpan.TicksPerSecond, 's')])
        {
            if (interval.Ticks > 0 && interval.Ticks % ticks == 0)
                return (interval.Ticks / ticks).ToString(CultureInfo.InvariantCulture) + unit;
        }

        return interval.ToString("c", CultureInfo.InvariantCulture);
    }

    private static bool TryRead(string text, out TimeSpan interval)
    {
        interval = default;

        if (text.Contains(':'))
            return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out interval);

        var perUnit = char.ToLowerInvariant(text[^1]) switch
        {
            's' => TimeSpan.TicksPerSecond,
            'm' => TimeSpan.TicksPerMinute,
            'h' => TimeSpan.TicksPerHour,
            'd' => TimeSpan.TicksPerDay,
            _ => 0,
        };

        var number = text[..^1];
        if (perUnit == 0 || number.Length is 0 or > 9 || !number.All(char.IsAsciiDigit)) return false;

        // Nine digits of days would overflow a TimeSpan; anything that large is refused as too long anyway.
        var value = long.Parse(number, NumberStyles.None, CultureInfo.InvariantCulture);
        interval = value > TimeSpan.MaxValue.Ticks / perUnit ? TimeSpan.MaxValue : new TimeSpan(value * perUnit);
        return true;
    }
}
