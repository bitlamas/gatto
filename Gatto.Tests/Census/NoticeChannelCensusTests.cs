using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a notice never goes through a warning sink, the sink is for things that went wrong. the launch path is unreachable from a test, so this census reads the source
public class NoticeChannelCensusTests
{
    //the sink has two spellings, Warn and OnWarning, and both are ends of one channel. the pattern's \s spans newlines, so it sees the wrapped call site
    private static readonly Regex NoticeIntoWarning =
        new(@"\.\s*(Warn|OnWarning)\s*\(\s*ServeNotice\s*\.", RegexOptions.Compiled);

    [Fact]
    public void NO_SERVE_NOTICE_LINE_TRAVELS_THROUGH_A_WARNING_SINK()
    {
        var root = SourceTree.RepoRoot();
        var offenders = new List<string>();

        foreach (var file in SourceTree.ProductionFiles())
        {
            //strip comments and blank literals, so the census counts only what the compiler reads. a doc comment describing this rule must not become a finding.
            var code = SourceTree.CodeOnly(File.ReadAllText(file));
            foreach (Match m in NoticeIntoWarning.Matches(code))
            {
                var line = code.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line}  {m.Value.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} notice line(s) travel through a warning sink. A warning sink prefixes "
            + "`! ` and writes to stderr, and `!` is reserved for things that went wrong. "
            + "Reusing a server that is already running is a normal event; write it through the "
            + "command layer instead.\n\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void THE_CENSUS_CAN_SEE_THE_DEFECT_when_it_is_there()
    {
        //the defect is written out verbatim here, including the four-line argument wrap a single-line pattern would miss
        Assert.Matches(NoticeIntoWarning, """
            warn.Warn(ServeNotice.Reusing(
                launchLoaded.ModelPath,
                new ServeManager(home, config.LlamaServer ?? "").DescribeRunning()?.Started,
                DateTimeOffset.Now,
                servedBytes));
            """);
        Assert.Matches(NoticeIntoWarning, "observer.OnWarning(ServeNotice.ExitHint(up, bytes, glyphs));");

        //the pattern must not match legitimate shapes. otherwise the rule would forbid all warnings and the next reader would gut it.
        Assert.DoesNotMatch(NoticeIntoWarning, "cli.Say(ServeNotice.Reusing(path, started, now, bytes));");
        Assert.DoesNotMatch(NoticeIntoWarning, "warn.Warn($\"hook error ({evt}): {ex.Message}\");");
    }
}
