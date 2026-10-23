using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gatto.Core.Sandbox;

//every P/Invoke of the sandbox, kept in one place beside the token and launch code that call them
internal static class SandboxNative
{
    public const uint TokenAllAccess = 0xF01FF;
    public const uint SeGroupIntegrity = 0x20;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;

    //the TOKEN_INFORMATION_CLASS values this code reads or sets
    public const int TokenGroups = 2;
    public const int TokenPrivileges = 3;
    public const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential)]
    public struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, uint length, out uint returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, uint length);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes,
        int level, int type, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool IsTokenRestricted(SafeAccessTokenHandle token);

    //the one object type and the one descriptor part the grants touch, SE_FILE_OBJECT and LABEL_SECURITY_INFORMATION
    public const int SeFileObject = 1;
    public const uint LabelSecurityInformation = 0x10;
    public const uint SddlRevision1 = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetNamedSecurityInfoW(string name, int type, uint info,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    //the tree action TREE_SEC_INFO_SET, and the PROG_INVOKE_SETTING values the callback reads and answers
    public const uint TreeSecInfoSet = 1;
    public const int ProgressInvokeOnError = 3;
    public const int ProgressCancelOperation = 4;

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    public delegate void TreeProgress(string objectName, uint status, ref int invokeSetting, IntPtr args, bool securitySet);

    //the tree form, since the plain set returns success over a descendant it could not label
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern uint TreeSetNamedSecurityInfoW(string name, int type, uint info,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, uint action, TreeProgress progress, int invokeSetting, IntPtr args);

    //the plain form, which the unplant uses: an empty label on a root takes every inherited one beneath it too
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern uint SetNamedSecurityInfoW(string name, int type, uint info,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    public const uint AclRevision = 2;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool InitializeAcl(IntPtr acl, uint length, uint revision);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr descriptor, IntPtr size);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr descriptor, uint revision, uint info,
        out IntPtr sddl, IntPtr length);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr memory);

    public const uint HandleFlagInherit = 0x1;
    public const int StartfUseStdHandles = 0x100;
    public const uint ExtendedStartupInfoPresent = 0x00080000;
    public const uint CreateNoWindow = 0x08000000;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint CreateSuspended = 0x00000004;
    public const uint CreateNewProcessGroup = 0x00000200;
    public static readonly IntPtr ProcThreadAttributeHandleList = 0x20002;

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetHandleInformation(SafeHandle handle, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string? application, char[] commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment,
        string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    //the JOBOBJECTINFOCLASS values this code sets or reads, and JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    public const int JobObjectBasicAccountingInformation = 1;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobExtendedLimit
    {
        public JobBasicLimit Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    //a job handle, closed with CloseHandle like any kernel object, which ends every process still in a kill-on-close job
    public sealed class JobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern JobHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(JobHandle job, int infoClass, ref JobExtendedLimit info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(JobHandle job, int infoClass, out JobBasicAccounting info, int length, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessInJob(IntPtr process, JobHandle job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
