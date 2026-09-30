using System.Text;
using System.Text.RegularExpressions;

namespace Gatto.Core.Memory;

//the project memory the model writes through the memory_write tool and reads back into the prompt prefix (pure filesystem, no tools or prompts)
public static class MemoryDir
{
    //cap on one fact file, memory is in the prompt prefix every turn so a bigger file would leak context budget
    public const int MaxFileBytes = 64 * 1024;

    private static readonly Regex TopicPattern = new("^[a-z0-9-]{1,40}$", RegexOptions.Compiled);

    //the launch cwd, flat with no upward search (a project with no .gatto of its own would otherwise root at the user's home)
    public static string FindProjectRoot(string cwd) => Path.GetFullPath(cwd);

    //lowercase the topic and check it against [a-z0-9-]{1,40}, which keeps out path separators, ".." and the .md extension
    public static string ValidateTopic(string topic)
    {
        var lowered = topic.ToLowerInvariant();
        if (!TopicPattern.IsMatch(lowered))
            throw new ArgumentException($"invalid memory topic \"{topic}\" — expected [a-z0-9-]{{1,40}}", nameof(topic));
        return lowered;
    }

    //the on-disk path, always <slug>.md with no special case for "index" (a fact slugged "index" would write a dead name)
    public static string PathFor(string projectRoot, string slug) =>
        Path.Combine(DirFor(projectRoot), $"{ValidateTopic(slug)}.md");

    //the memory directory for a project, public because the prompt names it absolutely (a bare .gatto would be resolved against the user's home)
    public static string DirFor(string projectRoot) =>
        Path.Combine(projectRoot, ".gatto", "memory");

    //write one fact file whole, normalized and size-capped, with no append mode (a whole rewrite is a revision the model can do reliably)
    public static int Write(string projectRoot, string slug, string content)
    {
        var path = PathFor(projectRoot, slug);
        return WriteAtomic(path, NormalizeTrailingNewline(content), content);
    }

    //a missing slug throws so the model can't learn that a wrong slug is harmless
    public static void Delete(string projectRoot, string slug)
    {
        var path = PathFor(projectRoot, slug);
        if (!File.Exists(path)) throw new InvalidOperationException($"no such fact: {slug}");
        File.Delete(path);
    }

    //every fact file, sorted by filename with StringComparer.Ordinal so the prompt prefix comes out the same on another machine
    public static IReadOnlyList<string> FactFiles(string projectRoot)
    {
        var dir = DirFor(projectRoot);
        try
        {
            return Directory.EnumerateFiles(dir, "*.md")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    //size-check the final bytes first, then write a temp file and move it over, so a reader never sees it half-written
    private static int WriteAtomic(string path, string finalText, string newContent)
    {
        var byteCount = Encoding.UTF8.GetByteCount(finalText);
        if (byteCount > MaxFileBytes)
            throw new InvalidOperationException(
                $"memory file {Path.GetFileName(path)} would exceed 64 KB — compress it or split the detail out");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, finalText);
        File.Move(tmp, path, overwrite: true);

        return newContent
            .Split('\n')
            .Count(line => line.Trim('\r', ' ', '\t').Length > 0);
    }

    //one trailing newline exactly, so repeated rewrites don't grow blank lines at the end of a fact file
    private static string NormalizeTrailingNewline(string text) => text.TrimEnd('\n', '\r') + "\n";
}
