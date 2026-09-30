using System.Text.Json;
using System.Text.Json.Nodes;
using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Home;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Roles;
using Gatto.Terminal;

namespace Gatto.Cli;

//one model's profile as the engine update reads it. a null argument list means the file could not be read, and such a profile is never written
internal sealed record EngineProfile(string Id, string Path, string? OwnServer, IReadOnlyList<string>? ExtraArgs);

//a home's engine facts, gathered once before anything is decided, with the probe and the running server read through the caller's functions
internal sealed record EngineSite(
    bool ConfigPresent, string? Server, ProbeResult? Banner, string? GattoFolder,
    IReadOnlyList<EngineProfile> Profiles, string? RunningModel)
{
    public static EngineSite Probe(string home, Func<string, ProbeResult> probe, Func<string, string?> running)
    {
        if (!File.Exists(Path.Combine(home, "gatto.json"))) return new(false, null, null, null, [], null);

        var configured = GattoConfig.Load(home).LlamaServer;
        if (string.IsNullOrWhiteSpace(configured)) return new(true, null, null, null, ReadProfiles(home), null);

        var exe = LlamaAssetSteering.ResolveServerExe(configured);
        return new(true, exe, probe(exe) with { Ran = exe }, GattoFolderOf(home, exe), ReadProfiles(home), running(exe));
    }

    //the folder name only when gatto named it, an executable directly inside the home's own llama folder. anywhere else there is no provenance to read
    private static string? GattoFolderOf(string home, string exe)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe));
        var parent = dir is null ? null : Path.GetDirectoryName(dir);
        return parent is not null
            && string.Equals(parent, Path.GetFullPath(Path.Combine(home, "llama")), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(dir)
            : null;
    }

    private static IReadOnlyList<EngineProfile> ReadProfiles(string home)
    {
        var models = Path.Combine(home, "models");
        if (!Directory.Exists(models)) return [];

        var found = new List<EngineProfile>();
        foreach (var dir in Directory.EnumerateDirectories(models).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(dir, "profile.json");
            if (File.Exists(path)) found.Add(Read(Path.GetFileName(dir), path));
        }
        return found;
    }

    private static EngineProfile Read(string id, string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return new(id, path, null, null);
            var own = root["llama_server"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
            if (root["extra_args"] is not JsonArray arr) return new(id, path, own, []);
            var args = new List<string>(arr.Count);
            foreach (var n in arr)
            {
                if (n is not JsonValue a || !a.TryGetValue<string>(out var text)) return new(id, path, own, null);
                args.Add(text);
            }
            return new(id, path, own, args);
        }
        catch (Exception) { return new(id, path, null, null); }
    }
}

//the question, as the self-update asks its own. null means the user left
internal delegate int? EngineAsker(SelectSpec spec);

//the three deeds that reach outside the home: the release listing, the download that checks each digest, and running an executable for its banner
internal sealed record EngineActs(
    Func<EngineFetchOffer?> Offer,
    Func<EnginePair, IProgress<FetchTick>?, EngineFetch> Fetch,
    Func<string, ProbeResult> Probe)
{
    public static EngineActs For(string home, Gatto.Terminal.GlyphSet glyphs) => new(
        () => { using var p = new LiveSetupProbes(home, glyphs, TextWriter.Null); return p.EngineOffer(); },
        (pair, progress) => { using var p = new LiveSetupProbes(home, glyphs, TextWriter.Null); return p.FetchEngine(pair, progress); },
        exe => LlamaServerProbe.Run(exe, TimeSpan.FromSeconds(10)));

    public static Func<string, string?> Running(string home) =>
        exe => new ServeManager(home, exe).DescribeRunning()?.Model;
}

//the engine phase of gatto update. every refusal returns before the first write, and a write waits on the landed build answering with the pin
internal static class EngineUpdate
{
    public static int Run(CommandContext ctx, CliSurface cli, Gatto.Terminal.GlyphSet g,
        EngineSite? site = null, EngineActs? acts = null, EngineAsker? ask = null)
    {
        var deeds = acts ?? EngineActs.For(ctx.Home, g);
        var here = site ?? EngineSite.Probe(ctx.Home, deeds.Probe, EngineActs.Running(ctx.Home));
        var pin = LlamaAssetSteering.PinnedRelease;

        //a home without a config is setup's to make, and this phase has nothing to say about it
        if (!here.ConfigPresent) return 0;
        if (here.Server is not { } server || here.Banner is not { } banner)
        {
            cli.Say("gatto.json names no engine. gatto setup chooses one.");
            return 0;
        }

        if (banner.Build is not { } build)
        {
            cli.Say($"couldn't read which build the engine at {server} is, so gatto left it alone. Nothing was changed.");
            return 1;
        }

        var state = LlamaAssetSteering.Classify(build, here.GattoFolder);
        if (state == LlamaAssetSteering.FoundState.Fork)
        {
            cli.Say($"the engine at {Path.GetDirectoryName(server)} is a build you chose, so gatto leaves it alone. Nothing was changed.");
            return 1;
        }
        if (state == LlamaAssetSteering.FoundState.Newer)
        {
            cli.Say($"the engine is b{build}, newer than {pin}, the release gatto is tested with. Nothing was changed.");
            return 0;
        }

        var dialect = LoadModeDialect.Load();
        var target = int.Parse(pin[1..], System.Globalization.CultureInfo.InvariantCulture);
        var own = here.Profiles.Where(p => p.OwnServer is not null).ToList();
        var unread = here.Profiles.Where(p => p.OwnServer is null && p.ExtraArgs is null).ToList();
        var rewrites = here.Profiles
            .Where(p => p.OwnServer is null && p.ExtraArgs is not null)
            .Select(p => (Profile: p, Args: dialect.Rewrite(p.ExtraArgs!, target)))
            .Where(r => !r.Args.SequenceEqual(r.Profile.ExtraArgs!))
            .ToList();
        var fetch = state == LlamaAssetSteering.FoundState.Older;

        if (!fetch && rewrites.Count == 0)
        {
            cli.Say($"the engine is {pin}, the release gatto is tested with.");
            return 0;
        }

        if (here.RunningModel is { } model)
        {
            cli.Say($"{model} is being served. Stop it with gatto serve stop, then run gatto update again. Nothing was changed.");
            return 1;
        }

        var into = LlamaFetch.DirFor(ctx.Home, pin);
        var landed = Path.Combine(into, LlamaSiblings.ServerName);
        var reuse = fetch && File.Exists(landed) && deeds.Probe(landed).Build is { } b
            && LlamaAssetSteering.Classify(b) == LlamaAssetSteering.FoundState.Exact;
        EnginePair? pair = null;
        if (fetch && !reuse)
        {
            pair = deeds.Offer()?.Assets;
            if (pair is null)
            {
                cli.Say($"couldn't read the {pin} release from GitHub just now. Try again later. Nothing was changed.");
                return 1;
            }
        }

        WriteMap(cli, build, server, pin, fetch, reuse, pair, into, [.. rewrites.Select(r => r.Profile.Id)],
            [.. own.Select(p => p.Id)], [.. unread.Select(p => p.Id)]);
        var asker = ask ?? Prompter(ctx);
        var answer = asker(SpecFor(pin, fetch, g));
        cli.Blank();
        if (answer != 0)
        {
            cli.Say("nothing was changed.");
            return 0;
        }

        var exe = server;
        if (fetch)
        {
            if (!reuse)
            {
                var got = deeds.Fetch(pair!, Progress(cli, g));
                cli.Blank();
                if (got.ServerPath is not { } path)
                {
                    Bad(cli, g, (got.Error ?? "the llama.cpp download failed.") + " Nothing was changed.");
                    return 1;
                }
                landed = path;
            }

            //the landed build is read before anything is written, so a release that is not the pin never becomes the engine
            if (deeds.Probe(landed).Build is not { } now || LlamaAssetSteering.Classify(now) != LlamaAssetSteering.FoundState.Exact)
            {
                Bad(cli, g, $"the llama.cpp at {Doctor.Tilde(into)} does not report {pin}, so gatto did not switch to it. Nothing else was changed.");
                return 1;
            }

            try { GattoConfigWriter.SetLlamaServer(ctx.Home, landed); }
            catch (Exception ex)
            {
                Bad(cli, g, $"couldn't point gatto.json at the new llama.cpp: {ex.Message}. Nothing else was changed.");
                return 1;
            }
            exe = landed;
            Good(cli, g, $"llama.cpp is now {pin}", Doctor.Tilde(Path.GetDirectoryName(landed) ?? landed));
        }

        foreach (var (profile, args) in rewrites)
        {
            if (Rewrite(profile, args, pin) is { } failed)
            {
                Bad(cli, g, $"couldn't update {profile.Id}: {failed}. The models above were updated.");
                return 1;
            }
            Good(cli, g, $"{profile.Id} updated", $"backup: profile.json.before-{pin}");
        }

        foreach (var p in own) Info(cli, $"{p.Id} not touched, it uses its own llama.cpp");
        foreach (var p in unread) Info(cli, $"{p.Id} not touched, gatto couldn't read its settings");
        if (fetch && !string.Equals(Path.GetDirectoryName(server), Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(server) is { } old && Directory.Exists(old))
            Info(cli, $"the old llama.cpp b{build} is still at {Doctor.Tilde(old)} ({Mb(SizeOf(old))})");
        return 0;
    }

    //what changes, drawn before the question: the engine here and the one that replaces it, then every model and what happens to it
    private static void WriteMap(CliSurface cli, string build, string server, string pin, bool fetch, bool reuse, EnginePair? pair,
        string into, IReadOnlyList<string> rewrites, IReadOnlyList<string> own, IReadOnlyList<string> unread)
    {
        cli.Blank();
        if (fetch)
        {
            var how = reuse ? "already downloaded"
                : pair is { } p ? $"download {Mb(p.Server.Size + (p.Companion?.Size ?? 0))}" : "";
            cli.Say($"llama.cpp runs your models. gatto is tested with {pin}.");
            cli.Row(("  now      ", Theme.Bright), ($"b{build}".PadRight(9), Theme.Bright), (Doctor.Tilde(Path.GetDirectoryName(server) ?? server), Theme.Dim));
            cli.Row(("  update   ", Theme.Bright), (pin.PadRight(9), Theme.Bright), ($"{Doctor.Tilde(into)} ({how})", Theme.Dim));
        }
        else cli.Say($"llama.cpp runs your models, and it is {pin}, the release gatto is tested with.");

        var width = rewrites.Concat(own).Concat(unread).Select(id => id.Length).DefaultIfEmpty(0).Max() + 3;
        if (width > 3)
        {
            cli.Blank();
            cli.Say("models");
            foreach (var id in rewrites) cli.Row(("  " + id.PadRight(width), Theme.Bright), ($"settings updated for {pin}, backup kept", Theme.Dim));
            foreach (var id in own) cli.Row(("  " + id.PadRight(width), Theme.Bright), ("not touched, it uses its own llama.cpp", Theme.Dim));
            foreach (var id in unread) cli.Row(("  " + id.PadRight(width), Theme.Bright), ("not touched, gatto couldn't read its settings", Theme.Dim));
        }
        //the question draws no blank row above itself, so the map ends on one
        cli.Blank();
    }

    //built as data so the screen's shape is assertable without a terminal. no row is pre-selected, since this replaces a program and rewrites the user's files
    internal static SelectSpec SpecFor(string pin, bool fetch, Gatto.Terminal.GlyphSet? glyphs)
    {
        var g = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;
        return new(
            TitleRows: [],
            Question: new PromptQuestion(fetch ? $"Update llama.cpp to {pin}?" : $"Update the model settings for {pin}?"),
            Options: [new SelectOption("Yes, update"), new SelectOption("Not now")],
            FooterHint: $"{g.ArrowsKey} choose {g.Dot} Enter confirm {g.Dot} Esc leave",
            NoInitialCursor: true);
    }

    private static void Good(CliSurface cli, Gatto.Terminal.GlyphSet g, string text, string detail) =>
        cli.Row((g.Ok, Theme.Ok), (" " + text + " ", Theme.Bright), ("(" + detail + ")", Theme.Dim));

    private static void Bad(CliSurface cli, Gatto.Terminal.GlyphSet g, string text) =>
        cli.Row((g.Bad, Theme.Err), (" " + text, Theme.Bright));

    private static void Info(CliSurface cli, string text) =>
        cli.Row(("i", Theme.Dim), (" " + text, Theme.Bright));

    //the previous file is kept once, and the new one goes through a temporary file so a failed write leaves the profile whole
    private static string? Rewrite(EngineProfile profile, IReadOnlyList<string> args, string pin)
    {
        try
        {
            var text = File.ReadAllText(profile.Path);
            var backup = profile.Path + ".before-" + pin;
            if (!File.Exists(backup)) File.WriteAllText(backup, text);

            var root = JsonNode.Parse(text)!.AsObject();
            root["extra_args"] = new JsonArray([.. args.Select(a => (JsonNode)JsonValue.Create(a)!)]);
            var tmp = profile.Path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, profile.Path, overwrite: true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static EngineAsker Prompter(CommandContext ctx)
    {
        var update = UpdateConsent.Prompter(ctx);
        return spec => update(spec);
    }

    private static IProgress<FetchTick> Progress(CliSurface cli, Gatto.Terminal.GlyphSet g) =>
        new Inline(t => cli.Live($"downloading{g.Ellipsis} {t.FileName} {Mb(t.Done)} of {Mb(t.Total)}"));

    //reports on the calling thread, in order, since a console has no context to marshal to
    private sealed class Inline(Action<FetchTick> report) : IProgress<FetchTick>
    {
        public void Report(FetchTick value) => report(value);
    }

    private static long SizeOf(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception) { return 0; }
    }

    private static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}
