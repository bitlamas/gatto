namespace Gatto.Core.Client;

//the elision frontier and the token ratio a request went out with, kept on its reply's record so a resume shapes its first request the same way
public sealed record ShapeMark(int ElidedThrough, double Ratio);
