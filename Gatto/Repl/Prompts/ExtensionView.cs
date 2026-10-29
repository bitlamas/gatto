namespace Gatto.Repl.Prompts;

//one source discovery found, as the launch and the off flag left it: what the help panel's extensions tab draws, built in Cli so Repl reads no extension type
public sealed record ExtensionView(
    string Name,
    bool On,   //the off flag's answer now
    bool Loaded,   //the script ran and committed at launch
    bool OffAtLaunch,   //gatto.json held it off, so it was never compiled this session
    string? Error,   //the first line of why it did not load, null when it loaded or was off
    bool ServesEndpoint,   //the session's endpoint is this source's, so it cannot be turned off
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Endpoints);
