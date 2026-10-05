namespace Gatto.Core.Models;

//the name a screen shows for the model string a request carries. the string itself is never rewritten, a server may match it exactly
internal static class ServedModelName
{
    //a llama-server started without an alias answers to its gguf path, shown as the file name alone. any other string, a cloud id with a slash included, shows as it is
    public static string Display(string modelString) =>
        modelString.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(modelString)
            : modelString;
}
