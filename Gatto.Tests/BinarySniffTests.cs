using System.Text;
using Gatto.Core.Client;

namespace Gatto.Tests;

//a BM head is a bitmap only when its DIB header size is one a real bitmap writes, two letters alone name a text file wrong
public class BinarySniffTests
{
    //the 14-byte file header, then the DIB header size and the rest of a 40-byte BITMAPINFOHEADER
    private static byte[] Bmp(uint dibSize)
    {
        var b = new byte[14 + 40];
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(14), dibSize);
        return b;
    }

    [Theory]
    [InlineData(12u)]
    [InlineData(40u)]
    [InlineData(52u)]
    [InlineData(56u)]
    [InlineData(108u)]
    [InlineData(124u)]
    public void A_bm_head_with_a_real_dib_header_size_is_a_bmp(uint dibSize) =>
        Assert.Equal("BMP image", BinarySniff.Describe(Bmp(dibSize)));

    [Fact]
    public void A_text_file_opening_with_bm_is_text() =>
        Assert.Null(BinarySniff.Describe(Encoding.ASCII.GetBytes("BMI notes, the weekly readings and the target for the month\n")));

    [Fact]
    public void A_bm_head_with_a_wrong_dib_size_and_a_nul_byte_is_binary()
    {
        var b = Bmp(7);
        Assert.Equal("binary", BinarySniff.Describe(b));
    }

    [Fact]
    public void Two_bytes_bm_alone_is_text() =>
        Assert.Null(BinarySniff.Describe("BM"u8));
}
