using System.Security.Cryptography;
using System.Text;
using Gatto.Extensions;

namespace Gatto.Tests;

//the nine shipped files are real files under Gatto/Shipped, and these pins hold their exact bytes
public class ShippedTreeTests
{
    //the vetted hash folds newlines, so the byte and sha columns are what catch a line-ending change
    private static readonly Dictionary<string, (string VettedHash, int Bytes, string Sha)> Pins =
        new(StringComparer.Ordinal)
        {
            ["agents/code-review.md"] =
                ("9500c6ebf14651674359fd8ea4d06d26761e3b46cdb46387daa8917d09b2c147",
                 1825, "b055390364d8ae9dfb815e0d9ce958dcaa996010be56b1808603a708dfcdbf3e"),
            ["agents/plan-review.md"] =
                ("35824a507af47ca8a176dc559fb63c90c85284bd45f8fbd35d0f1e60ff917d48",
                 1817, "449a0421d832c3e0579e456d541e7872b182f415e9c61b0b7b3d4811c493be63"),
            ["agents/security-review.md"] =
                ("01cef7e62a4a1fadc7b4f2e31b3ff9481343b389406688543b722f85fbeca0b5",
                 1943, "794e1a9c65f49087996854f5f9395e0a69e9e64fd66bb78ba9da69a2e4d3cd1f"),
            ["agents/spec-review.md"] =
                ("7ad55072464af5ef199d658fd67b912b65117aadec600bc2aa1de2cbdefb1651",
                 1834, "64d20df4a701df17d8ba2bb5df96f11d95ea55889ea2ccc86b37ea2484ee2ba1"),
            ["extensions/ask_user.csx"] =
                ("9ee8205f69c0d04e4c813296350407cde6da00368eedf8aea571c57eb68c3256",
                 6845, "e1f51f086f53ce4f39cebfdc2d9ad057010d5f8a66d50a020ed61076199f1c7b"),
            ["extensions/web_search.csx"] =
                ("4c9acb8c8681dc592a8451d92fb6988736f43c0fcca8b98859fca03792ab500f",
                 11835, "6a80e1c43e24a6d660dde9fbe040bf5cbd07a805dbbae50943f27a16d6d7a516"),
            ["roles/coder.json"] =
                ("64b94c93cca7ab9581c7ab6b73bfc6d9168a9f8b7e54cc6999961629177fffee",
                 848, "84571b1538f535289b5c216ebc7236264f9208bab950b3ecf6649b28a43d4607"),
            ["roles/generalist.json"] =
                ("35b5efcf8469f21bcafcdafd6412e52606f56ec02036d2f9b65d60dbcfc5bf84",
                 3, "ca3d163bab055381827226140568f3bef7eaac187cebd76878e0b63e9e442356"),
            ["roles/oracle.json"] =
                ("ce9dff2a6ebe3e50b299dc43d0de4275cb996e0e2027d6fbcb04b8f279fb0d87",
                 1258, "be55d3a122040f2206970f17fe38a9496b1342d7787f772c2af437ecdef3114a"),
        };

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
