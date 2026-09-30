namespace Gatto.Cli;

public sealed record ParsedArgs(
    string Command, string Role, string? Model, string? Prompt, bool Continue, bool Yes, bool Auto,
    string? Subcommand = null, string? Target = null, bool Detach = false,
    //the id resumes that one session, null just resumes the latest for the folder (trailing so existing ParsedArgs calls still compile)
    string? ContinueId = null,
    //only serve status reads this, every other command leaves it false
    bool Json = false,
    string? Endpoint = null,
    Gatto.Roles.ThinkingLevel? Effort = null);

public static class ArgRouter
{
    //a word gatto acts on must never read as a role name, and the child word comes from HardwareProbe.ChildWord so it has one home
    private static readonly string[] Reserved =
        { "serve", "doctor", "status", "audition", "model", "setup", "uninstall", "update",
          Gatto.Core.Hardware.HardwareProbe.ChildWord };
    private static readonly string[] ServeSubcommands = { "start", "stop", "status" };

    public static ParsedArgs Parse(string[] argv)
    {
        var command = "launch";
        var role = "generalist";
        string? model = null, prompt = null, continueId = null, endpoint = null;
        Gatto.Roles.ThinkingLevel? effort = null;
        var cont = false;
        var yes = false;
        var auto = false;

        var i = 0;
        if (argv.Length > 0 && !argv[0].StartsWith('-'))
        {
            //a non-reserved bare token is a role name. roles are user files, so the router stays a pure parser and GattoApp exits 2 with the list when the name is unknown
            if (Reserved.Contains(argv[0])) return ParseReserved(argv, role);
            role = argv[0];
            i = 1;
        }

        for (; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "-m" or "--model":
                    model = i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"missing value for {argv[i]}");
                    break;
                case "-p" or "--prompt":
                    prompt = i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"missing value for {argv[i]}");
                    break;
                case "--continue":
                    cont = true;
                    //the id takes the next token only when it doesn't start with a dash, a flag after --continue leaves the id empty
                    if (i + 1 < argv.Length && !argv[i + 1].StartsWith('-'))
                        continueId = argv[++i];
                    break;
                case "-e" or "--endpoint":
                    endpoint = i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"missing value for {argv[i]}");
                    break;
                case "--effort":
                    var level = i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"missing value for {argv[i]}");
                    //a bad level is a usage error here, before a config load or a launch can give it a second message
                    try { effort = Gatto.Roles.Thinking.Parse(level); }
                    catch (Gatto.Core.Home.GattoConfigException ex) { throw new ArgumentException(ex.Message); }
                    break;
                case "--yes":
                    yes = true;
                    break;
                case "--auto":   //parsed here so ParsedArgs grows once
                    auto = true;
                    break;
                case "-h" or "--help":
                    return new ParsedArgs("help", role, null, null, false, false, false);
                case "--version":
                    return new ParsedArgs("version", role, null, null, false, false, false);
                default:
                    throw new ArgumentException($"unknown option: {argv[i]}");
            }
        }
        if (effort is not null && prompt is null)
            throw new ArgumentException("--effort works only with -p. in the REPL, /effort sets the level");
        return new ParsedArgs(command, role, model, prompt, cont, yes, auto, ContinueId: continueId, Endpoint: endpoint, Effort: effort);
    }

    //a reserved command takes a bare subcommand and one bare target. only serve checks its subcommand, and a bare gatto serve means status
    private static ParsedArgs ParseReserved(string[] argv, string role)
    {
        var command = argv[0];
        string? sub = argv.Length > 1 && !argv[1].StartsWith('-') ? argv[1] : null;
        string? target = argv.Length > 2 && !argv[2].StartsWith('-') ? argv[2] : null;

        //the --detach flag runs serve start in the background (spawn, poll to ready, return) while the default streams the log and blocks until Ctrl+C
        var detach = argv.Any(a => a is "--detach" or "-d");

        //the --json flag belongs to serve status and is scanned as a bare flag, so it works with or without the subcommand typed
        var json = argv.Any(a => a is "--json" or "-j");

        if (command == "serve")
        {
            sub ??= "status";
            if (!ServeSubcommands.Contains(sub))
                throw new ArgumentException(
                    $"unknown serve subcommand '{sub}'. Valid: {string.Join(", ", ServeSubcommands)}");
        }

        //model takes new as its one subcommand, and RunModelAsync refuses a target or any other subcommand

        //audition reads its first bare token as the target rather than a subcommand, so shift it out of sub here
        if (command == "audition") { target = sub; sub = null; }

        return new ParsedArgs(command, role, null, null, false, false, false, sub, target, Detach: detach, Json: json);
    }
}
