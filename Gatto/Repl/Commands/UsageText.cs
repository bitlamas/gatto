using System.Globalization;
using Gatto.Core.Client;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Repl.Commands;

//the rows /usage draws from the meter's report, every word core's and every number the provider's
internal static class UsageText
{
    internal static IReadOnlyList<string> Lines(AllowanceReport r, string endpoint, DateTimeOffset now)
    {
        var lines = new List<string>();
        foreach (var a in r.Rows)
        {
            var row = InputFrame.AllowanceGrammar(a, a.Label);
            if (a.ResetAt is { } reset)
                row += " resets " + reset.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            lines.Add(row);
        }
        if (r.ReadAt is { } at) lines.Add("read " + Age(now - at));
        if (r.LastFailure is { } failure) lines.Add("last read failed: " + TermText.Sanitize(failure));
        if (r.Cost is { } cost) lines.Add(CostLine(cost));
        if (lines.Count == 0) lines.Add($"nothing read yet from endpoint {endpoint}");
        return lines;
    }

    internal static string CostLine(decimal cost) =>
        "cost $" + cost.ToString("0.00", CultureInfo.InvariantCulture) + " accounted by the provider";

    //how long ago a read was, in the largest whole unit
    internal static string Age(TimeSpan ago) =>
        ago < TimeSpan.FromMinutes(1) ? "just now"
        : ago < TimeSpan.FromHours(1) ? $"{(int)ago.TotalMinutes} min ago"
        : ago < TimeSpan.FromDays(1) ? $"{(int)ago.TotalHours} h ago"
        : $"{(int)ago.TotalDays} d ago";
}
