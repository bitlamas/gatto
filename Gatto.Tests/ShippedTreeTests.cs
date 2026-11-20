using System.Security.Cryptography;
using System.Text;
using Gatto.Extensions;

namespace Gatto.Tests;

//the seven shipped files are real files under Gatto/Shipped, and these pins hold their exact bytes
public class ShippedTreeTests
{
    //the vetted hash folds newlines, so the byte and sha columns are what catch a line-ending change
    private static readonly Dictionary<string, (string VettedHash, int Bytes, string Sha)> Pins =
        new(StringComparer.Ordinal)
        {
            ["extensions/ask_user.csx"] =
                ("29333e4da565dab0b21f8892b6e5d1b142eb86207aee13153b093961ab9c4349",
                 6790, "28a86c20c76c376f6ecd52a2e58529e472b9f8a468f5ac6d6111809e5fa8a02c"),
            ["extensions/web_search.csx"] =
                ("eaad2d32fce3b0ce6029f453447545338eee62353c7b7814497480696566367f",
                 11837, "6ac34e291304451c3985e35920c9cd7c4e4154d90dabc1aeab9268eedf893ff0"),
            ["extensions/cards.csx"] =
                ("93a8babecd5747e6f343e0b4cc3cc3bcf111acdf81d4cba8889732316ba044ad",
                 9028, "0edeefefbed2e9dddb7855634e2f452d086b5a894a5c1a8378ad4f9aa4022606"),
            ["extensions/undo.csx"] =
                ("fb109ce1147a4bbcaa4409f1023d009de7127a92234dddf5453a668a69458100",
                 29931, "0f57ad240e70a67a97c586f0aa1faafd9e48d9368da650aa507798ed7e029e99"),
            ["roles/raw.json"] =
                ("ca3121440f2b159c3e9f633dfc675995e470e11c1831b7b921e6b640090b4c41",
                 16, "02cf45d5dc440c228801dcf7378a19136ed3e7315a6fd291ff34bff644d957ee"),
            ["roles/reader.json"] =
                ("5621cbe801a98d5867fab744742543d78b63c84dcb85796c04ed37986bdfb400",
                 340, "d244f31fa8726d3e6705d3b51386eaad7a7032fb3bbb9dcbddb721924f608667"),
            ["roles/researcher.json"] =
                ("434532ada74e8f4a9b1f7f8bfeed16b533defb94f74642606414d968b8671a50",
                 435, "370219d022359f3bdd12f971f486987cf6c16e0e11772122c3531987d3c97628"),
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

    //raw, reader and researcher are the role files a home receives, the seven gatto once shipped are gone from the tree
    [Fact]
    public void RAW_READER_AND_RESEARCHER_ARE_THE_SHIPPED_ROLES() =>
        Assert.Equal(["roles/raw.json", "roles/reader.json", "roles/researcher.json"],
            ShippedExtensions.Files.Keys.Where(k => k.StartsWith("roles/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray());

    [Fact]
    public void SHIPPED_KEYS_ARE_EXACTLY_THESE()
    {
        //the assertion is exact equality, since any file dropped in that folder ships as a default
        Assert.Equal(
            Pins.Keys.OrderBy(k => k, StringComparer.Ordinal),
            ShippedExtensions.Files.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    //an installed copy written before the description and policy cut still runs vetted, so the revision before it stays in the set
    [Fact]
    public void THE_ASK_USER_REVISION_BEFORE_THE_PREFIX_CUT_STAYS_VETTED() =>
        Assert.Contains("9ee8205f69c0d04e4c813296350407cde6da00368eedf8aea571c57eb68c3256", ShippedExtensions.VettedContentHashes);

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
