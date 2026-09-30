namespace Gatto.Core.Client;

//the server answered, so the status is a typed fact and no screen reads it back out of the message text
public class GattoHttpStatusException(string message, int statusCode, string body)
    : GattoConnectionException(message)
{
    //the status the server replied with
    public int StatusCode { get; } = statusCode;

    //the body as received and untruncated, the server's own words are usually the actionable half
    public string Body { get; } = body;
}
