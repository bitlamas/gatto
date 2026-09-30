using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Gatto.Extensions;

//keep every #load inside one extension folder by returning null for a path that escapes. a null is a compile diagnostic, single-file extensions get no resolver
internal sealed class ConfinedSourceResolver : SourceFileResolver
{
    private readonly string _rootWithSep;

    public ConfinedSourceResolver(string folder)
        : base(ImmutableArray<string>.Empty, Path.GetFullPath(folder))
    {
        var full = Path.GetFullPath(folder);
        _rootWithSep = full.EndsWith(Path.DirectorySeparatorChar)
            ? full
            : full + Path.DirectorySeparatorChar;
    }

    public override string? ResolveReference(string path, string? baseFilePath)
    {
        var resolved = base.ResolveReference(path, baseFilePath);
        if (resolved is null) return null;

        var full = Path.GetFullPath(resolved);
        return full.StartsWith(_rootWithSep, StringComparison.OrdinalIgnoreCase) ? resolved : null;
    }
}
