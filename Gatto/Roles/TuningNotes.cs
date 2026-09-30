using System.Text;

namespace Gatto.Roles;

//the notes sit next to profile.json by convention, the profile must not reference the file (Model.Load throws on unknown keys)
internal static class TuningNotes
{
    public const string FileName = "tuning.txt";

    //keep every character of this string ASCII (type and Get-Content read a BOM-less file as ANSI, em-dashes come out as garbage)
    public static string Compose(ModelProfile p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("tuning notes for this model");
        sb.AppendLine("==========================");
        sb.AppendLine();
        sb.AppendLine("gatto picked these defaults. They are a starting point, not a verdict - this");
        sb.AppendLine("file exists so you can see what is adjustable and which way to push it.");
        sb.AppendLine("Edit profile.json (this file is only notes; nothing reads it).");
        sb.AppendLine();

        sb.AppendLine($"1. GPU offload            gpu_layers = {p.GpuLayers?.ToString() ?? "(unset)"}");
        sb.AppendLine("   How many transformer layers run on the GPU. 99 means \"all of them\"; llama.cpp");
        sb.AppendLine("   clamps to the real count. PUSH DOWN if the server dies on load or the machine");
        sb.AppendLine("   swaps; there is nothing to push up past all-of-them. You will know it helped");
        sb.AppendLine("   when the model loads at all, or when tokens/sec stops collapsing mid-reply.");
        sb.AppendLine();

        sb.AppendLine($"2. Flash attention        {(p.ExtraArgs.Contains("-fa") ? "-fa on (in extra_args)" : "(not set)")}");
        sb.AppendLine("   A faster attention kernel with a smaller memory footprint at long context.");
        sb.AppendLine("   PUSH: leave it on. Turn it OFF only if the backend refuses to start with it,");
        sb.AppendLine("   which some older builds do. You will know it helped by prompt-processing speed");
        sb.AppendLine("   at long context, not by short-prompt latency.");
        sb.AppendLine();

        sb.AppendLine($"3. KV cache quantization  cache_type_k = {p.CacheTypeK ?? "(default f16)"}"
                      + $", cache_type_v = {p.CacheTypeV ?? "(default f16)"}");
        sb.AppendLine("   The precision of the attention cache, which is what actually grows with context.");
        sb.AppendLine("   q8_0 costs about half of f16 for a quality difference most people cannot see.");
        sb.AppendLine("   PUSH TO q8_0 if you want more context in the same memory. Keep f16 if you see");
        sb.AppendLine("   quality drift on long conversations. NOTE: some architectures (MLA-family) are");
        sb.AppendLine("   unhappy with a quantized cache - if generation degrades, put this back first.");
        sb.AppendLine();

        sb.AppendLine("4. Batch sizes            -b / -ub (not set; llama.cpp defaults apply)");
        sb.AppendLine("   How much prompt is processed per pass. Larger batches process a long prompt");
        sb.AppendLine("   faster but need more scratch memory during that pass. PUSH UP if prompt");
        sb.AppendLine("   processing is slow and you have memory headroom; PUSH DOWN if load succeeds");
        sb.AppendLine("   but a long first prompt runs the machine out of memory. You will know it");
        sb.AppendLine("   helped from time-to-first-token on a LONG prompt.");
        sb.AppendLine();

        sb.AppendLine("5. MoE expert placement   --n-cpu-moe (not set; only relevant to MoE models)");
        sb.AppendLine("   For mixture-of-experts models, keeps some expert tensors on the CPU so the");
        sb.AppendLine("   attention path keeps the GPU. PUSH UP (more experts to CPU) when the model");
        sb.AppendLine("   almost fits and you would otherwise drop GPU layers - experts are touched less");
        sb.AppendLine("   than attention, so this usually costs less speed than offloading layers does.");
        sb.AppendLine("   Irrelevant for a dense model. You will know it helped if the model fits with");
        sb.AppendLine("   gpu_layers still high.");
        sb.AppendLine();

        sb.AppendLine($"context = {p.Context}");
        sb.AppendLine("   Computed to fit THIS machine, not copied from the model's maximum. Raising it");
        sb.AppendLine("   past what the memory supports is how a load silently starts failing later.");
        return sb.ToString();
    }
}
