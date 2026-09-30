namespace Gatto.Cli.Setup;

//the machine's names minus whitespace and trademark marks, since no case change is safe. clean the pair together, the processor rule reads the graphics name
internal static class HardwareNaming
{
    //the two names ready for the screen, or null where nothing was read. a name that cleans away to nothing comes back null, so the screen's fallback renders
    public static HardwareNames Clean(string? cpuRaw, string? gpuRaw)
    {
        var gpu = Tidy(gpuRaw);
        return new HardwareNames(WithoutTheCompanionGpu(Tidy(cpuRaw), gpu), gpu);
    }

    //outer whitespace and trademark marks, and every token survives, so the words in a name never change
    private static string? Tidy(string? raw)
    {
        if (raw is null) return null;

        var s = raw;
        foreach (var mark in Marks) s = s.Replace(mark, " ", StringComparison.OrdinalIgnoreCase);

        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 0 ? s : null;
    }

    //both spellings of each mark, since the registry writes whichever it likes
    private static readonly string[] Marks = ["(TM)", "(R)", "(C)", "™", "®", "©"];

    //cut the processor's companion clause only when the graphics name already says every word of it. no graphics name means no cut
    private static string? WithoutTheCompanionGpu(string? cpu, string? gpu)
    {
        if (cpu is null || gpu is null) return cpu;

        foreach (var sep in Connectives)
        {
            var at = cpu.LastIndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (at <= 0) continue;

            var tail = cpu[(at + sep.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tail.Length == 0) continue;
            if (!tail.All(word => gpu.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;

            return cpu[..at];
        }

        return cpu;
    }

    private static readonly string[] Connectives = [" w/ ", " with "];
}
