//a GIF or BMP goes out as a PNG of the same pixels, the same bytes every time
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class ImagePngTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-imgpng-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    [Fact]
    public void The_one_pixel_gif_fixture_becomes_a_transparent_pixel()
    {
        //the fixture's only pixel is colour 0, which its control block names transparent
        var (w, h, rgba, type) = ReadPng(ImagePng.Convert(ImageFixtures.Gif1x1, "image/gif"));
        Assert.Equal((1, 1, 6), (w, h, type));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, rgba);
    }

    [Fact]
    public void The_one_pixel_bmp_fixture_becomes_an_opaque_rgb_pixel()
    {
        var (w, h, rgba, type) = ReadPng(ImagePng.Convert(ImageFixtures.Bmp1x1(), "image/bmp"));
        Assert.Equal((1, 1, 2), (w, h, type));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba);
    }

    [Fact]
    public void A_gif_whose_table_fills_and_clears_decodes_every_pixel()
    {
        //noise fills and clears the table, the runs hit the code still being built
        var w = 160;
        var h = 120;
        var rng = new Random(7);
        var idx = new byte[w * h];
        for (var i = 0; i < idx.Length; i++) idx[i] = i % 97 < 40 ? (byte)(i / 13 % 256) : (byte)rng.Next(256);
        var palette = Palette(256, c => ((byte)c, (byte)(255 - c), (byte)(c * 7)));
        var gif = Gif(w, h, palette, idx, minCode: 8);

        var (pw, ph, rgba, type) = ReadPng(ImagePng.Convert(gif, "image/gif"));
        Assert.Equal((w, h, 2), (pw, ph, type));
        for (var i = 0; i < idx.Length; i++)
            Assert.Equal(palette.Skip(idx[i] * 3).Take(3).Append((byte)255), rgba.Skip(i * 4).Take(4));
    }

    [Fact]
    public void An_interlaced_gif_lands_each_stored_row_on_its_own_row()
    {
        var w = 3;
        var h = 11;
        var idx = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) idx[y * w + x] = (byte)(y % 4);
        var palette = Palette(4, c => ((byte)(c * 60), 0, 0));
        var order = new[] { 0, 8, 4, 2, 6, 10, 1, 3, 5, 7, 9 };
        var stored = order.SelectMany(y => idx.Skip(y * w).Take(w)).ToArray();
        var gif = Gif(w, h, palette, stored, minCode: 2, interlaced: true);

        var (_, _, rgba, _) = ReadPng(ImagePng.Convert(gif, "image/gif"));
        for (var y = 0; y < h; y++) Assert.Equal((byte)(y % 4 * 60), rgba[y * w * 4]);
    }

    [Fact]
    public void A_gif_frame_smaller_than_its_screen_is_drawn_at_its_offset_and_the_rest_is_transparent()
    {
        //the frame's local table wins over the global one
        var gif = Gif(2, 1, Palette(2, c => (9, 9, 9)), [1, 0], minCode: 2,
            screen: (4, 3), offset: (1, 2), local: Palette(2, c => c == 0 ? ((byte)10, (byte)20, (byte)30) : ((byte)40, (byte)50, (byte)60)));

        var (w, h, rgba, type) = ReadPng(ImagePng.Convert(gif, "image/gif"));
        Assert.Equal((4, 3, 6), (w, h, type));
        Assert.Equal(new byte[] { 40, 50, 60, 255, 10, 20, 30, 255 }, rgba.Skip((2 * 4 + 1) * 4).Take(8));
        Assert.Equal(0, rgba.Take((2 * 4 + 1) * 4).Count(b => b != 0));
    }

    [Fact]
    public void A_gif_cut_short_keeps_the_pixels_it_holds()
    {
        var idx = Enumerable.Range(0, 64).Select(i => (byte)(i % 3)).ToArray();
        var gif = Gif(8, 8, Palette(4, c => ((byte)(c + 1), 0, 0)), idx, minCode: 2);
        var cut = gif[..^6];

        var (_, _, rgba, _) = ReadPng(ImagePng.Convert(cut, "image/gif"));
        Assert.Equal(1, rgba[0]);
        Assert.Equal(0, rgba[^1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void A_palette_bmp_reads_its_indices_bottom_up(int bpp)
    {
        var w = 13;
        var h = 3;
        var colours = 1 << bpp;
        var idx = new int[w * h];
        for (var i = 0; i < idx.Length; i++) idx[i] = (i * 5 + 1) % colours;
        var palette = Enumerable.Range(0, colours).Select(c => ((byte)c, (byte)(c * 3), (byte)(255 - c))).ToArray();
        var bmp = Bmp(w, h, bpp, 0, palette, (x, y) => (uint)idx[y * w + x]);

        var (_, _, rgba, _) = ReadPng(ImagePng.Convert(bmp, "image/bmp"));
        for (var i = 0; i < idx.Length; i++)
        {
            var (r, g, b) = palette[idx[i]];
            Assert.Equal(new byte[] { r, g, b, 255 }, rgba.Skip(i * 4).Take(4));
        }
    }

    [Fact]
    public void A_top_down_bmp_keeps_its_first_row_on_top()
    {
        var bmp = Bmp(2, 2, 24, 0, null, (x, y) => y == 0 ? 0xFF0000u : 0x00FF00u, topDown: true);
        var (_, _, rgba, _) = ReadPng(ImagePng.Convert(bmp, "image/bmp"));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba.Take(4));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, rgba.Skip(8).Take(4));
    }

    [Fact]
    public void A_32_bit_bmp_without_bit_fields_ignores_its_fourth_byte()
    {
        var bmp = Bmp(1, 1, 32, 0, null, (_, _) => 0x00112233u);
        var (_, _, rgba, type) = ReadPng(ImagePng.Convert(bmp, "image/bmp"));
        Assert.Equal(2, type);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 255 }, rgba);
    }

    [Fact]
    public void A_v5_bmp_with_an_alpha_mask_keeps_its_alpha()
    {
        var bmp = Bmp(2, 1, 32, 3, null, (x, _) => x == 0 ? 0x80FF0000u : 0xFF00FF00u, header: 124,
            masks: (0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u));
        var (_, _, rgba, type) = ReadPng(ImagePng.Convert(bmp, "image/bmp"));
        Assert.Equal(6, type);
        Assert.Equal(new byte[] { 255, 0, 0, 0x80, 0, 255, 0, 255 }, rgba);
    }

    [Fact]
    public void An_alpha_mask_of_zeros_everywhere_reads_as_opaque()
    {
        var bmp = Bmp(1, 1, 32, 3, null, (_, _) => 0x00FF0000u, header: 124,
            masks: (0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u));
        var (_, _, rgba, type) = ReadPng(ImagePng.Convert(bmp, "image/bmp"));
        Assert.Equal(2, type);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba);
    }

    [Fact]
    public void A_565_bmp_scales_each_field_to_eight_bits()
    {
        var bmp = Bmp(1, 1, 16, 3, null, (_, _) => 0xF81Fu, masks: (0xF800u, 0x07E0u, 0x001Fu, 0u));
        var (_, _, rgba, _) = ReadPng(ImagePng.Convert(bmp, "image/bmp"));
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, rgba);
    }

    [Theory]
    [InlineData(3u, 24)]
    [InlineData(17u, 140)]
    [InlineData(1u, 8)]
    public void A_5_bit_channel_widens_by_repeating_its_bits(uint red, int expected)
    {
        //a scaled rounding would read 3 as 25, one level off the pixels a local server decoded from the same file
        var bmp = Bmp(1, 1, 16, 0, null, (_, _) => red << 10);
        var (_, _, rgba, _) = ReadPng(ImagePng.Convert(bmp, "image/bmp"));
        Assert.Equal(expected, rgba[0]);
    }

    [Fact]
    public void A_16_bit_bmp_without_bit_fields_reads_as_555()
    {
        var bmp = Bmp(1, 1, 16, 0, null, (_, _) => 0x03E0u);
        var (_, _, rgba, _) = ReadPng(ImagePng.Convert(bmp, "image/bmp"));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, rgba);
    }

    [Fact]
    public void An_rle8_bmp_reads_runs_absolute_runs_and_deltas_and_leaves_skipped_pixels_transparent()
    {
        //a run, an absolute run, an end of line, a delta past row 1, one pixel
        byte[] runs = [3, 1, 0, 3, 2, 3, 0, 0, 0, 0, 0, 2, 0, 1, 1, 2, 0, 1];
        var palette = new[] { ((byte)0, (byte)0, (byte)0), ((byte)255, (byte)0, (byte)0), ((byte)0, (byte)255, (byte)0), ((byte)0, (byte)0, (byte)255) };
        var bmp = BmpRaw(6, 3, 8, 1, palette, runs);

        var (_, _, rgba, type) = ReadPng(ImagePng.Convert(bmp, "image/bmp"));
        Assert.Equal(6, type);
        var bottom = rgba.Skip(2 * 6 * 4).ToArray();
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 255 }, bottom);
        Assert.Equal(0, rgba.Skip(6 * 4).Take(6 * 4).Count(b => b != 0));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, rgba.Take(4));
    }

    [Fact]
    public void An_rle4_bmp_alternates_the_two_nibbles()
    {
        byte[] runs = [5, 0x12, 0, 3, 0x30, 0x10, 0, 1];
        var palette = Enumerable.Range(0, 16).Select(c => ((byte)(c * 16), (byte)0, (byte)0)).ToArray();
        var bmp = BmpRaw(8, 1, 4, 2, palette, runs);

        var (_, _, rgba, _) = ReadPng(ImagePng.Convert(bmp, "image/bmp"));
        Assert.Equal(new byte[] { 16, 32, 16, 32, 16, 48, 0, 16 }, Enumerable.Range(0, 8).Select(i => rgba[i * 4]));
    }

    [Theory]
    [InlineData("GIF89a")]
    [InlineData("GIF89a\x01\x00\x01\x00\x00\x00\x00;")]
    public void A_gif_that_cannot_be_read_is_refused_at_attach_with_the_reason(string text)
    {
        var p = Write("broken.gif", System.Text.Encoding.Latin1.GetBytes(text));
        var e = Assert.Throws<InvalidOperationException>(() => ImageRef.FromFile(p));
        Assert.StartsWith("broken.gif cannot be read: the GIF ", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bmp_with_an_embedded_png_is_refused_by_read_file_with_the_reason()
    {
        Write("odd.bmp", BmpRaw(1, 1, 24, 5, null, new byte[16]));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => new ReadFileTool().ExecuteAsync(
            JsonDocument.Parse("""{"path":"odd.bmp"}""").RootElement, new TestToolContext(_dir), default));
        Assert.Equal("odd.bmp cannot be read: the BMP holds an embedded JPEG or PNG", e.Message);
    }

    [Fact]
    public void A_bitmap_past_the_pixel_ceiling_is_refused_before_a_byte_is_allocated()
    {
        var bmp = BmpRaw(10_000, 10_000, 1, 0, [(0, 0, 0), (255, 255, 255)], new byte[4]);
        var e = Assert.Throws<InvalidDataException>(() => ImagePng.Decode(bmp, "image/bmp"));
        Assert.Contains("10000x10000", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gif_goes_out_as_png_while_the_reference_keeps_the_file_as_attached()
    {
        var path = Write("anim.gif", ImageFixtures.Gif1x1);
        var img = ImageRef.FromFile(path);
        var m = new ChatMessage("user", "look", Images: [img]);

        using var doc = JsonDocument.Parse(RequestJson.SerialiseMessage(m, new ImageCache()));
        var url = doc.RootElement.GetProperty("content")[2].GetProperty("image_url").GetProperty("url").GetString()!;

        Assert.StartsWith("data:image/png;base64,", url, StringComparison.Ordinal);
        Assert.Equal(ImagePng.Convert(ImageFixtures.Gif1x1, "image/gif"), Convert.FromBase64String(url["data:image/png;base64,".Length..]));
        Assert.Equal(("image/gif", ImageRef.ShaOf(ImageFixtures.Gif1x1)), (img.MediaType, img.Sha256));
    }

    [Fact]
    public void A_bmp_serialises_to_the_same_bytes_in_a_fresh_cache()
    {
        //a resumed session builds a new cache, so the conversion must repeat byte for byte
        var w = 37;
        var h = 21;
        var bmp = Bmp(w, h, 24, 0, null, (x, y) => (uint)(x * 7 + y * 1031));
        var m = new ChatMessage("user", "look", Images: [ImageRef.FromFile(Write("scan.bmp", bmp))]);

        Assert.Equal(RequestJson.SerialiseMessage(m, new ImageCache()), RequestJson.SerialiseMessage(m, new ImageCache()));
    }

    [Fact]
    public void A_png_and_a_jpeg_go_out_as_they_are()
    {
        var png = ImageRef.FromFile(Write("a.png", ImageFixtures.Png1x1));
        var jpg = ImageRef.FromFile(Write("b.jpg", ImageFixtures.Jpeg1x1));
        var cache = new ImageCache();
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(ImageFixtures.Png1x1), cache.DataUri(png));
        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(ImageFixtures.Jpeg1x1), cache.DataUri(jpg));
    }

    [Fact]
    public void A_restored_gif_that_cannot_be_read_becomes_the_unavailable_stub_and_says_why_once()
    {
        //a session restores its references without the attach check, so the wire is the last place a bad file is caught
        var path = Write("bad.gif", "GIF89a"u8.ToArray());
        var img = new ImageRef(path, 6, ImageRef.ShaOf("GIF89a"u8), "image/gif");
        var said = new List<string>();
        var cache = new ImageCache();

        Assert.Null(cache.DataUri(img, said.Add));
        Assert.Null(cache.DataUri(img, said.Add));
        Assert.Equal("bad.gif cannot be read (the GIF header is missing), the model cannot see it", Assert.Single(said));
    }

    private static byte[] Palette(int count, Func<int, (byte R, byte G, byte B)> colour) =>
        [.. Enumerable.Range(0, count).SelectMany(c => { var (r, g, b) = colour(c); return new[] { r, g, b }; })];

    //a GIF89a with one frame, its indices written by a plain LZW encoder that clears when the table fills
    private static byte[] Gif(int w, int h, byte[] global, byte[] indices, int minCode, bool interlaced = false,
        (int W, int H)? screen = null, (int X, int Y)? offset = null, byte[]? local = null)
    {
        var o = new List<byte>();
        o.AddRange("GIF89a"u8.ToArray());
        var (sw, sh) = screen ?? (w, h);
        o.AddRange([(byte)sw, (byte)(sw >> 8), (byte)sh, (byte)(sh >> 8), (byte)(0x80 | TableBits(global)), 0, 0]);
        o.AddRange(global);
        var (ox, oy) = offset ?? (0, 0);
        o.AddRange([0x2C, (byte)ox, (byte)(ox >> 8), (byte)oy, (byte)(oy >> 8), (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8)]);
        o.Add((byte)((local is null ? 0 : 0x80 | TableBits(local)) | (interlaced ? 0x40 : 0)));
        if (local is not null) o.AddRange(local);
        o.Add((byte)minCode);
        var data = Lzw(indices, minCode);
        for (var i = 0; i < data.Length; i += 255)
        {
            var n = Math.Min(255, data.Length - i);
            o.Add((byte)n);
            o.AddRange(data.AsSpan(i, n).ToArray());
        }
        o.Add(0);
        o.Add(0x3B);
        return [.. o];
    }

    private static int TableBits(byte[] table) => (int)Math.Log2(table.Length / 3) - 1;

    private static byte[] Lzw(byte[] indices, int minCode)
    {
        var clear = 1 << minCode;
        var size = minCode + 1;
        var next = clear + 2;
        var table = new Dictionary<(int, byte), int>();
        var output = new List<byte>();
        long buffer = 0;
        var bits = 0;
        void Emit(int code)
        {
            buffer |= (long)code << bits;
            bits += size;
            while (bits >= 8) { output.Add((byte)buffer); buffer >>= 8; bits -= 8; }
        }
        Emit(clear);
        int prefix = indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            if (table.TryGetValue((prefix, indices[i]), out var code)) { prefix = code; continue; }
            Emit(prefix);
            table[(prefix, indices[i])] = next++;
            if (next > 1 << size && size < 12) size++;
            if (next == 4096)
            {
                Emit(clear);
                table.Clear();
                size = minCode + 1;
                next = clear + 2;
            }
            prefix = indices[i];
        }
        Emit(prefix);
        next++;
        if (next > 1 << size && size < 12) size++;
        Emit(clear + 1);
        if (bits > 0) output.Add((byte)buffer);
        return [.. output];
    }

    //an uncompressed bitmap, each pixel's value packed at the depth given, rows bottom-up unless topDown
    private static byte[] Bmp(int w, int h, int bpp, uint compression, (byte R, byte G, byte B)[]? palette,
        Func<int, int, uint> pixel, bool topDown = false, int header = 40, (uint R, uint G, uint B, uint A)? masks = null)
    {
        var stride = (bpp * w + 31) / 32 * 4;
        var rows = new byte[stride * h];
        for (var r = 0; r < h; r++)
        {
            var y = topDown ? r : h - 1 - r;
            for (var x = 0; x < w; x++)
            {
                var v = pixel(x, y);
                var at = r * stride;
                switch (bpp)
                {
                    case 1 or 4 or 8:
                        var bit = x * bpp;
                        rows[at + bit / 8] |= (byte)(v << (8 - bpp - bit % 8));
                        break;
                    case 16:
                        BinaryPrimitives.WriteUInt16LittleEndian(rows.AsSpan(at + x * 2), (ushort)v);
                        break;
                    case 24:
                        rows[at + x * 3] = (byte)v; rows[at + x * 3 + 1] = (byte)(v >> 8); rows[at + x * 3 + 2] = (byte)(v >> 16);
                        break;
                    default:
                        BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(at + x * 4), v);
                        break;
                }
            }
        }
        return BmpRaw(w, topDown ? -h : h, bpp, compression, palette, rows, header, masks);
    }

    private static byte[] BmpRaw(int w, int h, int bpp, uint compression, (byte R, byte G, byte B)[]? palette, byte[] pixels,
        int header = 40, (uint R, uint G, uint B, uint A)? masks = null)
    {
        var maskBytes = header == 40 && masks is not null ? 12 : 0;
        var paletteBytes = (palette?.Length ?? 0) * 4;
        var dataAt = 14 + header + maskBytes + paletteBytes;
        var b = new byte[dataAt + pixels.Length];
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), (uint)b.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), (uint)dataAt);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(14), (uint)header);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), h);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), (ushort)bpp);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(30), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(46), (uint)(palette?.Length ?? 0));
        if (masks is { } m)
        {
            var at = 54;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), m.R);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), m.G);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), m.B);
            if (header >= 56) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 12), m.A);
        }
        for (var i = 0; i < (palette?.Length ?? 0); i++)
        {
            var at = 14 + header + maskBytes + i * 4;
            b[at] = palette![i].B;
            b[at + 1] = palette[i].G;
            b[at + 2] = palette[i].R;
        }
        pixels.CopyTo(b, dataAt);
        return b;
    }

    //a PNG read back with a CRC loop of its own, so a chunk the encoder checksummed wrong fails here
    private static (int W, int H, byte[] Rgba, int ColourType) ReadPng(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8));
        var pos = 8;
        int w = 0, h = 0, type = 0;
        var idat = new MemoryStream();
        var ended = false;
        while (!ended)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            var name = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            Assert.Equal(BitCrc(png.AsSpan(pos + 4, len + 4)), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len)));
            switch (name)
            {
                case "IHDR":
                    w = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    h = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    Assert.Equal(8, data[8]);
                    type = data[9];
                    Assert.Equal(new byte[] { 0, 0, 0 }, data[10..13].ToArray());
                    break;
                case "IDAT": idat.Write(data); break;
                case "IEND": ended = true; break;
            }
            pos += 12 + len;
        }
        Assert.Equal(png.Length, pos);
        var channels = type == 6 ? 4 : 3;
        var rowLen = w * channels;
        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        var bytes = raw.ToArray();
        Assert.Equal((rowLen + 1) * h, bytes.Length);
        var prior = new byte[rowLen];
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var f = bytes[y * (rowLen + 1)];
            var row = new byte[rowLen];
            for (var i = 0; i < rowLen; i++)
            {
                int a = i >= channels ? row[i - channels] : 0, b = prior[i], c = i >= channels ? prior[i - channels] : 0;
                var p = a + b - c;
                var pred = f switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => Math.Abs(p - a) <= Math.Abs(p - b) && Math.Abs(p - a) <= Math.Abs(p - c) ? a : Math.Abs(p - b) <= Math.Abs(p - c) ? b : c,
                    _ => throw new InvalidDataException($"filter {f}"),
                };
                row[i] = (byte)(bytes[y * (rowLen + 1) + 1 + i] + pred);
            }
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < channels; c++) rgba[(y * w + x) * 4 + c] = row[x * channels + c];
                if (channels == 3) rgba[(y * w + x) * 4 + 3] = 255;
            }
            prior = row;
        }
        return (w, h, rgba, type);
    }

    private static uint BitCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var x in data)
        {
            crc ^= x;
            for (var k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
