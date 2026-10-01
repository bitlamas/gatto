using Gatto.Repl;

namespace Gatto.Cli;

//the /model rows of an endpoint that names its models, the dot on the current row and the default word on the saved one or else the first
internal static class ListedModelRows
{
    internal static IReadOnlyList<PickerItem> Build(IReadOnlyList<string> offered, string current, string? saved)
    {
        var defaultId = saved is not null && offered.Contains(saved, StringComparer.Ordinal) ? saved : offered[0];
        return offered.Select(id =>
        {
            var isCurrent = string.Equals(id, current, StringComparison.Ordinal);
            return new PickerItem(id, id, Marked: isCurrent, Current: isCurrent,
                Default: string.Equals(id, defaultId, StringComparison.Ordinal));
        }).ToList();
    }
}
