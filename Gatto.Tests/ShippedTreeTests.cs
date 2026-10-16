using System.Security.Cryptography;
using System.Text;
using Gatto.Extensions;

namespace Gatto.Tests;

//the twelve shipped files are real files under Gatto/Shipped, and these pins hold their exact bytes
public class ShippedTreeTests
{
    //the vetted hash folds newlines, so the byte and sha columns are what catch a line-ending change
    private static readonly Dictionary<string, (string VettedHash, int Bytes, string Sha)> Pins =
        new(StringComparer.Ordinal)
        {
            ["extensions/ask_user.csx"] =
                ("9ee8205f69c0d04e4c813296350407cde6da00368eedf8aea571c57eb68c3256",
                 6845, "e1f51f086f53ce4f39cebfdc2d9ad057010d5f8a66d50a020ed61076199f1c7b"),
            ["extensions/web_search.csx"] =
                ("4c9acb8c8681dc592a8451d92fb6988736f43c0fcca8b98859fca03792ab500f",
                 11835, "6a80e1c43e24a6d660dde9fbe040bf5cbd07a805dbbae50943f27a16d6d7a516"),
            ["extensions/cards.csx"] =
                ("1d56f684cd3868094f6668ac49a39d96abeb41d0eafe99dff452147b4b40f469",
                 8795, "a36da9665ec57b7b39923e0f19d5f5f5dd8fc13f942f9c286f653fa9f982f8fb"),
            ["extensions/undo.csx"] =
                ("091278e7e57e17bf2bf307db403f934d1c16a3e41ff29fb0f28e95eeb5d7052d",
                 28553, "64d46147ea999c4584c9bc149ebfcf6f11ee61cdd70617689ee708e1da916c09"),
            ["roles/code-review.json"] =
                ("42c2308c6198e0be8cc37a41be06c23619921d0a260900d211453933db6b1f0c",
                 1869, "d15ce43a36d45e526ac0a2d3154a1e22b9f7f06aaa5aabbc374138cc025347e4"),
            ["roles/coder.json"] =
                ("2d6ced6fbea38159984c627faa6d2aed0bfa4ec10a3f30c157da6ed471646694",
                 969, "a505da69211d96b79079a95cdd578b0228f2bdcec7b5dba064821c0c575e1a36"),
            ["roles/generalist.json"] =
                ("35b5efcf8469f21bcafcdafd6412e52606f56ec02036d2f9b65d60dbcfc5bf84",
                 3, "ca3d163bab055381827226140568f3bef7eaac187cebd76878e0b63e9e442356"),
            ["roles/oracle.json"] =
                ("0668a903f1072318ca5532da6089bcc537e2f64ffc8e27113f54e86318ced431",
                 993, "f0b9ec7b7b84baee9640036974791c2d9ae27daa13b9c1116f49c24cb76149e2"),
            ["roles/plan-review.json"] =
                ("c617f614f92238341dc40b96c16b0ea41e9f0fad87a3a0f409a1f1e69e9d3226",
                 1863, "ad5ad394b82ab8b949bd1dd0bf2c3ed00806af6e37ea35b3d7495ad944d043fd"),
            ["roles/raw.json"] =
                ("ca3121440f2b159c3e9f633dfc675995e470e11c1831b7b921e6b640090b4c41",
                 16, "02cf45d5dc440c228801dcf7378a19136ed3e7315a6fd291ff34bff644d957ee"),
            ["roles/security-review.json"] =
                ("e191359930f9c3b70a411b7903304dbe638140a69a9c8da57dabe3d410470ec5",
                 1989, "48fad8d22daac8ea7d824424173d2b0c2b1ddddea0b3e6e00a2eb80d42ae143d"),
            ["roles/spec-review.json"] =
                ("a1ed37a556b3361dbe8306532c07d5181a179f61a0ed08e6de21b0a8f161c7c0",
                 1884, "ac92bc0135abdedc6649cd8ba3a5dc06bec5062e7f64c36ec119ee72759a3830"),
        };

    [Fact]
    public void RAW_JSON_IS_ONE_KEY_AND_A_FINAL_LF_AND_LOADS_RAW()
    {
        Assert.Equal("{ \"raw\": true }\n", ShippedExtensions.Files["roles/raw.json"]);
        var dir = Directory.CreateTempSubdirectory("gatto-raw-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "raw.json"), ShippedExtensions.Files["roles/raw.json"]);
            Assert.True(Gatto.Roles.RoleFile.Load(dir, "raw").Raw);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    //each body hash is of the old agent file's text after its frontmatter, trimmed, so the move changed no word a reviewer reads
    [Theory]
    [InlineData("code-review", "Reviews code changes for correctness, simplicity, idiom, and test hygiene.",
        "4208a5aabd425c74a52412e01d5dbe04de0414e8766618d3292db31b65a8bfa8")]
    [InlineData("plan-review", "Reviews implementation plans against their spec for coverage, verifiability, and safe sequencing.",
        "4c8debdba24d1184bfcbdedf3e806b06f417525df6fc839ac3053d1f658365b3")]
    [InlineData("security-review", "Reviews code and config for security risk — OWASP-style, least privilege, fail-secure.",
        "12b9a4254d829a228cfbc7f6d09c140b39ec950e1306523fffd3ef3253fe2d4b")]
    [InlineData("spec-review", "Reviews specs and design docs for completeness, consistency, testability, and unambiguity.",
        "384a13597ea452e0ffd308383b9c29abcefbf22842d8a8b605087e3b9034d6b2")]
    public void THE_SHIPPED_REVIEWERS_LOAD_AS_RAW_CALLABLES(string name, string description, string bodySha)
    {
        var dir = Directory.CreateTempSubdirectory("gatto-reviewer-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, $"{name}.json"), ShippedExtensions.Files[$"roles/{name}.json"]);
            var role = Gatto.Roles.RoleFile.Load(dir, name);

            Assert.True(role.Raw);
            Assert.Equal(new[] { "read_file", "glob", "grep" }, role.Tools);
            Assert.Equal(10, role.Turns);
            Assert.Equal(description, role.Description);
            Assert.Equal(bodySha, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role.Append!))).ToLowerInvariant());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void THE_SHIPPED_SITTERS_CARRY_THEIR_DRESS()
    {
        var coder = ShippedRoleFiles.Load("coder");
        var oracle = ShippedRoleFiles.Load("oracle");

        Assert.Equal(("#4BBECD", "#0E7C86", 4), (coder.Tint, coder.TintLight, coder.Cat!.Count));
        Assert.Equal(("#B88AAF", "#834E8F", 6), (oracle.Tint, oracle.TintLight, oracle.Cat!.Count));
        foreach (var bare in new[] { "generalist", "raw" })
        {
            var role = ShippedRoleFiles.Load(bare);
            Assert.Null(role.Cat);
            Assert.Null(role.Tint);
            Assert.Null(role.TintLight);
        }
    }

    [Fact]
    public void SHIPPED_KEYS_ARE_EXACTLY_THESE()
    {
        //the assertion is exact equality, since any file dropped in that folder ships as a default
        Assert.Equal(
            Pins.Keys.OrderBy(k => k, StringComparer.Ordinal),
            ShippedExtensions.Files.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void SHIPPED_HASHES_ARE_UNCHANGED_BY_THE_MOVE()
    {
        foreach (var (key, pin) in Pins)
            Assert.Equal(pin.VettedHash,
                ShippedExtensions.VettedHashFor(key, ShippedExtensions.Files[key]));
    }

    [Fact]
    public void SHIPPED_BYTES_ARE_UNCHANGED_BY_THE_MOVE()
    {
        foreach (var (key, pin) in Pins)
        {
            var bytes = Encoding.UTF8.GetBytes(ShippedExtensions.Files[key]);
            Assert.Equal(pin.Bytes, bytes.Length);
            Assert.Equal(pin.Sha, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    [Fact]
    public void A_BOM_IN_A_SHIPPED_FILE_IS_REFUSED_BY_NAME()
    {
        //the loader refuses a BOM but not an unlisted resource, so catching a stray file is this suite's job
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("// hi")).ToArray();

        var ex = Assert.Throws<InvalidOperationException>(
            () => ShippedExtensions.DecodeShipped("extensions/ask_user.csx", withBom));
        Assert.Contains("extensions/ask_user.csx", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BOM", ex.Message, StringComparison.Ordinal);

        //the same bytes without the BOM decode fine, so the guard is about the BOM alone
        Assert.Equal("// hi", ShippedExtensions.DecodeShipped("extensions/ask_user.csx",
            Encoding.UTF8.GetBytes("// hi")));
    }

    [Fact]
    public void EVERY_EXTENSION_KEY_IS_FLAT()
    {
        //only a flat key is vetted, since the hash feeds the path before the bytes, and a nested file would load unvetted
        foreach (var key in ShippedExtensions.Files.Keys.Where(
            k => k.StartsWith("extensions/", StringComparison.Ordinal)))
            Assert.Equal(1, key.Count(c => c == '/'));
    }

    [Fact]
    public void NO_SHIPPED_TEXT_CARRIES_A_BOM()
    {
        foreach (var (key, text) in ShippedExtensions.Files)
            Assert.False(text.StartsWith('﻿'), $"{key} carries a BOM");
    }
}
