using System.Text.RegularExpressions;

namespace Gatto.Core.Acquire;

//the generation comes from the name, since one architecture covers Qwen3.5 and Qwen3.6. the label is kept, since 3.10 read as a decimal sorts below 3.6
internal readonly record struct ModelTier(string Label, int Major, int Minor)
{
    //a name with no version in it, which is never current, since guessing from its neighbours would put it on the default shelf
    public static ModelTier Unversioned => new("unversioned", -1, -1);

    public bool IsVersioned => Major >= 0;

    //newest first, compared component by component on the integers rather than on the label
    public static int Compare(ModelTier a, ModelTier b) =>
        a.Major != b.Major ? b.Major.CompareTo(a.Major) : b.Minor.CompareTo(a.Minor);
}

//the qwen-family parser only, since every other family has a real architecture split to key on
internal static partial class QwenTier
{
    //anchored at the last path segment, so a publisher prefix is ignored. no overrides, so -Next falls to 3 on its own
    [GeneratedRegex(@"^Qwen(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex Versioned();

    //the tier a repo id belongs to, and never null: an unreadable name is Unversioned, which is a real answer
    public static ModelTier Of(string? repoId)
    {
        if (repoId is not { Length: > 0 }) return ModelTier.Unversioned;

        var name = repoId[(repoId.LastIndexOf('/') + 1)..];
        var m = Versioned().Match(name);
        if (!m.Success) return ModelTier.Unversioned;

        var major = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = m.Groups[2].Success
            ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;

        //the label is the text as written, so 3 stays 3 and no minor is invented that the publisher did not write
        return new ModelTier(m.Groups[0].Value[4..], major, minor);
    }
}
