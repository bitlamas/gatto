using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Term;

//the Win32 clipboard write never throws into the render loop. the OS owns the handle after a success, and a failure before that frees it
public sealed class ClipboardTests
{
    private sealed class FakeNative : IClipboardNative
    {
        public int OpenAttempts;
        public int OpenSucceedsAfter = 0;   //the fake opens once the attempt count is past this
        public bool AllocFails;
        public bool SetSucceeds = true;
        public bool Setted, Freed, Closed;
        public string? LastText;

        public bool Open() { OpenAttempts++; return OpenAttempts > OpenSucceedsAfter; }
        public void Empty() { }
        public nint Alloc(string s) { LastText = s; return AllocFails ? 0 : 1; }
        public bool Set(nint h) { Setted = true; return SetSucceeds; }
        public void Close() { Closed = true; }
        public void Free(nint h) { Freed = true; }
    }

    [Fact]
    public void Successful_set_does_NOT_free_the_handle_and_closes()   //the OS owns the handle once the set succeeds
    {
        var n = new FakeNative();
        Assert.True(new Win32Clipboard(n).TrySet("日本語 🐱 café"));
        Assert.True(n.Setted);
        Assert.False(n.Freed);            //don't free here, the OS owns the HGLOBAL now
        Assert.True(n.Closed);
        Assert.Equal("日本語 🐱 café", n.LastText);
    }

    [Fact]
    public void Set_failure_frees_the_handle_and_fails_soft()   //a failure before the set succeeds frees the handle
    {
        var n = new FakeNative { SetSucceeds = false };
        Assert.False(new Win32Clipboard(n).TrySet("x"));
        Assert.True(n.Freed);
        Assert.True(n.Closed);
    }

    [Fact]
    public void Alloc_failure_fails_soft_without_free_or_set()
    {
        var n = new FakeNative { AllocFails = true };
        Assert.False(new Win32Clipboard(n).TrySet("x"));
        Assert.False(n.Setted);
        Assert.False(n.Freed);   //nothing to free, since Alloc returned 0
        Assert.True(n.Closed);
    }

    [Fact]
    public void Open_failure_retries_then_fails_soft()
    {
        var n = new FakeNative { OpenSucceedsAfter = 999 };   //the fake never opens the clipboard
        Assert.False(new Win32Clipboard(n).TrySet("x"));      //answers false and does not throw
        Assert.True(n.OpenAttempts > 1);                       //more than one attempt, so the bounded retry ran
    }

    [Fact]
    public void Open_that_succeeds_on_a_later_attempt_still_sets()
    {
        var n = new FakeNative { OpenSucceedsAfter = 2 };   //the fake opens on the third attempt
        Assert.True(new Win32Clipboard(n).TrySet("x"));
        Assert.True(n.Setted);
    }
}
