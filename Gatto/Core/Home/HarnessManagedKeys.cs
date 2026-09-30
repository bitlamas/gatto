using System.Text.Json;

namespace Gatto.Core.Home;

//the five body fields the harness computes, so a load-time check refuses an override that sets one instead of dropping it silently
public static class HarnessManagedKeys
{
    public static readonly IReadOnlyCollection<string> Names =
        new HashSet<string>(StringComparer.Ordinal) { "model", "messages", "stream", "stream_options", "tools" };

    //throws the caller's message the moment a property collides with a harness-managed key, and obj must already be an object
    public static void Check(JsonElement obj, Func<string, string> messageFor)
    {
        foreach (var prop in obj.EnumerateObject())
            if (Names.Contains(prop.Name))
                throw new GattoConfigException(messageFor(prop.Name));
    }
}
