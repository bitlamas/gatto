using System.Text;
using System.Text.Json;

namespace Gatto.Core.Client;

//how one chat message becomes request JSON, a pure seam so prefix-stability checks can call it directly
public static class RequestJson
{
    //write one message into the in-flight request, conditional fields go in only when they apply and a reader must tolerate absence
    public static void WriteMessage(Utf8JsonWriter w, ChatMessage m,
        ImageCache? images = null, Action<string>? onUnavailable = null)
    {
        w.WriteStartObject();
        w.WriteString("role", m.Role);
        //no images keeps content a plain string and images become the parts array, so a missing cache must never drop them
        if (m.Images is { Count: > 0 } imgs)
            WriteContentParts(w, m.Content, imgs, images ?? new ImageCache(), onUnavailable);
        else if (m.Content is not null) w.WriteString("content", m.Content);
        //resend the assistant turn's reasoning, a model that conditions on prior thinking needs the channel again next turn
        if (m.ReasoningContent is not null) w.WriteString("reasoning_content", m.ReasoningContent);
        if (m.ToolCallId is not null) w.WriteString("tool_call_id", m.ToolCallId);
        if (m.ToolCalls is { Count: > 0 })
        {
            w.WriteStartArray("tool_calls");
            foreach (var tc in m.ToolCalls)
            {
                w.WriteStartObject();
                w.WriteString("id", tc.Id);
                w.WriteString("type", "function");
                w.WriteStartObject("function");
                w.WriteString("name", tc.Name);
                w.WriteString("arguments", tc.ArgumentsJson);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    //content as an OpenAI parts array, text first then one image_url per image, and a missing file becomes a text stub inside the array
    private static void WriteContentParts(Utf8JsonWriter w, string? text,
        IReadOnlyList<ImageRef> imgs, ImageCache cache, Action<string>? onUnavailable)
    {
        w.WriteStartArray("content");
        w.WriteStartObject();
        w.WriteString("type", "text");
        w.WriteString("text", text ?? "");
        w.WriteEndObject();
        for (var i = 0; i < imgs.Count; i++)
        {
            var img = imgs[i];
            var uri = cache.DataUri(img, onUnavailable);
            //the label sits immediately before its picture, and repeats the token the user's own message uses
            var label = $"image {i + 1} of {imgs.Count}: {img.Name}";
            w.WriteStartObject();
            if (uri is null)
            {
                //an unavailable image keeps its slot, dropping it would claim N images while the model counts N-1
                w.WriteString("type", "text");
                //the stub instructs, a bare "unavailable" left the model to guess which picture it had lost
                w.WriteString("text",
                    $"{label} — unavailable, not shown. It was attached earlier and has since been "
                    + "moved, deleted or changed. You cannot see it: do not describe it and do not "
                    + "confuse it with any other image here; ask the user to attach it again.");
                w.WriteEndObject();
            }
            else
            {
                w.WriteString("type", "text");
                w.WriteString("text", label);
                w.WriteEndObject();

                w.WriteStartObject();
                w.WriteString("type", "image_url");
                w.WriteStartObject("image_url");
                w.WriteString("url", uri);
                w.WriteEndObject();
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
    }

    //one tool as the request advertises it, the one writer the body and the baseline's hash share
    public static void WriteTool(Utf8JsonWriter w, ToolSpec t)
    {
        w.WriteStartObject();
        w.WriteString("type", "function");
        w.WriteStartObject("function");
        w.WriteString("name", t.Name);
        w.WriteString("description", t.Description);
        w.WritePropertyName("parameters");
        t.ParametersSchema.WriteTo(w);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    public static byte[] ToolBytes(ToolSpec t)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) WriteTool(w, t);
        return ms.ToArray();
    }

    //one message as a JSON string, for tests that check prefix stability without a client
    public static string SerialiseMessage(ChatMessage m,
        ImageCache? images = null, Action<string>? onUnavailable = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) WriteMessage(w, m, images, onUnavailable);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
