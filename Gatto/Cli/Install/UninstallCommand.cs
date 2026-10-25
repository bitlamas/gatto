using Gatto.Core;
using Gatto.Core.Home;
using Gatto.Roles.Serve;

namespace Gatto.Cli.Install;

//gatto uninstall: the consent, the removal, and the helper that clears the install folder once gatto has exited
internal static class UninstallCommand
{
    //the context, the read-only site and the irreversible acts are three records. the deeds stay out of the rendering record, or a reader takes it for output only
    internal static int RunUninstall(
        CommandContext? context = null, UninstallSite? site = null, UninstallActs? acts = null)
    {
        var ctx = context ?? CommandContext.Production();
        //interactive only, it refuses without a terminal
        if (!ctx.Interactive)
        {
            Console.Error.WriteLine(
                "gatto uninstall needs a terminal it can ask questions in. Run it directly rather "
                + "than through a pipe or a script.");
            return 2;
        }

        var home = ctx.Home;
        var where = site ?? UninstallSite.Probe();
        var deeds = acts ?? UninstallActs.Production;
        var installDir = where.InstallDir;

        //the full banner, written after the refusal so a piped run's error is the only thing it writes
        var glyphs = CommandBanner.GlyphsFor(home);   //one glyph set per run, two painters must not disagree
        CommandBanner.Write(ctx.Out, ctx.Theme, "uninstall", glyphs: glyphs,
            stamp: ctx.Stamp);

        //the theme is resolved once, the surface and the prompter both take it from the context
        var cli = ctx.Surface();
        var ask = ctx.Prompter();

        //the screen offers to stop a running server, advice to run gatto serve stop afterwards is unfollowable once gatto is gone
        ServeManager? serve = null;
        Gatto.Roles.Serve.RunningInfo? running = null;
        try
        {
            var cfg = GattoConfig.Load(home);
            serve = new ServeManager(home, cfg.LlamaServer ?? "");
            running = serve.DescribeRunning();
        }
        catch (Exception) { } //no readable config means nothing to say about a server

        //the map is built from the same reads the deletion uses, a second set could disagree with what gets removed
        string? llamaServer = null;
        var models = new List<(string, string)>();
        try
        {
            var config = GattoConfig.Load(home);
            llamaServer = config.LlamaServer;
            var modelsDir = Path.Combine(home, "models");
            foreach (var id in Gatto.Roles.Models.Model.ListIds(modelsDir))
            {
                try { models.Add((id, Gatto.Roles.Models.Model.Load(modelsDir, id).Profile.ActivePath)); }
                catch (GattoConfigException) { } //a model whose profile will not load is skipped rather than failing the whole map
            }
        }
        catch (Exception) { } //a home with no readable config still has an exe and a PATH entry to remove

        //both consent screens go through SelectPrompt, nothing here asks for a typed y or n
        var consent = UninstallConsent.Ask(ask, home, running);
        if (consent is null)
        {
            cli.Say(UninstallConsent.NothingRemoved);
            cli.Close();
            return 0;
        }

        var deleteHome = consent.Value.DeleteHome;

        //the install facts come from the site probe. an absent exe or PATH entry prints no line, RemovesAnything is asked rather than assumed
        var install = where.Facts;

        //the caller reads whether a weights file has no model row and passes the fact in, the map never reads the disk
        var map = Uninstall.Map(installDir, home, deleteHome, install, llamaServer, models,
            consent.Value.StopServer ? running : null,
            otherWeightFiles: Uninstall.HasUnnamedWeights(home, models));
        cli.Blank();
        //nothing is removed until the map and the PATH entry are in place, so gatto can always say what it did
        UninstallConsent.WriteMap(cli, map);

        //the sibling notice goes before the confirm, since it changes what a yes achieves. it stays outside the map, whose rows are all the confirm covers
        if (deleteHome && Uninstall.SiblingNotice(where.SiblingGattos) is { } sibling)
        {
            cli.Blank();
            cli.Say(sibling);
        }

        cli.Blank();

        //a yes that removes nothing is a confirmation not worth asking for, and the map above has already printed
        if (!Uninstall.RemovesAnything(map))
        {
            cli.Say("there is nothing to remove. This gatto is not installed on this machine.");
            cli.Close();
            return 0;
        }

        //the confirm covers the map printed above
        if (!UninstallConsent.Confirm(ask))
        {
            cli.Say(UninstallConsent.NothingRemoved);
            cli.Close();
            return 0;
        }

        //the stop comes before anything is removed, so a declined one can name the running server
        if (consent.Value.StopServer && serve is not null)
        {
            //the stop writes through the writer it is handed, so it gets ServeLines built over this screen's layer
            serve.StopAsync(new ServeLines(cli, glyphs), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        else if (running is { } left)
            cli.Say(UninstallConsent.LeftRunning(left));

        //remove the PATH entry first, so a failure here leaves an installed gatto rather than a dangling entry. the outcome is captured for the sentence below
        var pathRemoved = false;
        if (install.OnPath)
        {
            if (deeds.RemovePathEntry(installDir) is { } pathProblem)
                Console.Error.WriteLine("! " + pathProblem);
            else
                pathRemoved = true;
        }

        if (deleteHome)
        {
            //every entry that would not go is reported on its own line. the keep set from the map is passed to the delete, so the weights files it promised stay
            var treeFailures = deeds.RemoveTree(home, Uninstall.KeptUnder(map, home));
            foreach (var failure in treeFailures)
                Console.Error.WriteLine($"! couldn't remove {failure.Path}: {failure.Why}");

            //the notice is added on top of the per-entry failures above
            if (Uninstall.PartialRemovalNotice(home, treeFailures, where.SiblingGattos) is { } note)
                cli.Say(note);
        }

        //the off-PATH sentence is said only when the entry was really removed, and it goes through cli.Say like the rest
        if (pathRemoved) cli.Say("gatto is off your PATH.");
        if (install.ExePresent)
        {
            cli.Say("the program file removes itself as this exits.");
            deeds.ArmSweeper(installDir);
        }
        //the closing row for the banner above, on every path that printed one
        cli.Close();
        return 0;
    }

    //a detached shell waits for this process to exit, a running image cannot delete itself. armed last, and best-effort, a failed sweeper leaves one folder behind
    internal static void ArmSweeper(string installDir)
    {
        try
        {
            var pid = Environment.ProcessId;
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -WindowStyle Hidden -Command "
                    + $"\"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; "
                    + $"Start-Sleep -Milliseconds 300; "
                    + $"Remove-Item -LiteralPath '{installDir}' -Recurse -Force -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception)
        {
            Console.Error.WriteLine($"! couldn't remove {installDir} automatically. Delete it by hand.");
        }
    }
}
