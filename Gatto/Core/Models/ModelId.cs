namespace Gatto.Core.Models;

//the one home for what makes a model id a usable directory name, a second copy would drift. the messages are user-facing, so don't re-word them here
internal static class ModelId
{
    //the longest derived model id, the id becomes a directory name
    private const int MaxIdLength = 64;

    //the id comes from the repo a fetched model came from, else the declared name, the file stem only when the header has none, and it is derived once

    //the slug rule on its own, extracted from Derive so the fetch can name a folder before any metadata exists
    public static string Slug(string raw)
    {
        var slug = new string(raw.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-");
        slug = slug.Trim('-', '.');
        //the slug comes from a name the file supplies verbatim, so cap it or Directory.CreateDirectory throws PathTooLongException
        if (slug.Length > MaxIdLength) slug = slug[..MaxIdLength].Trim('-', '.');
        return slug.Length > 0 ? slug : "model";
    }

    public static string Derive(GgufMetadata md, string path, string? repoId = null) =>
        repoId is { Length: > 0 } repo ? FromRepo(repo)
            : Slug(string.IsNullOrWhiteSpace(md.Name) ? Path.GetFileNameWithoutExtension(path) : md.Name!);

    //the repo's own name less its org and the -GGUF a conversion adds, so the fetch's folder and the model's id are one name
    public static string FromRepo(string repoId)
    {
        var name = repoId.Split('/').Last();
        return Slug(name.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name);
    }

    //a path-hostile id throws rather than being rewritten, the user typed it and will type it again at /model
    public static void Validate(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new Gatto.Core.Home.GattoConfigException("model id cannot be empty or whitespace");
        if (id is "." or ".." || id.Contains("..", StringComparison.Ordinal))
            throw new Gatto.Core.Home.GattoConfigException($"model id '{id}' is not valid — must not contain '..'");
        if (id.IndexOfAny(['/', '\\']) >= 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new Gatto.Core.Home.GattoConfigException(
                $"model id '{id}' is not valid — it becomes a directory name, so no path separators or special characters");
    }
}
