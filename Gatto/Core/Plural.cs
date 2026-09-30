namespace Gatto.Core;

//one home for a count and its noun. only 1 tells the rule apart, so a test must use it, and an irregular noun passes its own plural
internal static class Plural   //a caller that rounds passes the rounded number the reader sees
{
    //the count and its noun, 1 line or 2 lines
    public static string Of(long n, string singular, string? plural = null)
        => $"{n} {Noun(n, singular, plural)}";

    //just the noun, for a caller that composes the number itself (a formatted size, a number already in a wider sentence)
    public static string Noun(long n, string singular, string? plural = null)
        => n == 1 ? singular : plural ?? singular + "s";
}
