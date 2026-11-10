using System.Runtime.Versioning;
using System.Text;
using Gatto.Core;
using Gatto.Core.Home;

namespace Gatto.Tests;

//an extension's sealed secrets, one file per extension name, read on every call and written whole
public sealed class ExtensionSecretsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-ext-secrets-").FullName;

    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    //a deterministic stand-in for the OS seal, counting the writes it seals
    private sealed class FakeSeal(bool failUnseal = false) : ISecretSeal
    {
        public int Seals;
        public byte[] Seal(byte[] plain) { Seals++; return [.. plain.Select(b => (byte)(b ^ 0x5A))]; }
        public byte[] Unseal(byte[] @sealed, string what) =>
            failUnseal ? throw new GattoConfigException($"cannot unseal {what}: wrong account") : [.. @sealed.Select(b => (byte)(b ^ 0x5A))];
    }

    private ExtensionSecrets New(ISecretSeal seal, string name = "alpha") => new(_home, name, seal);

    //a logout or a login while another caller reads the store, as a usage read on the pool does beside a typed /logout, never fails on the open file
    [Fact]
    public void A_read_in_flight_never_fails_a_remove_or_a_write()
    {
        var s = New(new FakeSeal());
        s.Set("refresh", "rt-0");
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) _ = s.Get("refresh");
        });
        try
        {
            for (var i = 0; i < 300; i++)
            {
                s.RemoveAll();
                s.Set("refresh", "rt-" + i);
            }
        }
        finally
        {
            stop.Cancel();
            reader.GetAwaiter().GetResult();
        }

        Assert.Equal("rt-299", s.Get("refresh"));
    }

    [Fact]
    public void Set_then_Get_round_trips_and_no_plaintext_of_the_value_is_under_the_home()
    {
        var s = New(new FakeSeal());

        s.Set("refresh_token", "rt-value-1234");

        Assert.Equal(Path.Combine(_home, "secrets", "alpha.bin"), s.Path);
        Assert.Equal("rt-value-1234", New(new FakeSeal()).Get("refresh_token"));
        Assert.Null(s.Get("absent"));
        var needle = Encoding.UTF8.GetBytes("rt-value-1234");
        foreach (var file in Directory.EnumerateFiles(_home, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.False(bytes.AsSpan().IndexOf(needle) >= 0, $"plaintext of the value in {file}");
        }
    }

    [Fact]
    public void Set_of_several_keys_is_one_write()
    {
        var seal = new FakeSeal();
        var s = New(seal);

        s.Set(new Dictionary<string, string> { ["access"] = "a", ["refresh"] = "r" });

        Assert.Equal(1, seal.Seals);
        Assert.Equal("a", s.Get("access"));
        Assert.Equal("r", s.Get("refresh"));
    }

    [Fact]
    public void Remove_and_RemoveAll()
    {
        var s = New(new FakeSeal());
        s.Set(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });

        s.Remove("a");
        Assert.Null(s.Get("a"));
        Assert.Equal("2", s.Get("b"));

        s.RemoveAll();
        Assert.False(File.Exists(s.Path));
        Assert.Null(s.Get("b"));
    }

    [Fact]
    public void Any_is_false_with_no_file_and_after_RemoveAll()
    {
        var s = New(new FakeSeal());
        Assert.False(s.Any());
        s.Set("a", "1");
        Assert.True(s.Any());
        s.RemoveAll();
        Assert.False(s.Any());
    }

    [Fact]
    public void Two_names_are_two_files()
    {
        New(new FakeSeal(), "alpha").Set("t", "A");
        New(new FakeSeal(), "beta").Set("t", "B");

        Assert.Equal("A", New(new FakeSeal(), "alpha").Get("t"));
        Assert.Equal("B", New(new FakeSeal(), "beta").Get("t"));
    }

    [Fact]
    public void A_file_that_will_not_unseal_throws_naming_the_path_and_stays()
    {
        New(new FakeSeal()).Set("t", "v");
        var s = New(new FakeSeal(failUnseal: true));

        var ex = Assert.Throws<GattoConfigException>(() => s.Get("t"));

        Assert.Contains(s.Path, ex.Message);
        Assert.True(File.Exists(s.Path));
    }

    [Fact]
    public void The_unsupported_seal_throws_the_config_exception()
    {
        var seal = new UnsupportedSeal();
        const string message = "secrets need the Windows data protection API; this OS has no seal";

        Assert.Equal(message, Assert.Throws<GattoConfigException>(() => seal.Seal([1])).Message);
        Assert.Equal(message, Assert.Throws<GattoConfigException>(() => seal.Unseal([1], "x")).Message);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void The_dpapi_seal_round_trips_through_ExtensionSecrets()
    {
        if (!OperatingSystem.IsWindows()) return;
        New(new DpapiSeal()).Set("t", "real-seal-value");

        Assert.Equal("real-seal-value", New(new DpapiSeal()).Get("t"));
    }

    [Fact]
    public void A_held_target_throws_could_not_write_and_keeps_the_old_file()
    {
        var s = New(new FakeSeal());
        s.Set("t", "old");
        var before = File.ReadAllBytes(s.Path);

        using (new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Assert.Throws<GattoConfigException>(() => s.Set("t", "new"));
            Assert.StartsWith($"could not write {s.Path}: ", ex.Message);
        }

        Assert.Equal(before, File.ReadAllBytes(s.Path));
        Assert.Equal(["alpha.bin"], Directory.GetFiles(Path.Combine(_home, "secrets")).Select(Path.GetFileName));
    }

    [Fact]
    public void A_held_bin_that_cannot_be_read_says_could_not_read()
    {
        var s = New(new FakeSeal());
        s.Set("t", "v");

        using (new FileStream(s.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<GattoConfigException>(() => s.Set("t", "w"));
            Assert.StartsWith($"could not read {s.Path}: ", ex.Message);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void The_bin_written_by_Set_carries_the_restricted_acl()
    {
        if (!OperatingSystem.IsWindows()) return;
        var s = New(new FakeSeal());

        s.Set("t", "v");

        SecretFileTests.AssertRestricted(s.Path);
    }

    [Fact]
    public void ToString_names_the_file_and_no_value()
    {
        var s = New(new FakeSeal());
        s.Set("t", "secret-value");

        Assert.Equal(s.Path, s.ToString());
    }
}
