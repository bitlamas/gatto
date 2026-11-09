using System.Buffers.Binary;

namespace Gatto.Tests.Fakes;

//1x1 pictures in each format read_file accepts, whole files rather than magic bytes so a walk can hand one to a real server
public static class ImageFixtures
{
    public static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public static readonly byte[] Gif1x1 = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    public static readonly byte[] Jpeg1x1 = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");

    //a 24-bit bitmap of one pixel: the 14-byte file header, a 40-byte info header, one row padded to 4 bytes
    public static byte[] Bmp1x1()
    {
        var b = new byte[58];
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), 58);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(34), 4);
        b[54] = 0xFF;
        return b;
    }
}
