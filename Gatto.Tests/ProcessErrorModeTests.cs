using System.Runtime.InteropServices;

namespace Gatto.Tests;

//gatto sets its own error mode, a process-wide setting, so this class must not run beside a test that spawns a child
[Collection("e2e")]
public class ProcessErrorModeTests
{
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();

    //start at 0x2 and expect 0x3, so the test tells an OR from an overwrite or a deleted call
    [Fact]
    public void The_missing_dll_bit_is_ADDED_to_whatever_was_inherited()
    {
        var original = GetErrorMode();
        try
        {
            SetErrorMode(0x0002);
            Gatto.Core.ProcessErrorMode.FailFastOnMissingDll();
            Assert.Equal(0x0003u, GetErrorMode());
        }
        finally { SetErrorMode(original); }
    }
}
