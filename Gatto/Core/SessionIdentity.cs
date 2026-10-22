using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Gatto.Core;

//which session a transcript and a shell child belong to: the origin transcript's name projected to a GUID, when the session began, and the name /name gave it
public sealed record SessionIdentity(Guid Id, DateTimeOffset Start, string? Name = null)
{
    //the variables every shell child reads, the name only when the session has one
    public const string IdVariable = "GATTO_SESSION_ID";
    public const string StartVariable = "GATTO_SESSION_START";
    public const string NameVariable = "GATTO_SESSION_NAME";

    //the MD5 of the file name read as one GUID, little-endian as Guid(byte[]) reads it, the projection every gatto seat's journal entry used
    public static Guid Project(string fileName) => new(MD5.HashData(Encoding.UTF8.GetBytes(fileName)));

    public string IdText => Id.ToString("D");

    //local time with its offset to the second, the format of the journal and the letters, one formatter for the transcript and the child
    public string StartText => Start.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    //the fields as a transcript holds them, null when the id or the start is missing or unreadable. a record with no name is a session never named
    public static SessionIdentity? Parse(string? id, string? start, string? name = null) =>
        Guid.TryParseExact(id, "D", out var g)
        && DateTimeOffset.TryParseExact(start, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at)
            ? new SessionIdentity(g, at, string.IsNullOrEmpty(name) ? null : name) : null;
}
