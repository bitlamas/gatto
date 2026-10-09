namespace Gatto.Tests.Fakes;

//a temp folder that goes when the test ends, with a file written in it when the test asks
internal sealed class TempHome : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("gatto-home-").FullName;

    public static TempHome Empty() => new();

    public static TempHome With(string name, string text)
    {
        var home = new TempHome();
        File.WriteAllText(System.IO.Path.Combine(home.Path, name), text);
        return home;
    }

    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}
