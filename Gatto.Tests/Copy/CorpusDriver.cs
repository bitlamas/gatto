using Gatto.Cli;
using Gatto.Terminal;

namespace Gatto.Tests.Copy;

//the four modes a printed surface renders in, and light is here because Theme ships light values for all five inks
public enum CorpusMode
{
    Dark,
    Light,
    Plain,
    OneShot,
}

//every volatile value a surface can reach, fixed. a corpus that captured a real clock, pid or build sha would go red on the next commit
internal static class CorpusFacts
{
    public const string Version = "0.5.1";
    public const string Build = "f22c6ea";
    public const string ModelId = "qwen3.6-35b-a3b";
    public const string LongModelId = "qwen3.8-flash-next-q4-k-xl-dn4";
    public const int Pid = 2844;
    public const int Port = 1235;

    public static readonly DateOnly Today = new(2026, 9, 15);
    public static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    //the stamp every header in the corpus prints, so a golden holds this sha rather than the exporting commit's
    public static VersionStamp Stamp => new(Version, Build, Dev: false);
}

//builds the writer and the theme a case renders through, and drives a real command through GattoApp.RunAsync where it can be driven deterministically
internal sealed class CorpusDriver : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-corpus-").FullName;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    //a command through GattoApp.RunAsync resolves its glyph set from this home. a harness without WT_SESSION would otherwise render ascii against a unicode golden
    public CorpusDriver() => File.WriteAllText(Path.Combine(_home, "gatto.json"),
        """{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local","glyphs":"unicode"}""");

    public string Home => _home;

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        try { Directory.Delete(_home, true); } catch (Exception) { } //best effort, since a leftover temp folder must not fail the run
    }

    //null for Plain and OneShot, the ordinary pipe case, which must render byte-identical text through the same calls
    public static Theme? ThemeFor(CorpusMode mode) => mode switch
    {
        CorpusMode.Dark => new Theme(new TermCaps(true, true), ThemeMode.Dark),
        CorpusMode.Light => new Theme(new TermCaps(true, true), ThemeMode.Light),
        _ => null,
    };

    public static GlyphSet Glyphs => GlyphSet.Unicode;

    public CommandContext Context(CorpusMode mode, TextWriter output) =>
        //a command that would ask a question refuses instead, so Interactive false turns refusals into a surface the corpus pins
        new(output, _home, Interactive: false, ThemeFor(mode), Stamp: CorpusFacts.Stamp);

    //the whole funnel for one argv, with stdout and stderr captured apart so a golden shows which stream a line arrives on
    public Rendered Run(CorpusMode mode, params string[] argv)
    {
        var screen = new StringWriter();
        var raw = new StringWriter();
        var err = new StringWriter();
        Console.SetOut(raw);
        Console.SetError(err);
        try
        {
            var ctx = Context(mode, screen);
            var exit = GattoApp.RunAsync(argv, ctx).GetAwaiter().GetResult();
            return new Rendered(screen.ToString(), raw.ToString(), err.ToString(), exit);
        }
        finally
        {
            Console.SetOut(_origOut);
            Console.SetError(_origErr);
        }
    }

    internal sealed record Rendered(string Screen, string Raw, string Err, int Exit);

    //the streams are labelled, so a line that moves between them moves a golden. the concatenation puts Screen before Raw, so cross-stream order is not pinned
    public static string Frame(Rendered r) =>
        "--- stdout ---\n" + Visible(r.Screen + r.Raw)
        + "\n--- stderr ---\n" + Visible(r.Err)
        + "\n--- exit ---\n" + r.Exit + "\n";

    //escapes are written as the text ESC, so a golden stays readable and a wrong colour shows up as a number
    public static string Visible(string s) =>
        s.Replace("\u001b", "ESC").Replace("\r\n", "\n");
}
