using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Home;
using Gatto.Tests.Support;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//default_publisher picks the org the curated shelf opens on, and the broadened view ignores that preference
[Collection(SubstDriveCollection.Name)]
public class PublisherPreferenceTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-pub-").FullName;
    public void Dispose() => Directory.Delete(_home, recursive: true);

    private void Write(string clause) =>
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            "{\"endpoints\":{\"local\":{\"base_url\":\"http://x\"}},\"default_endpoint\":\"local\"" + clause + "}");

    [Fact]
    public void A_CONFIGURED_PUBLISHER_LOADS()
    {
        Write(",\"default_publisher\":\"bartowski\"");
        Assert.Equal("bartowski", GattoConfig.Load(_home).DefaultPublisher);
    }

    //an absent key loads as null, which stays distinct from a setting equal to the shipped default
    [Fact]
    public void AN_ABSENT_PUBLISHER_IS_NULL_AND_NEVER_THE_DATED_DEFAULT()
    {
        Write("");
        Assert.Null(GattoConfig.Load(_home).DefaultPublisher);
    }

    [Fact]
    public void AN_UNKNOWN_PUBLISHER_IS_REFUSED_NAMING_THE_COMPILED_ALLOWLIST()
    {
        Write(",\"default_publisher\":\"acme-weights\"");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));

        Assert.Contains("acme-weights", ex.Message);
        //the refusal must name every accepted org, since a partial list sends the user off to search.
        foreach (var org in UploaderAllowlist.Load().Orgs) Assert.Contains(org, ex.Message);
    }

    [Fact]
    public void A_PUBLISHER_OF_THE_WRONG_TYPE_IS_REFUSED()
    {
        Write(",\"default_publisher\":7");
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
        Assert.Contains("default_publisher", ex.Message);
        Assert.Contains("string", ex.Message);
    }

    //an empty string is neither a publisher nor an absence, so it must be refused. omitting the key is the only way to mean no preference.
    [Fact]
    public void AN_EMPTY_PUBLISHER_IS_REFUSED_RATHER_THAN_READ_AS_NO_PREFERENCE()
    {
        Write(",\"default_publisher\":\"\"");
        Assert.Throws<GattoConfigException>(() => GattoConfig.Load(_home));
    }

    [Fact]
    public void THE_SHELF_OPENS_ON_THE_CONFIGURED_PUBLISHER()
    {
        Write(",\"default_publisher\":\"bartowski\"");
        using var probes = new LiveSetupProbes(_home, GlyphSet.Unicode, TextWriter.Null);

        Assert.Equal("bartowski", probes.Allowlist.CuratedPublisherFor(HubSearchView.Curated));
        Assert.Equal(["bartowski"], probes.Allowlist.OrgsFor(HubSearchView.Curated));
    }

    [Fact]
    public void WITHOUT_A_PREFERENCE_THE_SHELF_OPENS_ON_THE_DATED_DEFAULT()
    {
        Write("");
        using var probes = new LiveSetupProbes(_home, GlyphSet.Unicode, TextWriter.Null);

        Assert.Equal(UploaderAllowlist.Load().DefaultView,
            probes.Allowlist.CuratedPublisherFor(HubSearchView.Curated));
    }

    //the broadened view must show the full org list, since a count check would pass a preference that quietly narrowed it.
    [Fact]
    public void THE_BROADENED_VIEW_IS_DEAF_TO_THE_PREFERENCE()
    {
        Write(",\"default_publisher\":\"bartowski\"");
        using var probes = new LiveSetupProbes(_home, GlyphSet.Unicode, TextWriter.Null);

        Assert.Null(probes.Allowlist.CuratedPublisherFor(HubSearchView.Broadened));
        Assert.Equal(UploaderAllowlist.Load().Orgs, probes.Allowlist.OrgsFor(HubSearchView.Broadened));
    }

    //an unreadable config leaves the dated default standing, since the read must not throw while the shelf draws
    [Fact]
    public void AN_UNREADABLE_CONFIG_LEAVES_THE_DATED_DEFAULT_STANDING()
    {
        File.WriteAllText(Path.Combine(_home, "gatto.json"), "{ this is not json");
        using var probes = new LiveSetupProbes(_home, GlyphSet.Unicode, TextWriter.Null);

        Assert.Equal(UploaderAllowlist.Load().DefaultView,
            probes.Allowlist.CuratedPublisherFor(HubSearchView.Curated));
    }

    //a missing config throws differently from an unparseable one, and it is normal during setup, so the shelf must still open
    [Fact]
    public void A_HOME_WITH_NO_CONFIG_YET_STILL_OPENS_THE_CURATED_SHELF()
    {
        using var probes = new LiveSetupProbes(_home, GlyphSet.Unicode, TextWriter.Null);

        Assert.Equal(UploaderAllowlist.Load().DefaultView,
            probes.Allowlist.CuratedPublisherFor(HubSearchView.Curated));
    }

    //the membership check lives in CuratedPublisherFor alone, so a preference outside the list searches everyone
    [Fact]
    public void A_PREFERENCE_OUTSIDE_THE_LIST_FALLS_BACK_TO_EVERYONE()
    {
        var preferring = UploaderAllowlist.Load().Preferring("acme-weights");

        Assert.Null(preferring.CuratedPublisherFor(HubSearchView.Curated));
        Assert.Equal(preferring.Orgs, preferring.OrgsFor(HubSearchView.Curated));
    }

    [Fact]
    public void NO_PREFERENCE_LEAVES_THE_ALLOWLIST_EXACTLY_AS_SHIPPED()
    {
        var shipped = UploaderAllowlist.Load();
        Assert.Equal(shipped, shipped.Preferring(null));
    }
}
