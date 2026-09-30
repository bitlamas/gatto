using Gatto.Core.Home;

namespace Gatto.Tests;

//a project file can only switch things off (compat, memory, auto_compact). an unreadable file reads as null, malformed content throws
public class ProjectFileConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-pfc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string content) => File.WriteAllText(Path.Combine(_dir, ".gatto.json"), content);

    [Fact]
    public void MissingFile_Null()
    {
        Assert.Null(ProjectFileConfig.TryReadCompat(_dir));
    }

    [Fact]
    public void CompatTrue_Parsed()
    {
        Write("""{"context_files":{"compat":true}}""");

        Assert.True(ProjectFileConfig.TryReadCompat(_dir));
    }

    [Fact]
    public void CompatFalse_Parsed()
    {
        Write("""{"context_files":{"compat":false}}""");

        Assert.False(ProjectFileConfig.TryReadCompat(_dir));
    }

    [Fact]
    public void UnknownTopKey_Throws()
    {
        //an unknown top-level key is rejected, and the message names the file and the key
        Write("""{"tools":{}}""");

        var ex = Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadCompat(_dir));
        Assert.Contains("tools", ex.Message);
        Assert.Contains(".gatto.json", ex.Message);
        Assert.Contains("compat", ex.Message);
    }

    [Fact]
    public void UnknownNestedKey_Throws()
    {
        //use a real home-config key, so the test says a valid user key is still refused in a project file
        Write("""{"context_files":{"home":true}}""");

        var ex = Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadCompat(_dir));
        Assert.Contains("context_files.home", ex.Message);   //match the full key path, so a passing assertion is not a coincidence of substrings.
        Assert.Contains("compat", ex.Message);
    }

    [Fact]
    public void WrongType_Throws()
    {
        Write("""{"context_files":{"compat":"yes"}}""");

        var ex = Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadCompat(_dir));
        Assert.Contains("compat", ex.Message);
    }

    [Fact]
    public void InvalidJson_Throws()
    {
        Write("{ not json");

        Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadCompat(_dir));
    }

    [Fact]
    public void RootNotAnObject_Throws()
    {
        Write("[1,2,3]");

        Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadCompat(_dir));
    }

    [Fact]
    public void ContextFilesWrongType_Throws()
    {
        Write("""{"context_files":"nope"}""");

        Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadCompat(_dir));
    }

    [Fact]
    public void EmptyObject_Null()
    {
        //an empty object is well-formed and pins nothing, so it reads as no file rather than as malformed content.
        Write("{}");

        Assert.Null(ProjectFileConfig.TryReadCompat(_dir));
    }

    [Fact]
    public void ContextFilesPresentWithoutCompat_Null()
    {
        Write("""{"context_files":{}}""");

        Assert.Null(ProjectFileConfig.TryReadCompat(_dir));
    }

    [Fact]
    public void UnreadableFile_Null_NotThrown()
    {
        var path = Path.Combine(_dir, ".gatto.json");
        File.WriteAllText(path, """{"context_files":{"compat":true}}""");

        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        //a locked file must read as absent rather than throw, while malformed content still throws

        Assert.Null(ProjectFileConfig.TryReadCompat(_dir));
    }

    //a project file may remove capability and never arm it, so false disables memory for the subtree and true is a legal no-op

    [Fact]
    public void MemoryEnabledFalse_Parsed()
    {
        Write("""{"memory":{"enabled":false}}""");
        Assert.False(ProjectFileConfig.TryReadMemoryEnabled(_dir));
    }

    [Fact]
    public void MemoryEnabledTrue_Parsed_ButIsANoOpAtTheResolver()
    {
        Write("""{"memory":{"enabled":true}}""");
        Assert.True(ProjectFileConfig.TryReadMemoryEnabled(_dir));
    }

    [Fact]
    public void MemorySection_PinsNothing_Null()
    {
        Write("""{"memory":{}}""");
        Assert.Null(ProjectFileConfig.TryReadMemoryEnabled(_dir));
    }

    [Fact]
    public void BothSections_CoexistAndAreReadIndependently()
    {
        Write("""{"context_files":{"compat":true},"memory":{"enabled":false}}""");
        Assert.True(ProjectFileConfig.TryReadCompat(_dir));
        Assert.False(ProjectFileConfig.TryReadMemoryEnabled(_dir));
    }

    [Fact]
    public void UnknownMemoryKey_Throws()
    {
        Write("""{"memory":{"index_budget":500}}""");
        var ex = Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadMemoryEnabled(_dir));
        Assert.Contains("index_budget", ex.Message);
    }

    [Fact]
    public void MemoryEnabledWrongType_Throws()
    {
        Write("""{"memory":{"enabled":"yes"}}""");
        Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadMemoryEnabled(_dir));
    }

    [Fact]
    public void StillStrict_AnUnrelatedTopLevelKeyThrows()
    {
        //nothing about tools, extensions or server settings may be read from a project file
        Write("""{"llama_server":"C:\evil.exe"}""");
        Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadCompat(_dir));
    }

    [Fact]
    public void EffectiveMemoryEnabled_AncestorFalse_DisablesTheSubtree()
    {
        var deep = Path.Combine(_dir, "src", "sub");
        Directory.CreateDirectory(deep);
        Write("""{"memory":{"enabled":false}}""");   //this file sits at the root, an ancestor of the deeper directory.

        Assert.False(ProjectFileConfig.EffectiveMemoryEnabled(deep, configured: true));
    }

    [Fact]
    public void EffectiveMemoryEnabled_ProjectTrue_CannotReArmAGloballyDisabledMemory()
    {
        //project config only removes capability, so a true here must not undo a home config that turned memory off
        Write("""{"memory":{"enabled":true}}""");

        Assert.False(ProjectFileConfig.EffectiveMemoryEnabled(_dir, configured: false));
    }

    [Fact]
    public void EffectiveMemoryEnabled_NearestWins_ADeeperTrueDoesNotUndoAnAncestorFalse()
    {
        //nearest ancestor wins picks which file applies, so a deeper true is still a no-op and the subtree stays off
        var deep = Path.Combine(_dir, "src");
        Directory.CreateDirectory(deep);
        Write("""{"memory":{"enabled":false}}""");
        File.WriteAllText(Path.Combine(deep, ".gatto.json"), """{"memory":{"enabled":true}}""");

        Assert.False(ProjectFileConfig.EffectiveMemoryEnabled(deep, configured: true));
    }

    [Fact]
    public void EffectiveMemoryEnabled_NoFilesAnywhere_IsTheConfiguredValue()
    {
        Assert.True(ProjectFileConfig.EffectiveMemoryEnabled(_dir, configured: true));
        Assert.False(ProjectFileConfig.EffectiveMemoryEnabled(_dir, configured: false));
    }

    //the search is deepest-first, so the reported file must be the nearest one that disabled it
    [Fact]
    public void EffectiveMemoryEnabled_NamesTheNearestFileThatTurnedItOff()
    {
        var deep = Path.Combine(_dir, "src");
        Directory.CreateDirectory(deep);
        Write("""{"memory":{"enabled":false}}""");
        File.WriteAllText(Path.Combine(deep, ".gatto.json"), """{"memory":{"enabled":false}}""");

        Assert.False(ProjectFileConfig.EffectiveMemoryEnabled(deep, configured: true, out var by));
        Assert.Equal(Path.Combine(deep, ".gatto.json"), by);
    }

    [Fact]
    public void AutoCompactFalse_Parsed()
    {
        //a top-level bare boolean mirrors the home gatto.json key it vetoes, since a wrapper object would spell one setting two ways
        Write("""{"auto_compact":false}""");
        Assert.False(ProjectFileConfig.TryReadAutoCompact(_dir));
    }

    [Fact]
    public void AutoCompactTrue_Parsed_ButIsANoOpAtTheResolver()
    {
        Write("""{"auto_compact":true}""");

        Assert.True(ProjectFileConfig.TryReadAutoCompact(_dir));
        //the value parses, yet it can never arm the feature
        Assert.False(ProjectFileConfig.EffectiveAutoCompact(_dir, configured: false));
    }

    [Fact]
    public void AutoCompactAbsent_Null()
    {
        Write("""{"memory":{"enabled":true}}""");
        Assert.Null(ProjectFileConfig.TryReadAutoCompact(_dir));
    }

    [Fact]
    public void AutoCompactWrongType_Throws()
    {
        Write("""{"auto_compact":"no"}""");
        var ex = Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadAutoCompact(_dir));
        Assert.Contains("must be a boolean", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllThreeKeysCoexist_AndAreReadIndependently()
    {
        //strictness lives in one shared ReadRoot, so reading one key must not reject a file that holds the others
        Write("""{"context_files":{"compat":true},"memory":{"enabled":false},"auto_compact":false}""");

        Assert.True(ProjectFileConfig.TryReadCompat(_dir));
        Assert.False(ProjectFileConfig.TryReadMemoryEnabled(_dir));
        Assert.False(ProjectFileConfig.TryReadAutoCompact(_dir));
    }

    [Fact]
    public void THE_REJECTION_MESSAGE_NAMES_EVERY_ACCEPTED_KEY()
    {
        //the message must name every accepted key, since a sentence and a list of the same fact can drift apart
        Write("""{"nonsense":1}""");
        var ex = Assert.Throws<GattoConfigException>(() => ProjectFileConfig.TryReadCompat(_dir));

        Assert.Contains("context_files.compat", ex.Message, StringComparison.Ordinal);
        Assert.Contains("memory.enabled", ex.Message, StringComparison.Ordinal);
        Assert.Contains("auto_compact", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveAutoCompact_AncestorFalse_DisablesTheSubtree()
    {
        var deep = Path.Combine(_dir, "src", "sub");
        Directory.CreateDirectory(deep);
        Write("""{"auto_compact":false}""");

        Assert.False(ProjectFileConfig.EffectiveAutoCompact(deep, configured: true));
    }

    [Fact]
    public void EffectiveAutoCompact_NamesTheNearestFileThatTurnedItOff()
    {
        var deep = Path.Combine(_dir, "src");
        Directory.CreateDirectory(deep);
        Write("""{"auto_compact":false}""");
        File.WriteAllText(Path.Combine(deep, ".gatto.json"), """{"auto_compact":false}""");

        Assert.False(ProjectFileConfig.EffectiveAutoCompact(deep, configured: true, out var by));
        Assert.Equal(Path.Combine(deep, ".gatto.json"), by);
    }

    [Fact]
    public void EffectiveAutoCompact_NoFilesAnywhere_IsTheConfiguredValue()
    {
        Assert.True(ProjectFileConfig.EffectiveAutoCompact(_dir, configured: true));
        Assert.False(ProjectFileConfig.EffectiveAutoCompact(_dir, configured: false));
    }
}
