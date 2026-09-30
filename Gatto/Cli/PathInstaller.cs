using Microsoft.Win32;

namespace Gatto.Cli;

//the complete new value and whether it goes back as REG_EXPAND_SZ. a value holding %VARS% must keep that kind, or its variables freeze at today's expansion
internal readonly record struct PathWrite(string Value, bool Expandable);

//the user PATH half of self-install, where a failed read writes nothing. only the user's own PATH is touched, so gatto never needs elevation
internal static class PathInstaller
{
    private const string EnvironmentKey = "Environment";
    private const string PathValue = "Path";

    //the pure decision. null means the read failed, and an empty PATH is a legal value to append to
    public static PathWrite? Plan(string? currentRaw, bool wasExpandable, string entry)
    {
        //a failed read writes nothing, treating it as empty would replace the whole PATH with one entry
        if (currentRaw is null) return null;

        if (UserPath.Append(currentRaw, entry) is not { } updated) return null;   //already on the PATH, so nothing to write

        //keep REG_EXPAND_SZ when the value had it or holds %VARS%, a plain string would freeze the variables
        return new PathWrite(updated, wasExpandable || updated.Contains('%'));
    }

    //the same decision for removal, null means nothing is written
    public static PathWrite? PlanRemoval(string? currentRaw, bool wasExpandable, string entry)
    {
        if (currentRaw is null) return null;
        if (UserPath.Remove(currentRaw, entry) is not { } updated) return null;
        return new PathWrite(updated, wasExpandable || updated.Contains('%'));
    }

    //the raw user PATH, or null when it can't be read. an absent PATH reads as empty, which is a value gatto may append to
    public static (string? Raw, bool Expandable) ReadRaw()
    {
        //not Windows counts as a failed read, and every registry call routes through here so one guard covers the file
        if (!OperatingSystem.IsWindows()) return (null, false);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey, writable: false);
            if (key is null) return ("", false);

            //the flag is DoNotExpandEnvironmentNames, or %USERPROFILE% comes back expanded and the written value bakes in today's path
            var raw = key.GetValue(PathValue, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var kind = key.GetValueKind(PathValue);
            return (raw ?? "", kind == RegistryValueKind.ExpandString);
        }
        catch (Exception) { return (null, false); }   //an unreadable PATH returns null, so the caller writes nothing
    }

    //puts entry on the user's PATH, null when it worked or had nothing to do. the registry write is untested, and the fix is the same seam Remove already has
    public static string? Add(string entry) => Apply(Plan, entry);

    //takes entry off the user's PATH
    public static string? Remove(string entry) => Apply(PlanRemoval, entry);

    //answered through PlanRemoval so this and the removal cannot disagree about an entry, and an unreadable PATH answers false
    public static bool HasEntry(string entry)
    {
        var (raw, expandable) = ReadRaw();
        return raw is not null && PlanRemoval(raw, expandable, entry) is not null;
    }

    private static string? Apply(Func<string?, bool, string, PathWrite?> plan, string entry)
    {
        var (raw, expandable) = ReadRaw();
        //non-Windows arrives here as a null read, so the write below is unreachable and the message names the PATH rather than the platform
        if (raw is null) return "couldn't read your PATH, so nothing was changed";

        if (plan(raw, expandable, entry) is not { } write) return null;   //a null plan means there is nothing to write

        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(EnvironmentKey);
            key.SetValue(PathValue, write.Value,
                write.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        }
        catch (Exception ex) { return $"couldn't update your PATH: {ex.Message}"; }

        Broadcast();
        return null;
    }

    //tells the shell the environment changed, so a new terminal sees it without a logout. best-effort, a failed broadcast must not undo the write
    private static void Broadcast()
    {
        try
        {
            _ = SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "Environment", 2, 1000, out _);
        }
        catch (Exception) { } //the write already succeeded, the notification is only a courtesy
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
