using Gatto.Core.Acquire;

namespace Gatto.Tests;

//the approved publishers are a dated list someone reviewed, so the test pins both the list and the day
public class UploaderAllowlistTests
{
    [Fact]
    public void THE_ALLOWLIST_IS_THE_SEVEN_OF_THE_REVIEW()
    {
        var a = UploaderAllowlist.Load();
        Assert.Equal(["unsloth", "lmstudio-community", "ggml-org", "bartowski", "google", "Qwen", "mistralai"], a.Orgs);
        Assert.Equal("2026-10-07", a.ReviewedDate);
    }

    //excluded, since its converted models predate reliable tool calls, and a review must not re-add it unargued
    [Fact]
    public void THE_EXCLUDED_ORG_STAYS_OUT() =>
        Assert.DoesNotContain("TheBloke", UploaderAllowlist.Load().Orgs);
}
