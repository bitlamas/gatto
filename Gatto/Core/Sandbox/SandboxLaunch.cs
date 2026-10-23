using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static Gatto.Core.Sandbox.SandboxNative;

namespace Gatto.Core.Sandbox;

//a shell child under the sandbox token, with the pipes and the process handle the shell tool reads from the same way it reads Process.Start's
[SupportedOSPlatform("windows")]
internal sealed class SandboxLaunch : IDisposable
{
    public required Process Process { get; init; }
    public required StreamReader Stdout { get; init; }
    public required StreamReader Stderr { get; init; }

    public void Dispose()
    {
        Stdout.Dispose();
        Stderr.Dispose();
        Process.Dispose();
    }

    public const string CommandVariable = "GATTO_SANDBOX_COMMAND";

    //cmd switches the hidden console to UTF-8 before PowerShell reads it. the command waits in a variable, since cmd would expand a % and split on a quote or a pipe in it
    internal static string CommandLine(string exe) =>
        $"cmd.exe /c chcp 65001 >nul & {exe} -NoProfile -NonInteractive -Command "
        + $"\"$__gatto = $env:{CommandVariable}; Remove-Item Env:{CommandVariable}; Invoke-Expression $__gatto\"";

    //the last line keeps -Command's exit rule: run inside a wrapper, a failing last statement would otherwise exit 0
    internal static string Wrapped(string command) => command + "\nif (-not $?) { exit 1 }";

    //a build server the child starts runs at low integrity and outlives it, then takes the user's next build over its shared pipe and cannot write that output
    internal static readonly (string Name, string Value)[] NoBuildServers =
        [("UseSharedCompilation", "false"), ("MSBUILDDISABLENODEREUSE", "1"), ("DOTNET_CLI_USE_MSBUILD_SERVER", "0")];

    //the environment the off path would give the child, with TEMP and TMP moved to the process's own folder, the build servers off and the command beside them
    internal static string EnvironmentBlock(IDictionary<string, string?> environment, string temp, string command)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in environment)
            if (v is not null) vars[k] = v;
        vars["TEMP"] = temp;
        vars["TMP"] = temp;
        foreach (var (name, value) in NoBuildServers) vars[name] = value;
        vars[CommandVariable] = Wrapped(command);
        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        return sb.Append('\0').ToString();
    }

    public static SandboxLaunch Start(SandboxState state, ProcessStartInfo offPath, string command)
    {
        if (state.Token is not { } token || state.Temp is not { } temp)
            throw new InvalidOperationException("the sandbox is not on");

        var inherit = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        //each pipe's child end is inheritable and the parent's is not, so only the three child ends can reach the child
        Pipe(ref inherit, out var stdinRead, out var stdinWrite, parentIsRead: false);
        Pipe(ref inherit, out var stdoutRead, out var stdoutWrite, parentIsRead: true);
        Pipe(ref inherit, out var stderrRead, out var stderrWrite, parentIsRead: true);

        var listSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        var list = Marshal.AllocHGlobal(listSize);
        var handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
        var env = Marshal.StringToHGlobalUni(EnvironmentBlock(offPath.Environment, temp, command));
        var listReady = false;
        JobHandle? job = null;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref listSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
            listReady = true;
            //the handle list keeps every other inheritable handle in gatto, another child's pipe ends among them, out of this child
            Marshal.WriteIntPtr(handles, 0, stdinRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, stdoutWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, stderrWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, handles, 3 * IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StdInput = stdinRead.DangerousGetHandle(),
                    StdOutput = stdoutWrite.DangerousGetHandle(),
                    StdError = stderrWrite.DangerousGetHandle(),
                },
                AttributeList = list,
            };
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var line = (CommandLine(offPath.FileName) + "\0").ToCharArray();
            //in a kill-on-close job before it runs a line, so its whole tree ends with gatto however gatto ends
            job = Job();
            //suspended, so gatto holds the process before it can exit and its exit code stays readable
            if (!CreateProcessAsUserW(token, cmd, line, IntPtr.Zero, IntPtr.Zero, true,
                    ExtendedStartupInfoPresent | CreateNoWindow | CreateUnicodeEnvironment | CreateSuspended | CreateNewProcessGroup,
                    env, offPath.WorkingDirectory, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");

            Process process;
            try
            {
                if (!AssignProcessToJobObject(job, info.Process))
                {
                    var error = Marshal.GetLastWin32Error();
                    TerminateProcess(info.Process, 1);
                    throw new Win32Exception(error, "AssignProcessToJobObject");
                }
                process = Process.GetProcessById(info.ProcessId);
                _ = process.SafeHandle;   //opened now, while the child is suspended, so the exit code survives the exit
                ResumeThread(info.Thread);
            }
            finally
            {
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
            }

            //the child holds its ends now. gatto's copies go, or the drains never see EOF, and stdin closes at once as the off path's does
            stdinRead.Dispose();
            stdoutWrite.Dispose();
            stderrWrite.Dispose();
            stdinWrite.Dispose();
            var utf8 = new UTF8Encoding(false);
            var launch = new SandboxLaunch
            {
                Process = process,
                Stdout = new StreamReader(new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false), utf8),
                Stderr = new StreamReader(new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false), utf8),
            };
            state.Hold(job);
            return launch;
        }
        catch
        {
            job?.Dispose();   //a child already in the job ends with it
            foreach (var h in new SafeHandle[] { stdinRead, stdinWrite, stdoutRead, stdoutWrite, stderrRead, stderrWrite }) h.Dispose();
            throw;
        }
        finally
        {
            if (listReady) DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(env);
        }
    }

    private static JobHandle Job()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
        var limit = new JobExtendedLimit { Basic = new JobBasicLimit { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limit, Marshal.SizeOf<JobExtendedLimit>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject");
        }
        return job;
    }

    private static void Pipe(ref SecurityAttributes inherit, out SafeFileHandle read, out SafeFileHandle write, bool parentIsRead)
    {
        if (!CreatePipe(out read, out write, ref inherit, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe");
        if (!SetHandleInformation(parentIsRead ? read : write, HandleFlagInherit, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation");
    }
}
