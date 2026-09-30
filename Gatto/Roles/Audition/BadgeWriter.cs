using System.Text.Json;
using Gatto.Core.Acquire;

namespace Gatto.Roles.Audition;

//it takes an AuditionVerdict, so it sits in Roles and uses BadgeRegister's constants, and a failed measurement is still written
internal static class BadgeWriter
{
    //both keys come off the verdict's stamp, and the four-argument form stays for the wizard that holds the Hub row it just fetched
    public static DateOnly Write(string homePath, AuditionVerdict verdict) =>
        Write(homePath, verdict.Stamp.ModelFileName, verdict, verdict.Stamp.RepoId);

    //one record per model file, replaced on a re-audition, the repo id only for a Hub model, and the measured day handed back
    public static DateOnly Write(string homePath, string modelKey, AuditionVerdict verdict, string? repoId = null)
    {
        var dir = Path.Combine(homePath, BadgeRegister.DirectoryName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, BadgeRegister.FileNameFor(modelKey));

        var badge = verdict.Pass && !verdict.Disqualified;
        var measured = DateOnly.FromDateTime(DateTime.Now);

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", BadgeRegister.Schema);
            w.WriteString("model_key", modelKey);
            //repo_id is the Hub-side key written beside model_key, so a badge is found from either direction
            if (repoId is { Length: > 0 } repo) w.WriteString("repo_id", repo);

            //the verdict key goes out on both branches, a failure record says so rather than reading like a file written before the key
            w.WriteString("verdict", badge ? "pass" : "fail");
            w.WriteString("measured", measured.ToString("yyyy-MM-dd"));

            //badge fields are written only on a pass, a fail record with a build stamp would invite a reader to treat presence as proof
            if (badge)
            {
                w.WriteString("gatto_build", verdict.Stamp.GattoBuild);
                w.WriteString("sampling_note", verdict.Stamp.SamplingNote);
                //which llama-server produced this, written only when the server reported it, and the same GGUF can fail on mainline and pass on a fork
                if (verdict.Stamp.Server is { Length: > 0 } server) w.WriteString("server", server);
            }

            //evidence the reader ignores, the rate is absent when the run reported no timings rather than left out for compatibility
            w.WriteNumber("battery_version", Battery.Version);
            w.WriteNumber("wall_clock_s", Math.Round(verdict.WallClock.TotalSeconds, 1));
            if (verdict.DecodeTokS is { } rate) w.WriteNumber("decode_tok_s", Math.Round(rate, 1));
            //do not write model_file, it repeated model_key and no reader ever read it, old records with it are inert
            if (verdict.Stamp.Quant is { } q) w.WriteString("quant", q);
            w.WriteNumber("context", verdict.Stamp.Context);

            w.WriteStartArray("tasks");
            foreach (var t in verdict.Tasks)
            {
                w.WriteStartObject();
                w.WriteString("id", t.TaskId);
                //a skipped task writes skipped and nothing else, a pass false would record a failure for work the model was never asked to do
                if (t.Skipped)
                {
                    w.WriteBoolean("skipped", true);
                    w.WriteEndObject();
                    continue;
                }
                w.WriteBoolean("pass", t.Pass);
                w.WriteNumber("elapsed_s", Math.Round(t.Elapsed.TotalSeconds, 1));
                if (t.Shapes.Count > 0)
                {
                    w.WriteStartArray("shapes");
                    foreach (var s in t.Shapes) w.WriteStringValue(s.ToString());
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        //write beside then move over, a reader must never see a half-written record
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, buffer.ToArray());
        File.Move(tmp, path, overwrite: true);
        //the date comes back only after the move, a caller never holds it for a record that failed to write
        return measured;
    }
}
