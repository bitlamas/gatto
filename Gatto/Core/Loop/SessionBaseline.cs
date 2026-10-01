using System.Security.Cryptography;
using System.Text;
using Gatto.Core.Client;

namespace Gatto.Core.Loop;

public sealed record ToolMark(string Name, string Sha256);
public sealed record ContextFileMark(string Path, string Sha256);
public sealed record PolicyMark(string Extension, string Line);
public sealed record ThinkingMark(string? Level, string? BodyJson);

//what the system message was composed from, the values a resume compares to say what changed
public sealed record BaselineSources(
    string? Date, string? Memory,
    IReadOnlyList<ContextFileMark> ContextFiles, IReadOnlyList<PolicyMark> Policy,
    string RoleAppendSha256, string ModelAppendSha256);

public sealed record SessionBaseline(
    int Seq, string Role, string Model, string ReasoningHistory, ThinkingMark Thinking,
    IReadOnlyList<ToolMark> Tools, BaselineSources Sources,
    string? Endpoint = null);   //the endpoint name the session ran on, null only in a record written before the field existed

//a composed system text and the baseline it came from, built only in full, since a dropped baseline makes the next resume reset
public sealed record ComposedSystem(string Text, SessionBaseline? Baseline);

//the note a resume carries to the model, with the values it brings the model up to
public sealed record SessionUpdate(string Text, BaselineSources Sources, int BaseSeq);

//a record compares a list member by reference, so the lists are compared here element by element and in order
public static class BaselineMarks
{
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    //each tool hashed from the bytes the request writes for it, so a changed description or schema changes the mark
    public static IReadOnlyList<ToolMark> ToolsOf(IReadOnlyList<ToolSpec> specs) =>
        specs.Select(s => new ToolMark(s.Name, Sha256Hex(RequestJson.ToolBytes(s)))).ToList();

    public static bool SameTools(IReadOnlyList<ToolMark> a, IReadOnlyList<ToolMark> b) => a.SequenceEqual(b);
}
