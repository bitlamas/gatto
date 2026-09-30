using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core.Client;
using Gatto.Core.Home;

namespace Gatto.Roles;

//the formatter behind serve status --json. offload, fallback or OOM fields stay out (the probe reports none of them)
public static class ServeStatusJson
{
    //the quant rule lives in QuantToken, Core can't reach Roles and a second copy drifts

    //build the one JSON object serve status --json prints. null loaded means the probe got nothing, null matchesModel means it couldn't be worked out
    public static string Build(LoadedModel? loaded, string? modelId, bool? matchesModel)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();

            //a server that answers without naming its weights writes null, same shape as no server answering
            if (loaded?.ModelPath is { Length: > 0 } path) w.WriteString("model_path", path);
            else w.WriteNull("model_path");

            if (loaded?.NCtx is int nCtx) w.WriteNumber("n_ctx", nCtx);
            else w.WriteNull("n_ctx");

            if (modelId is not null) w.WriteString("model_id", modelId);
            else w.WriteNull("model_id");

            if (matchesModel is bool m) w.WriteBoolean("matches_model", m);
            else w.WriteNull("matches_model");

            //omit quant when it doesn't parse, these other fields always appear, with null
            var quant = loaded?.ModelPath is { Length: > 0 } qp
                ? Gatto.Core.Acquire.QuantToken.Of(qp) : null;
            if (quant is not null) w.WriteString("quant", quant);

            w.WriteNumber("schema_version", SessionStore.SchemaVersion);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
