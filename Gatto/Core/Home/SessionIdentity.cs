using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Gatto.Core.Home;

//which session a transcript and a shell child belong to: the origin transcript's name projected to a GUID, and when the session began
public sealed record SessionIdentity(Guid Id, DateTimeOffset Start)
{
    //the variables every shell child reads
    public const string IdVariable = "GATTO_SESSION_ID";
    public const string StartVariable = "GATTO_SESSION_START";

    //the MD5 of the file name read as one GUID, little-endian as Guid(byte[]) reads it, the projection every gatto seat's journal entry used
    public static Guid Project(string fileName) => new(MD5.HashData(Encoding.UTF8.GetBytes(fileName)));

    public string IdText => Id.ToString("D");

    //local time with its offset to the second, the format of the journal and the letters, one formatter for the transcript and the child
    public string StartText => Start.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    //the two fields as a transcript holds them, null when either is missing or unreadable
    public static SessionIdentity? Parse(string? id, string? start) =>
        Guid.TryParseExact(id, "D", out var g)
        && DateTimeOffset.TryParseExact(start, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at)
            ? new SessionIdentity(g, at) : null;
}
