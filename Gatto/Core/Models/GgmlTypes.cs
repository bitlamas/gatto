namespace Gatto.Core.Models;

//block size and bytes per block, since a size is elements / blockSize * typeSize. an unknown id yields nothing, since a guess would call a new format damaged
internal static class GgmlTypes
{
    //block size and bytes per block, indexed by ggml type id, and null is a type this table does not know
    private static readonly (int Block, int Bytes)?[] Table =
    [
        (1, 4),        //0  F32
        (1, 2),        //1  F16
        (32, 18),      //2  Q4_0
        (32, 20),      //3  Q4_1
        null,          //4  Q4_2, retired
        null,          //5  Q4_3, retired
        (32, 22),      //6  Q5_0
        (32, 24),      //7  Q5_1
        (32, 34),      //8 Q8_0
        (32, 36),      //9 Q8_1
        (256, 84),     //10 Q2_K
        (256, 110),    //11 Q3_K
        (256, 144),    //12 Q4_K
        (256, 176),    //13 Q5_K
        (256, 210),    //14 Q6_K
        (256, 292),    //15 Q8_K
        (256, 66),     //16 IQ2_XXS
        (256, 74),     //17 IQ2_XS
        (256, 98),     //18 IQ3_XXS
        (256, 50),     //19 IQ1_S
        (32, 18),      //20 IQ4_NL
        (256, 110),    //21 IQ3_S
        (256, 82),     //22 IQ2_S
        (256, 136),    //23 IQ4_XS
        (1, 1),        //24 I8
        (1, 2),        //25 I16
        (1, 4),        //26 I32
        (1, 8),        //27 I64
        (1, 8),        //28 F64
        (256, 56),     //29 IQ1_M
        (1, 2),        //30 BF16
        null,          //31 Q4_0_4_4, retired
        null,          //32 Q4_0_4_8, retired
        null,          //33 Q4_0_8_8, retired
        (256, 54),     //34 TQ1_0
        (256, 66),     //35 TQ2_0
    ];

    //bytes on disk for a tensor, or null when the answer can't be stated (an overflow must not wrap into a plausible size)
    public static long? Bytes(uint type, long elements)
    {
        if (type >= Table.Length || Table[type] is not { } t) return null;
        if (elements < 0 || elements % t.Block != 0) return null;

        try { return checked(elements / t.Block * t.Bytes); }
        catch (OverflowException) { return null; }
    }
}
