using System.Security.Cryptography;
using Gatto.Core.Tools;

namespace Gatto.Core.Client;

//a reference to an attached image rather than its bytes, DisplayName names a pasted one whose path is a hash
public sealed record ImageRef(string Path, long Bytes, string Sha256, string MediaType,
    string? DisplayName = null)
{
    public static ImageRef FromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new ImageRef(path, bytes.LongLength, ShaOf(bytes), MediaTypeOf(path));
    }

    //the display name, falling back to the file's own name
    public string Name => DisplayName ?? System.IO.Path.GetFileName(Path);

    internal static string ShaOf(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    //content-sniffed rather than extension-trusted, a .png that is really text throws here
    internal static string MediaTypeOf(string path) => BinarySniff.DescribeFile(path) switch
    {
        "PNG image" => "image/png",
        "JPEG image" => "image/jpeg",
        "GIF image" => "image/gif",
        "BMP image" => "image/bmp",
        var other => throw new InvalidOperationException(
            $"{System.IO.Path.GetFileName(path)} is {other ?? "not an image"} — only PNG, JPEG, GIF and BMP can be attached"),
    };
}
