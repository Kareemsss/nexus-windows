using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Core.Calendar;

public sealed record CalendarEvent(string Title, DateTimeOffset Start, DateTimeOffset End, string? Location, string? JoinUrl, string Platform);

/// <summary>
/// Small, defensive iCalendar (RFC 5545) reader for upcoming-meeting display. Supports UTC / floating / TZID
/// times, all-day events (ignored for meetings), and the recurrence subset used by virtually all meeting
/// series: FREQ=DAILY|WEEKLY with INTERVAL, COUNT, UNTIL, BYDAY, plus EXDATE. Exotic rules are skipped
/// rather than guessed.
/// </summary>
public static partial class IcsCalendar
{
    [GeneratedRegex(@"https://[^\s""<>\\]*(teams\.microsoft\.com/l/meetup-join|teams\.live\.com/meet|zoom\.us/j/|meet\.google\.com/|webex\.com/)[^\s""<>\\]*", RegexOptions.IgnoreCase)]
    private static partial Regex JoinUrlRegex();

    public static IReadOnlyList<CalendarEvent> Occurrences(string ics, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<CalendarEvent>();
        foreach (var ev in ParseEvents(ics))
        {
            try { Expand(ev, from, to, result); }
            catch { /* a single malformed event must not hide the rest of the calendar */ }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private sealed class RawEvent
    {
        public readonly Dictionary<string, (string Value, Dictionary<string, string> Params)> Props = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Value, Dictionary<string, string> Params)> ExDates = new();
    }

    private static IEnumerable<RawEvent> ParseEvents(string ics)
    {
        RawEvent? current = null;
        foreach (var line in Unfold(ics))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { current = new RawEvent(); continue; }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null) yield return current;
                current = null;
                continue;
            }
            if (current is null) continue;

            int colon = IndexOfUnquoted(line, ':');
            if (colon <= 0) continue;
            var head = line[..colon].Split(';');
            var name = head[0];
            var pars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in head.Skip(1))
            {
                int eq = p.IndexOf('=');
                if (eq > 0) pars[p[..eq]] = p[(eq + 1)..].Trim('"');
            }
            var value = line[(colon + 1)..];
            if (name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase)) current.ExDates.Add((value, pars));
            else current.Props[name] = (value, pars);
        }
    }

    private static int IndexOfUnquoted(string s, char c)
    {
        bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') q = !q;
            else if (s[i] == c && !q) return i;
        }
        return -1;
    }

    private static IEnumerable<string> Unfold(string ics)
    {
        var sb = new StringBuilder();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) { sb.Append(raw, 1, raw.Length - 1); continue; }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear().Append(raw);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string Unescape(string v) =>
        v.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    private static void Expand(RawEvent ev, DateTimeOffset from, DateTimeOffset to, List<CalendarEvent> sink)
    {
        if (!ev.Props.TryGetValue("DTSTART", out var dtStart)) return;
        if (dtStart.Params.TryGetValue("VALUE", out var vt) && vt.Equals("DATE", StringComparison.OrdinalIgnoreCase)) return; // all-day
        if (ev.Props.TryGetValue("STATUS", out var status) && status.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) return;

        var start = ParseDate(dtStart.Value, dtStart.Params);
        if (start is null) return;
        var end = ev.Props.TryGetValue("DTEND", out var dtEnd) ? ParseDate(dtEnd.Value, dtEnd.Params) : null;
        var length = end is { } e && e > start ? e - start.Value : TimeSpan.FromMinutes(30);

        string title = ev.Props.TryGetValue("SUMMARY", out var sum) ? Unescape(sum.Value) : "Untitled meeting";
        string? location = ev.Props.TryGetValue("LOCATION", out var loc) ? Unescape(loc.Value) : null;
        string haystack = string.Join(' ', new[] { "URL", "LOCATION", "DESCRIPTION", "X-MICROSOFT-SKYPETEAMSMEETINGURL" }
            .Select(k => ev.Props.TryGetValue(k, out var p) ? p.Value : string.Empty));
        var m = JoinUrlRegex().Match(haystack.Replace("\\n", " "));
        string? join = m.Success ? m.Value : null;
        string platform = PlatformFor(join, location);

        var exdates = new HashSet<DateTimeOffset>();
        foreach (var (value, pars) in ev.ExDates)
            foreach (var part in value.Split(','))
                if (ParseDate(part, pars) is { } x) exdates.Add(x);

        void Emit(DateTimeOffset s)
        {
            if (exdates.Contains(s)) return;
            if (s + length < from || s > to) return;
            sink.Add(new CalendarEvent(title, s, s + length, location, join, platform));
        }

        if (!ev.Props.TryGetValue("RRULE", out var rrule)) { Emit(start.Value); return; }

        var rule = rrule.Value.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        var freq = rule.GetValueOrDefault("FREQ", "");
        int interval = int.TryParse(rule.GetValueOrDefault("INTERVAL", "1"), out var iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(rule.GetValueOrDefault("COUNT", ""), out var cnt) ? cnt : null;
        DateTimeOffset? until = rule.TryGetValue("UNTIL", out var u) ? ParseDate(u, new Dictionary<string, string>()) : null;
        var byDay = rule.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Select(ParseDay).Where(d => d is not null).Select(d => d!.Value).ToHashSet()
            : null;

        // Expand in the event's own wall-clock so DST transitions keep the meeting at the same local time.
        var tz = dtStart.Value.Trim().EndsWith('Z') ? TimeZoneInfo.Utc : ResolveZone(dtStart.Params);
        var localStart = TimeZoneInfo.ConvertTime(start.Value, tz).DateTime;
        int emitted = 0;
        const int SafetyLimit = 5000;

        if (freq.Equals("DAILY", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < SafetyLimit; i++)
            {
                var local = localStart.AddDays(i * interval);
                var occ = ToOffset(local, tz);
                if (until is { } un && occ > un) break;
                if (count is { } c && emitted >= c) break;
                if (byDay is null || byDay.Contains(local.DayOfWeek)) { emitted++; Emit(occ); }
                if (occ > to) break;
            }
        }
        else if (freq.Equals("WEEKLY", StringComparison.OrdinalIgnoreCase))
        {
            var days = byDay ?? new HashSet<DayOfWeek> { localStart.DayOfWeek };
            var weekStart = localStart.Date.AddDays(-(int)localStart.DayOfWeek);
            for (int w = 0; w < SafetyLimit; w++)
            {
                var week = weekStart.AddDays(7 * w * interval);
                bool stop = false;
                for (int d = 0; d < 7; d++)
                {
                    var day = week.AddDays(d);
                    if (!days.Contains(day.DayOfWeek)) continue;
                    var local = day + localStart.TimeOfDay;
                    if (local < localStart) continue;
                    var occ = ToOffset(local, tz);
                    if ((until is { } un && occ > un) || (count is { } c && emitted >= c)) { stop = true; break; }
                    emitted++;
                    Emit(occ);
                    if (occ > to) { stop = true; break; }
                }
                if (stop) break;
            }
        }
        else
        {
            Emit(start.Value); // unsupported frequency: show only the first instance instead of guessing
        }
    }

    private static DayOfWeek? ParseDay(string token)
    {
        var t = token.Trim().ToUpperInvariant();
        if (t.Length > 2) t = t[^2..]; // "1MO" / "-1FR" positional forms: keep the weekday only
        return t switch
        {
            "SU" => DayOfWeek.Sunday, "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, _ => null,
        };
    }

    private static TimeZoneInfo ResolveZone(Dictionary<string, string> pars)
    {
        if (pars.TryGetValue("TZID", out var id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        }
        return TimeZoneInfo.Local;
    }

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return new DateTimeOffset(unspecified, tz.GetUtcOffset(unspecified));
    }

    internal static DateTimeOffset? ParseDate(string value, Dictionary<string, string> pars)
    {
        value = value.Trim();
        bool utc = value.EndsWith('Z');
        var core = utc ? value[..^1] : value;
        string[] formats = { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm", "yyyyMMdd" };
        if (!DateTime.TryParseExact(core, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return null;
        if (utc) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        return ToOffset(dt, ResolveZone(pars));
    }

    private static string PlatformFor(string? url, string? location)
    {
        var s = (url ?? "") + " " + (location ?? "");
        if (s.Contains("teams", StringComparison.OrdinalIgnoreCase)) return "Microsoft Teams";
        if (s.Contains("zoom", StringComparison.OrdinalIgnoreCase)) return "Zoom";
        if (s.Contains("meet.google", StringComparison.OrdinalIgnoreCase)) return "Google Meet";
        if (s.Contains("webex", StringComparison.OrdinalIgnoreCase)) return "Webex";
        return string.IsNullOrWhiteSpace(location) ? "Calendar" : location!;
    }
}
