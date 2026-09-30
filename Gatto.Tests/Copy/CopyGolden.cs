namespace Gatto.Tests.Copy;

//the corpus's golden store, resolved to the repository copy rather than the one beside the test binary
internal static class CopyGolden
{
    //one location, since a store beside the binary would let a re-export write to the build output and still pass
    public static string Dir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Gatto.sln"))) d = d.Parent;
            Assert.True(d is not null, "could not find the repo root from " + AppContext.BaseDirectory);
            return Path.Combine(d!.FullName, "Gatto.Tests", "Copy", "Goldens");
        }
    }

    //an export is deliberate: goldens are written only under GATTO_CORPUS_EXPORT=1, so an ordinary suite can't rewrite its own oracle
    public static bool Exporting =>
        Environment.GetEnvironmentVariable("GATTO_CORPUS_EXPORT") == "1";

    public static string Path_(string name) => Path.Combine(Dir, name + ".txt");

    //the comparison, and the only place a corpus case decides pass or fail.
    public static void Check(string name, string actual)
    {
        Directory.CreateDirectory(Dir);
        var path = Path_(name);
        var text = Normalise(actual);

        if (Exporting)
        {
            File.WriteAllText(path, text);
            return;
        }

        Assert.True(File.Exists(path),
            $"no golden for '{name}'. A surface with no golden is a census gap: add it to the corpus, "
            + "then export with GATTO_CORPUS_EXPORT=1 and read what it wrote.");
        //the verdict comes from Mismatches and nowhere else, so a test can ask this judge whether it would fail
        if (!Mismatches(name, text)) return;
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), text);
    }

    //the way a test asks whether the matcher would fail, without failing the run itself
    public static bool Mismatches(string name, string actual)
    {
        var path = Path_(name);
        if (!File.Exists(path)) return true;
        return File.ReadAllText(path).Replace("\r\n", "\n") != Normalise(actual);
    }

    //normalise line endings, since an edit or a checkout can turn a file CRLF and the corpus would then report a copy defect
    private static string Normalise(string s) => s.Replace("\r\n", "\n");
}
