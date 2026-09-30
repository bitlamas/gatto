namespace Gatto.Core.Tools;

//names the format of a file whose bytes the model can't use, binary alone would only tell it to give up
public static class BinarySniff
{
    //enough bytes to clear any header, few enough that sniffing a huge file is one buffered read
    public const int HeadBytes = 8000;

    //magic numbers, longest-first where prefixes could collide, deliberately short for the common cases
    private static readonly (byte[] Magic, string Name)[] Signatures =
    [
        ([0xFF, 0xD8, 0xFF], "JPEG image"),
        ([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], "PNG image"),
        ([(byte)'G', (byte)'I', (byte)'F', (byte)'8'], "GIF image"),
        ([(byte)'B', (byte)'M'], "BMP image"),
        ([0x00, 0x00, 0x01, 0x00], "ICO icon"),
        ([(byte)'%', (byte)'P', (byte)'D', (byte)'F'], "PDF document"),
        ([(byte)'P', (byte)'K', 0x03, 0x04], "ZIP archive (or a zip-based format: docx/xlsx/jar)"),
        ([(byte)'M', (byte)'Z'], "Windows executable"),
        ([0x7F, (byte)'E', (byte)'L', (byte)'F'], "ELF binary"),
        ([(byte)'G', (byte)'G', (byte)'U', (byte)'F'], "GGUF model file"),
        ([0x1F, 0x8B], "gzip archive"),
        ([(byte)'O', (byte)'g', (byte)'g', (byte)'S'], "Ogg media"),
        ([(byte)'R', (byte)'I', (byte)'F', (byte)'F'], "RIFF media (wav/avi/webp)"),
    ];

    //the format name, binary, or null when it is text, and the BOM check must come first or UTF-16 reads as binary
    public static string? Describe(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return null;   //an empty file is empty text, so nothing to detect

        //a BOM means text, checked before the NUL rule can misfire on UTF-16
        if (StartsWith(head, [0xEF, 0xBB, 0xBF])) return null;   //UTF-8
        if (StartsWith(head, [0xFF, 0xFE])) return null;         //UTF-16 LE
        if (StartsWith(head, [0xFE, 0xFF])) return null;         //UTF-16 BE

        foreach (var (magic, name) in Signatures)
            if (StartsWith(head, magic)) return name;

        //a NUL byte means binary, no text encoding we read without a BOM has one
        return head.IndexOf((byte)0) >= 0 ? "binary" : null;
    }

    //sniff a file on disk, reading only the first HeadBytes
    public static string? DescribeFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> buf = stackalloc byte[HeadBytes];
        var n = fs.Read(buf);
        return Describe(buf[..n]);
    }

    private static bool StartsWith(ReadOnlySpan<byte> head, ReadOnlySpan<byte> prefix) =>
        head.Length >= prefix.Length && head[..prefix.Length].SequenceEqual(prefix);
}
