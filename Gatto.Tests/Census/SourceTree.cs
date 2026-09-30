namespace Gatto.Tests.Census;

//the one home for reading gatto's own source from a test. shared rather than copied, so censuses cannot drift on which tree they read.
internal static class SourceTree
{
    //search upward for the folder that holds Gatto.sln and fail when none exists, so a census never reads an empty tree
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gatto.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    //read a source file with its line endings normalized to LF, so a census gives the same answer on a CRLF checkout
    public static string Read(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n").Replace("\r", "\n");

    //every folder whose source ships in the exe. a product folder missing here drops out of every census that reads production files, and nothing fails.
    public static readonly IReadOnlyList<string> ProductProjects = ["Gatto", "Gatto.Terminal"];

    //obj and bin are skipped, their generated code would read as live references. test files are out, a type used only by a test is the target
    public static IReadOnlyList<string> ProductionFiles() =>
        [.. ProductProjects
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(RepoRoot(), p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !HasFolderBelow(RepoRoot(), f, "obj", "bin"))
            .OrderBy(f => f, StringComparer.Ordinal)];

    //true when the file sits under the named folder of any product project.
    public static bool IsUnder(string file, params string[] folder) =>
        ProductProjects.Any(p => file.StartsWith(
            Path.Combine([RepoRoot(), p, .. folder]) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    //lists the production files under one product-relative folder, such as Cli, across every product project
    public static IReadOnlyList<string> ProductionFilesUnder(params string[] folder) =>
        [.. ProductionFiles().Where(f => IsUnder(f, folder))];

    //true when the file belongs to the named product project, whatever folder it sits in.
    public static bool IsInProject(string file, string project) =>
        file.StartsWith(Path.Combine(RepoRoot(), project) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    //lists every production file of one product project, for a census that reads the whole project rather than a folder
    public static IReadOnlyList<string> ProductionFilesIn(string project) =>
        [.. ProductionFiles().Where(f => IsInProject(f, project))];

    //reads only the segments below root. a checkout under a tmp or bin folder would otherwise drop every file and sweep an empty tree
    public static bool HasFolderBelow(string root, string path, params string[] folders) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => folders.Contains(part, StringComparer.OrdinalIgnoreCase));

    //remove comments and blank the text inside literals, but keep interpolation holes, so a census counts only what the compiler reads as code.
    public static string CodeOnly(string source) => Scan(source, keepStrings: false);

    //strips comments and keeps string literals whole. a screen-key census needs the literal, blanking it would make the census blind to half its subject
    public static string WithoutComments(string source) => Scan(source, keepStrings: true);

    //returns every string literal as a raw slice. nested literals are separate entries, so a caller that strips a hole doesn't lose one
    public static IReadOnlyList<string> StringLiterals(string source)
    {
        var literals = new List<string>();
        Scan(source, keepStrings: false, literals);
        return literals;
    }

    //returns every comment as a raw slice, the text the public sync publishes as written
    public static IReadOnlyList<string> Comments(string source)
    {
        var comments = new List<string>();
        Scan(source, keepStrings: false, comments: comments);
        return comments;
    }

    private static string Scan(string source, bool keepStrings, List<string>? literals = null, List<string>? comments = null)
    {
        var outp = new System.Text.StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                var start = i;
                while (i < source.Length && source[i] != '\n') i++;
                comments?.Add(source[start..i].TrimEnd('\r'));
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var start = i;
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n') outp.Append('\n');   //keep the line count equal to the source.
                    i++;
                }
                i = Math.Min(i + 2, source.Length);
                comments?.Add(source[start..i]);
                continue;
            }

            var prefix = 0;
            var dollars = 0;
            var verbatim = false;
            while (i + prefix < source.Length && source[i + prefix] is '$' or '@')
            {
                if (source[i + prefix] == '$') dollars++;
                verbatim |= source[i + prefix] == '@';
                prefix++;
            }

            if (i + prefix < source.Length && source[i + prefix] == '"')
            {
                var start = i;
                i += prefix;
                var fence = 0;
                while (i + fence < source.Length && source[i + fence] == '"') fence++;

                //when literals are kept, hole text goes to a throwaway sink (the raw slice is appended instead and must stay byte-identical to the source)
                var sink = keepStrings ? new System.Text.StringBuilder() : outp;
                i = fence >= 3
                    ? SkipRawString(source, i, fence, dollars, sink, literals)
                    : SkipString(source, i, verbatim, dollars > 0, sink, literals);
                literals?.Add(source[start..Math.Min(i, source.Length)]);
                if (keepStrings) outp.Append(source, start, i - start);
                continue;
            }

            if (c == '\'')
            {
                var start = i;
                i++;
                while (i < source.Length && source[i] != '\'')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
                i++;
                if (keepStrings) outp.Append(source, start, Math.Min(i, source.Length) - start);
                continue;
            }

            outp.Append(c);
            i++;
        }

        return outp.ToString();
    }

    //scans a single- or double-quoted string body and emits only the contents of interpolation holes.
    private static int SkipString(string s, int i, bool verbatim, bool interpolated,
        System.Text.StringBuilder outp, List<string>? literals = null)
    {
        i++;
        while (i < s.Length)
        {
            var c = s[i];

            if (verbatim && c == '"' && i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
            if (!verbatim && c == '\\') { i += 2; continue; }
            if (c == '"') return i + 1;

            if (interpolated && c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { i += 2; continue; }   //a doubled brace is literal text, so it does not open a hole
                i = EmitHole(s, i, outp, literals);
                continue;
            }

            if (c == '\n') outp.Append('\n');
            i++;
        }
        return i;
    }

    //scans a raw string body delimited by quote fences. holes are code and the rest is prose.
    private static int SkipRawString(string s, int i, int fence, int dollars,
        System.Text.StringBuilder outp, List<string>? literals = null)
    {
        i += fence;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                var run = 0;
                while (i + run < s.Length && s[i + run] == '"') run++;
                if (run >= fence) return i + run;
                i += run;
                continue;
            }

            //a raw string with N dollar signs opens a hole with N braces, and a shorter brace run is text
            if (dollars > 0 && s[i] == '{')
            {
                var braces = 0;
                while (i + braces < s.Length && s[i + braces] == '{') braces++;
                if (braces < dollars) { i += braces; continue; }
                i = EmitHole(s, i + braces - dollars, outp, literals);
                continue;
            }

            if (s[i] == '\n') outp.Append('\n');
            i++;
        }
        return i;
    }

    //copies one interpolation hole into the code stream, counting braces outside quotes. nested literals are recursed into, so their holes stay code
    private static int EmitHole(string s, int i, System.Text.StringBuilder outp, List<string>? literals = null)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];

            //inside a hole, a literal's body is prose and its own holes are code, so the top-level rules apply by recursion.
            if (c == '"')
            {
                var dollars = 0;
                var verbatim = false;
                for (var k = i - 1; k >= 0 && s[k] is '$' or '@'; k--)
                {
                    if (s[k] == '$') dollars++;
                    verbatim |= s[k] == '@';
                }

                var fence = 0;
                while (i + fence < s.Length && s[i + fence] == '"') fence++;
                var litStart = i;
                i = fence >= 3
                    ? SkipRawString(s, i, fence, dollars, outp, literals)
                    : SkipString(s, i, verbatim, dollars > 0, outp, literals);
                literals?.Add(s[litStart..Math.Min(i, s.Length)]);
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'')
                {
                    if (s[i] == '\\') i++;
                    i++;
                }
                i++;
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i + 1;
            }

            outp.Append(c == '\n' ? '\n' : c);
            i++;
        }
        return i;
    }
}
