using System.IO.Compression;

namespace Gatto.Core.Client;

//GIF and BMP written as PNG, the same bytes every time since the cache and the prefix depend on it
internal static class ImagePng
{
    //past this many pixels a decode holds gigabytes, and no vision encoder reads that many
    internal const long MaxPixels = 50_000_000;

    internal readonly record struct Pixels(int Width, int Height, byte[] Rgba);

    //whether a media type goes out as PNG instead of as itself
    public static bool Converts(string mediaType) => mediaType is "image/gif" or "image/bmp";

    //the pixels of a GIF's first frame or of a BMP, InvalidDataException naming what is wrong when they cannot be read
    public static Pixels Decode(ReadOnlySpan<byte> bytes, string mediaType) => mediaType switch
    {
        "image/gif" => Gif(bytes),
        "image/bmp" => Bmp(bytes),
        _ => throw new ArgumentException($"{mediaType} is not converted", nameof(mediaType)),
    };

    //the PNG bytes for a GIF or a BMP
    public static byte[] Convert(ReadOnlySpan<byte> bytes, string mediaType) => Encode(Decode(bytes, mediaType));

    private static InvalidDataException Bad(string what) => new(what);

    private static int U16(ReadOnlySpan<byte> b, int at) => b[at] | b[at + 1] << 8;

    private static uint U32(ReadOnlySpan<byte> b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

    private static void Size(long width, long height, string kind)
    {
        if (width <= 0 || height <= 0) throw Bad($"the {kind} has no pixels");
        if (width * height > MaxPixels) throw Bad($"the {kind} is {width}x{height}, more pixels than gatto converts");
    }

    //the first frame only, transparent where it does not reach the logical screen
    private static Pixels Gif(ReadOnlySpan<byte> b)
    {
        if (b.Length < 13 || b[0] != 'G' || b[1] != 'I' || b[2] != 'F') throw Bad("the GIF header is missing");
        var screenW = U16(b, 6);
        var screenH = U16(b, 8);
        var flags = b[10];
        var pos = 13;
        byte[]? global = null;
        if ((flags & 0x80) != 0)
        {
            var n = 3 << ((flags & 7) + 1);
            if (pos + n > b.Length) throw Bad("the GIF ends inside its colour table");
            global = b.Slice(pos, n).ToArray();
            pos += n;
        }
        var transparent = -1;
        while (true)
        {
            if (pos >= b.Length) throw Bad("the GIF ends before its first frame");
            var block = b[pos++];
            if (block == 0x21)
            {
                if (pos >= b.Length) throw Bad("the GIF ends inside an extension");
                var label = b[pos++];
                //the graphic control block: size, flags, delay, transparent index
                if (label == 0xF9 && pos + 4 < b.Length && b[pos] >= 4 && (b[pos + 1] & 1) != 0) transparent = b[pos + 4];
                pos = SkipSubBlocks(b, pos);
            }
            else if (block == 0x2C) return GifFrame(b, pos, screenW, screenH, global, transparent);
            else if (block == 0x3B) throw Bad("the GIF holds no frame");
            else throw Bad($"the GIF holds an unknown block 0x{block:X2}");
        }
    }

    private static int SkipSubBlocks(ReadOnlySpan<byte> b, int pos)
    {
        while (true)
        {
            if (pos >= b.Length) throw Bad("the GIF ends inside a block");
            var n = b[pos++];
            if (n == 0) return pos;
            pos += n;
        }
    }

    private static Pixels GifFrame(ReadOnlySpan<byte> b, int pos, int screenW, int screenH, byte[]? global, int transparent)
    {
        if (pos + 10 > b.Length) throw Bad("the GIF ends inside its first frame");
        var left = U16(b, pos);
        var top = U16(b, pos + 2);
        var w = U16(b, pos + 4);
        var h = U16(b, pos + 6);
        var flags = b[pos + 8];
        pos += 9;
        var palette = global;
        if ((flags & 0x80) != 0)
        {
            var n = 3 << ((flags & 7) + 1);
            if (pos + n > b.Length) throw Bad("the GIF ends inside a frame's colour table");
            palette = b.Slice(pos, n).ToArray();
            pos += n;
        }
        if (palette is null) throw Bad("the GIF has no colour table");
        //a screen of zero size is drawn at the frame's own size
        if (screenW == 0 || screenH == 0) { screenW = w; screenH = h; left = 0; top = 0; }
        Size(screenW, screenH, "GIF");
        if ((long)w * h > MaxPixels) throw Bad($"the GIF's first frame is {w}x{h}, more pixels than gatto converts");
        if (pos >= b.Length) throw Bad("the GIF ends before its pixel data");
        var minCode = b[pos++];
        if (minCode is < 1 or > 8) throw Bad($"the GIF's code size {minCode} is out of range");
        var data = new List<byte>();
        while (pos < b.Length)
        {
            var n = b[pos++];
            if (n == 0) break;
            var take = Math.Min(n, b.Length - pos);
            for (var i = 0; i < take; i++) data.Add(b[pos + i]);
            pos += n;
        }
        var total = w * h;
        var index = new byte[total];
        var got = Lzw(data, minCode, index);
        var rgba = new byte[(long)screenW * screenH * 4];
        var interlaced = (flags & 0x40) != 0;
        var colours = palette.Length / 3;
        for (var k = 0; k < got; k++)
        {
            var row = k / w;
            var y = top + (interlaced ? Interlaced(row, h) : row);
            var x = left + k % w;
            if (x >= screenW || y >= screenH) continue;
            int c = index[k];
            if (c == transparent) continue;
            var at = ((long)y * screenW + x) * 4;
            //an index past the table is drawn black, as decoders do
            if (c < colours) { rgba[at] = palette[c * 3]; rgba[at + 1] = palette[c * 3 + 1]; rgba[at + 2] = palette[c * 3 + 2]; }
            rgba[at + 3] = 255;
        }
        return new Pixels(screenW, screenH, rgba);
    }

    //the image row a stored row lands on: every 8th from 0, every 8th from 4, every 4th from 2, every 2nd from 1
    private static int Interlaced(int row, int h)
    {
        var pass1 = (h + 7) / 8;
        if (row < pass1) return row * 8;
        row -= pass1;
        var pass2 = (h + 3) / 8;
        if (row < pass2) return 4 + row * 8;
        row -= pass2;
        var pass3 = (h + 1) / 4;
        if (row < pass3) return 2 + row * 4;
        return 1 + (row - pass3) * 2;
    }

    //the colour indices in order, a frame cut short leaves the rest transparent
    private static int Lzw(List<byte> data, int minCode, byte[] output)
    {
        var clear = 1 << minCode;
        var end = clear + 1;
        var size = minCode + 1;
        var next = clear + 2;
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var stack = new byte[4097];
        for (var i = 0; i < clear; i++) suffix[i] = (byte)i;
        var prev = -1;
        byte first = 0;
        var buffer = 0;
        var bits = 0;
        var at = 0;
        var outAt = 0;
        while (outAt < output.Length)
        {
            while (bits < size)
            {
                if (at >= data.Count) return outAt;
                buffer |= data[at++] << bits;
                bits += 8;
            }
            var code = buffer & ((1 << size) - 1);
            buffer >>= size;
            bits -= size;
            if (code == clear) { size = minCode + 1; next = clear + 2; prev = -1; continue; }
            if (code == end) break;
            if (prev == -1)
            {
                if (code >= clear) throw Bad("the GIF's pixel data is corrupt");
                output[outAt++] = (byte)code;
                prev = code;
                first = (byte)code;
                continue;
            }
            if (code > next || (code == next && next >= 4096)) throw Bad("the GIF's pixel data is corrupt");
            var sp = 0;
            var c = code;
            if (code == next) { stack[sp++] = first; c = prev; }
            while (c >= clear)
            {
                stack[sp++] = suffix[c];
                c = prefix[c];
            }
            first = suffix[c];
            stack[sp++] = first;
            while (sp > 0 && outAt < output.Length) output[outAt++] = stack[--sp];
            if (next < 4096)
            {
                prefix[next] = prev;
                suffix[next] = first;
                next++;
                if (next == 1 << size && size < 12) size++;
            }
            prev = code;
        }
        return outAt;
    }

    //uncompressed, bit fields, RLE8 and RLE4, an embedded JPEG or PNG is refused
    private static Pixels Bmp(ReadOnlySpan<byte> b)
    {
        if (b.Length < 26 || b[0] != 'B' || b[1] != 'M') throw Bad("the BMP header is missing");
        var dataAt = U32(b, 10);
        var header = (int)U32(b, 14);
        int w, h, bpp;
        uint compression = 0, used = 0;
        uint rMask = 0, gMask = 0, bMask = 0, aMask = 0;
        if (header == 12)
        {
            w = U16(b, 18);
            h = U16(b, 20);
            bpp = U16(b, 24);
        }
        else if (header >= 40)
        {
            if (14 + header > b.Length) throw Bad("the BMP ends inside its header");
            w = (int)U32(b, 18);
            h = (int)U32(b, 22);
            bpp = U16(b, 28);
            compression = U32(b, 30);
            used = U32(b, 46);
            var masksAt = header >= 52 ? 54 : compression is 3 or 6 ? 14 + header : -1;
            var maskCount = header >= 56 ? 4 : header >= 52 ? 3 : compression == 6 ? 4 : 3;
            if (masksAt > 0)
            {
                if (masksAt + maskCount * 4 > b.Length) throw Bad("the BMP ends inside its colour masks");
                rMask = U32(b, masksAt);
                gMask = U32(b, masksAt + 4);
                bMask = U32(b, masksAt + 8);
                if (maskCount == 4) aMask = U32(b, masksAt + 12);
            }
        }
        else throw Bad($"the BMP header of {header} bytes is not one gatto reads");
        var topDown = h < 0;
        if (topDown) h = -h;
        Size(w, h, "BMP");
        if (compression is 4 or 5) throw Bad("the BMP holds an embedded JPEG or PNG");
        if (compression is not (0 or 1 or 2 or 3 or 6)) throw Bad($"the BMP compression {compression} is not one gatto reads");
        if (bpp is not (1 or 4 or 8 or 16 or 24 or 32)) throw Bad($"the BMP depth of {bpp} bits is not one gatto reads");
        if (compression == 1 && bpp != 8 || compression == 2 && bpp != 4) throw Bad("the BMP's RLE does not match its depth");
        if (compression is 3 or 6 && bpp is not (16 or 32)) throw Bad("the BMP's bit fields do not match its depth");
        if (dataAt >= b.Length) throw Bad("the BMP ends before its pixel data");

        byte[]? palette = null;
        if (bpp <= 8)
        {
            var entry = header == 12 ? 3 : 4;
            var count = used == 0 || used > (1u << bpp) ? 1 << bpp : (int)used;
            var at = 14 + header + (header == 40 && compression == 3 ? 12 : header == 40 && compression == 6 ? 16 : 0);
            palette = new byte[count * 3];
            for (var i = 0; i < count; i++)
            {
                var p = at + i * entry;
                if (p + 3 > b.Length) break;
                palette[i * 3] = b[p + 2];
                palette[i * 3 + 1] = b[p + 1];
                palette[i * 3 + 2] = b[p];
            }
        }

        var rgba = new byte[(long)w * h * 4];
        var pixels = b[(int)dataAt..];
        if (compression is 1 or 2) Rle(pixels, w, h, compression == 1, palette!, rgba);
        else
        {
            var stride = ((long)bpp * w + 31) / 32 * 4;
            if (stride * h > pixels.Length) throw Bad("the BMP ends inside its pixel data");
            if (compression == 0 && bpp == 16) { rMask = 0x7C00; gMask = 0x03E0; bMask = 0x001F; aMask = 0; }
            var alpha = compression is 3 or 6 && aMask != 0;
            var seenAlpha = false;
            for (var r = 0; r < h; r++)
            {
                var row = pixels.Slice((int)(stride * r), (int)stride);
                var y = topDown ? r : h - 1 - r;
                for (var x = 0; x < w; x++)
                {
                    var at = ((long)y * w + x) * 4;
                    switch (bpp)
                    {
                        case 1 or 4 or 8:
                            var bit = x * bpp;
                            var idx = (row[bit >> 3] >> (8 - bpp - (bit & 7))) & ((1 << bpp) - 1);
                            Palette(palette!, idx, rgba, at);
                            break;
                        case 24:
                            rgba[at] = row[x * 3 + 2]; rgba[at + 1] = row[x * 3 + 1]; rgba[at + 2] = row[x * 3]; rgba[at + 3] = 255;
                            break;
                        case 32 when compression == 0:
                            rgba[at] = row[x * 4 + 2]; rgba[at + 1] = row[x * 4 + 1]; rgba[at + 2] = row[x * 4]; rgba[at + 3] = 255;
                            break;
                        default:
                            var v = bpp == 16 ? (uint)U16(row, x * 2) : U32(row, x * 4);
                            rgba[at] = Field(v, rMask); rgba[at + 1] = Field(v, gMask); rgba[at + 2] = Field(v, bMask);
                            rgba[at + 3] = alpha ? Field(v, aMask) : (byte)255;
                            seenAlpha |= alpha && rgba[at + 3] != 0;
                            break;
                    }
                }
            }
            //an alpha channel of zeros everywhere is one no writer meant, the image is shown opaque
            if (alpha && !seenAlpha)
                for (var i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        }
        return new Pixels(w, h, rgba);
    }

    private static void Palette(byte[] palette, int idx, byte[] rgba, long at)
    {
        if (idx * 3 < palette.Length) { rgba[at] = palette[idx * 3]; rgba[at + 1] = palette[idx * 3 + 1]; rgba[at + 2] = palette[idx * 3 + 2]; }
        rgba[at + 3] = 255;
    }

    //a masked channel widened to 8 bits by repeating its bits, as the common loaders do
    private static byte Field(uint v, uint mask)
    {
        if (mask == 0) return 0;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var bits = System.Numerics.BitOperations.PopCount(mask >> shift);
        var value = (v & mask) >> shift;
        if (bits >= 8) return (byte)(value >> (bits - 8));
        var r = value << (8 - bits);
        for (var have = bits; have < 8; have += bits) r |= r >> bits;
        return (byte)r;
    }

    //run-length rows from the bottom up, and a pixel the runs skip stays transparent
    private static void Rle(ReadOnlySpan<byte> p, int w, int h, bool eight, byte[] palette, byte[] rgba)
    {
        int x = 0, y = 0, at = 0;
        void Put(int idx)
        {
            if (x < w && y < h) Palette(palette, idx, rgba, ((long)(h - 1 - y) * w + x) * 4);
            x++;
        }
        while (at + 1 < p.Length && y < h)
        {
            int count = p[at], value = p[at + 1];
            at += 2;
            if (count > 0)
            {
                for (var i = 0; i < count; i++) Put(eight ? value : i % 2 == 0 ? value >> 4 : value & 15);
                continue;
            }
            if (value == 0) { x = 0; y++; }
            else if (value == 1) return;
            else if (value == 2)
            {
                if (at + 1 >= p.Length) return;
                x += p[at];
                y += p[at + 1];
                at += 2;
            }
            else
            {
                var bytes = eight ? value : (value + 1) / 2;
                if (at + bytes > p.Length) return;
                for (var i = 0; i < value; i++) Put(eight ? p[at + i] : i % 2 == 0 ? p[at + i / 2] >> 4 : p[at + i / 2] & 15);
                at += bytes + (bytes & 1);
            }
        }
    }

    //8-bit RGB when every pixel is opaque, RGBA otherwise, each row with the filter whose bytes sum smallest
    internal static byte[] Encode(Pixels px)
    {
        var opaque = true;
        for (var i = 3; i < px.Rgba.Length; i += 4)
            if (px.Rgba[i] != 255) { opaque = false; break; }
        var channels = opaque ? 3 : 4;
        var rowLen = px.Width * channels;
        var prior = new byte[rowLen];
        var row = new byte[rowLen];
        var candidates = new byte[5][];
        for (var f = 0; f < 5; f++) candidates[f] = new byte[rowLen + 1];

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < px.Height; y++)
            {
                var src = (long)y * px.Width * 4;
                for (var x = 0; x < px.Width; x++)
                    for (var c = 0; c < channels; c++) row[x * channels + c] = px.Rgba[src + x * 4 + c];
                var best = 0;
                var bestSum = long.MaxValue;
                for (var f = 0; f < 5; f++)
                {
                    var o = candidates[f];
                    o[0] = (byte)f;
                    long sum = 0;
                    for (var i = 0; i < rowLen; i++)
                    {
                        int a = i >= channels ? row[i - channels] : 0, up = prior[i], ul = i >= channels ? prior[i - channels] : 0;
                        var predicted = f switch { 0 => 0, 1 => a, 2 => up, 3 => (a + up) >> 1, _ => Paeth(a, up, ul) };
                        var v = (byte)(row[i] - predicted);
                        o[i + 1] = v;
                        sum += v < 128 ? v : 256 - v;
                    }
                    if (sum < bestSum) { bestSum = sum; best = f; }
                }
                z.Write(candidates[best]);
                (prior, row) = (row, prior);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)px.Width);
        BigEndian(ihdr, 4, (uint)px.Height);
        ihdr[8] = 8;
        ihdr[9] = (byte)(opaque ? 2 : 6);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void BigEndian(byte[] into, int at, uint v)
    {
        into[at] = (byte)(v >> 24);
        into[at + 1] = (byte)(v >> 16);
        into[at + 2] = (byte)(v >> 8);
        into[at + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, (uint)data.Length);
        for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, head.AsSpan(4, 4)), data) ^ 0xFFFFFFFFu;
        var tail = new byte[4];
        BigEndian(tail, 0, crc);
        s.Write(tail);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var x in data) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
