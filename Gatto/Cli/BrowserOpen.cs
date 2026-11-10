using System.ComponentModel;
using System.Diagnostics;
using Gatto.Terminal;

namespace Gatto.Cli;

//opens a login page in the user's own browser, a program gatto starts and never bounds, and a host that cannot open one hands the url back as a row
internal static class BrowserOpen
{
    //a test's hand on the opener, carried in its own async flow so no test starts a browser, and null in production
    internal static readonly AsyncLocal<Action<string>?> OpenOverride = new();

    internal static void Open(string url, Action<string> warn)
    {
        //the shell starts whatever it is handed, so a page from a login server is opened only as an absolute web address
        if (!Uri.TryCreate(url, UriKind.Absolute, out var page) || (page.Scheme != Uri.UriSchemeHttps && page.Scheme != Uri.UriSchemeHttp))
        {
            warn($"could not open the browser; open {TermText.Sanitize(url)} yourself");
            return;
        }
        if (OpenOverride.Value is { } open) { open(url); return; }
        try
        {
            using var opened = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            warn($"could not open the browser; open {TermText.Sanitize(url)} yourself");
        }
    }
}
