using Gatto.Core.Tools;

namespace Gatto.Tests;

//these cover the hoist that puts the UTF-8 prelude after any leading using or param, and only string logic runs here
public class PowerShellUtf8Tests
{
    private const string Marker = "[Console]::OutputEncoding";   //the marker is a stable slice of the prelude text.

    private static void AssertOrder(string result, params string[] fragmentsInOrder)
    {
        var prev = -1;
        foreach (var f in fragmentsInOrder)
        {
            var at = result.IndexOf(f, StringComparison.Ordinal);
            Assert.True(at >= 0, $"missing fragment: {f}\nin: {result}");
            Assert.True(at > prev, $"fragment out of order: {f}\nin: {result}");
            prev = at;
        }
    }

    [Fact]
    public void PlainCommand_PrependsPrelude()
    {
        var r = PowerShellUtf8.WithPrelude("Write-Output hi");
        AssertOrder(r, Marker, "Write-Output hi");
    }

    [Fact]
    public void LeadingUsing_PreludeGoesAfterIt()
    {
        var r = PowerShellUtf8.WithPrelude("using namespace System.IO; [Path]::GetFileName('x')");
        AssertOrder(r, "using namespace System.IO", Marker, "[Path]::GetFileName");
    }

    [Fact]
    public void MultipleLeadingUsings_PreludeGoesAfterAll()
    {
        var r = PowerShellUtf8.WithPrelude("using namespace System.IO\nusing namespace System.Text\n[Path]::GetTempPath()");
        AssertOrder(r, "using namespace System.IO", "using namespace System.Text", Marker, "[Path]::GetTempPath");
    }

    [Fact]
    public void LeadingParamBlock_PreludeGoesAfterIt()
    {
        var r = PowerShellUtf8.WithPrelude("param([int]$n=2) $n*3");
        AssertOrder(r, "param([int]$n=2)", Marker, "$n*3");
    }

    [Fact]
    public void ParamWithQuotedParenDefault_LeftUnhandled_PreludeStaysFront()
    {
        //an accepted edge: a param default with a quoted paren is left alone, so the prelude stays in front of param
        var r = PowerShellUtf8.WithPrelude("param($x='a)b') $x");
        AssertOrder(r, Marker, "param($x='a)b')");
    }

    [Fact]
    public void UsingLikeIdentifier_IsNotTreatedAsKeyword()
    {
        //the word boundary keeps usingfoo a bare command rather than a using statement
        var r = PowerShellUtf8.WithPrelude("usingfoo -bar");
        AssertOrder(r, Marker, "usingfoo -bar");
    }

    [Fact]
    public void LeadingLineComment_BeforeUsing_StillHoists()
    {
        //a # comment may sit before using, so the prelude goes after the using
        var r = PowerShellUtf8.WithPrelude("# note\nusing namespace System.IO\n[Path]::GetTempPath()");
        AssertOrder(r, "# note", "using namespace System.IO", Marker, "[Path]::GetTempPath");
    }

    [Fact]
    public void HyphenatedCommand_IsNotTreatedAsUsing()
    {
        //a hyphen is a rejected boundary, so using-module stays a command and the prelude keeps the front
        var r = PowerShellUtf8.WithPrelude("using-module Foo; bar");
        AssertOrder(r, Marker, "using-module Foo");
    }
}
