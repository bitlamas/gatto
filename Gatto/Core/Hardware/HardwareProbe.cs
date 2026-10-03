namespace Gatto.Core.Hardware;

//the largest heap prices the budget, the heap sum feeds the carve-out test, other cards' heaps add to a discrete one
internal sealed record HardwareSnapshot(ulong? InstalledBytes, ulong OsVisibleBytes, GpuKind GraphicsKind,
    ulong? GraphicsMemoryBytes, string? GraphicsVendorId = null, ulong? GraphicsLocalTotalBytes = null,
    IReadOnlyList<ulong>? MoreDiscreteHeaps = null);

//the names stay off the snapshot so the classifier can't read one, the last is the integrated device to pin
internal sealed record ProbeReading(HardwareSnapshot? Snapshot, string? CpuName, string? GpuName, string? ServeOnlyGpu = null);

//a null Snapshot means the probe could not read os-visible memory or ran past the deadline, and Detail says which
internal sealed record ProbeOutcome(HardwareSnapshot? Snapshot, string Detail,
    string? CpuName = null, string? GpuName = null, string? ServeOnlyGpu = null);

internal static class HardwareProbe
{
    //the child's word, hidden from Help and reserved so a role file can't take it and make the wizard run a role
    internal const string ChildWord = "probe-machine";

    //loader layers disabled so an injected overlay can't hang the windowless child, and the read is UTF-8 so the cpu name isn't mangled
    internal static System.Diagnostics.ProcessStartInfo ChildStartInfo(string exePath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add(ChildWord);
        psi.Environment["VK_LOADER_LAYERS_DISABLE"] = "~all~";
        psi.Environment["VK_INSTANCE_LAYERS"] = "";
        return psi;
    }

    //wizard-time only, so nothing runs the probe at startup, and a failure is a null Snapshot with a Detail string
    public static ProbeOutcome Run(TimeSpan deadline)
    {
        //a null ProcessPath means gatto can't name its own image, so the CPU-only shape would be a claim about the machine
        if (Environment.ProcessPath is not { } exePath)
            return new(null, "gatto's own path is unknown, so the hardware child could not be started");

        using var p = new System.Diagnostics.Process { StartInfo = ChildStartInfo(exePath) };
        try { p.Start(); }
        catch (Exception ex) { return new(null, $"could not start the hardware child: {ex.Message}"); }

        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)deadline.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { }   //deadline is the bound, so kill the whole tree
            return new(null, $"hardware probe did not answer within {deadline.TotalSeconds:0}s");
        }
        //a grandchild that inherited the pipe write handles can keep EOF open forever, so wait a bounded grace
        Task.WaitAll(new Task[] { so, se }, 2000);
        var stdout = so.IsCompletedSuccessfully ? so.Result : "";
        var stderr = se.IsCompletedSuccessfully ? se.Result.Trim() : "";
        var reading = ParseReport(stdout);
        //a failed parse reports no names either, even when the cpu line arrived, so no chip name sits above missing figures
        return reading.Snapshot is null
            ? new(null, "hardware probe produced no usable report"
                + (stderr.Length > 0 ? $" — stderr: {stderr}" : ""))
            : new(reading.Snapshot, "ok", reading.CpuName, reading.GpuName, reading.ServeOnlyGpu);
    }

    //one device's lines: the vulkan type ordinal, the largest device-local heap and the sum of them
    private sealed record VkDevice(int Index, string? Name, uint TypeCode, string? Vendor, ulong LargestLocalHeap,
        ulong LocalHeapSum);

    //one pass picks the device and produces the snapshot and the names together, so the selection rule has no second copy
    public static ProbeReading ParseReport(string text)
    {
        ulong? installed = null, visible = null;
        string? cpuName = null;
        var vulkanOk = false;
        var devices = new Dictionary<int, VkDevice>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var rest = line[(eq + 1)..];
            switch (key)
            {
                case "installed": if (ulong.TryParse(rest, out var iv)) installed = iv; break;
                case "visible": if (ulong.TryParse(rest, out var vv)) visible = vv; break;
                case "cpu": if (rest.Trim() is { Length: > 0 } cn) cpuName = cn; break;
                //a failure clears every device (the child prints its status before listing them), and both spellings of the failure line close the gate
                case "vulkan": vulkanOk = rest.StartsWith("ok", StringComparison.Ordinal); break;
                case "error_vulkan": vulkanOk = false; break;
                default:
                    if (key.StartsWith("device_", StringComparison.Ordinal))
                        ParseDeviceLine(devices, key["device_".Length..], rest);
                    break;
            }
        }
        if (!vulkanOk) devices.Clear();

        //index order first, so the stable sorts below keep the earlier device on a tie
        var usable = devices.Values.Where(d => d.TypeCode is 1 or 2 && d.LargestLocalHeap > 0).OrderBy(d => d.Index).ToList();
        var cards = usable.Where(d => d.TypeCode == 2).OrderByDescending(d => d.LargestLocalHeap).ToList();
        var integrated = usable.Where(d => d.TypeCode == 1).OrderByDescending(d => d.LargestLocalHeap).FirstOrDefault();

        HardwareSnapshot? Snap(VkDevice? d, GpuKind kind, IReadOnlyList<ulong>? more = null) => visible is { } vis
            ? new HardwareSnapshot(installed, vis, kind, d?.LargestLocalHeap, d?.Vendor, d?.LocalHeapSum, more)
            : null;

        if (cards.Count == 0)
            return integrated is null
                ? new ProbeReading(Snap(null, GpuKind.None), cpuName, null)
                : new ProbeReading(Snap(integrated, GpuKind.Integrated), cpuName, integrated.Name);

        //llama-server spreads a model over every discrete card and leaves an integrated device out while one is present, so the cards add up
        var together = Snap(cards[0], GpuKind.Discrete, cards.Count > 1 ? [.. cards.Skip(1).Select(c => c.LargestLocalHeap)] : null);
        var names = string.Join(" + ", cards.Select(c => c.Name).OfType<string>());

        //a unified pool wins only past the floor and above every card together, and then the server must be pinned to it
        if (integrated is not null && together is not null && Snap(integrated, GpuKind.Integrated) is { } pool
            && HardwareClassifier.Classify(pool).GpuBudgetBytes is var poolBudget
            && poolBudget >= HardwareClassifier.PoolOverCardsFloorBytes
            && poolBudget > HardwareClassifier.Classify(together).GpuBudgetBytes)
            return new ProbeReading(pool, cpuName, integrated.Name, ServeOnlyGpu: integrated.Name);

        return new ProbeReading(together, cpuName, names.Length > 0 ? names : null);
    }

    //a malformed index, an unknown field or a value that fails its parse is ignored rather than fatal
    private static void ParseDeviceLine(Dictionary<int, VkDevice> devices, string afterPrefix, string rest)
    {
        var us = afterPrefix.IndexOf('_');
        if (us <= 0 || !int.TryParse(afterPrefix[..us], out var idx)) return;
        var field = afterPrefix[(us + 1)..];

        var dev = devices.TryGetValue(idx, out var existing) ? existing : new VkDevice(idx, null, 0, null, 0, 0);
        switch (field)
        {
            case "name":
                devices[idx] = dev with { Name = rest.Trim() is { Length: > 0 } n ? n : null };
                break;
            case "type":
            {
                //the child prints INTEGRATED_GPU (1), so take the word before the space
                var word = rest.Split(' ', 2)[0];
                var code = word switch
                {
                    "INTEGRATED_GPU" => 1u,
                    "DISCRETE_GPU" => 2u,
                    "VIRTUAL_GPU" => 3u,
                    "CPU" => 4u,
                    _ => 0u,                                    //the other device type, and any type word this list doesn't know
                };
                devices[idx] = dev with { TypeCode = code };
                break;
            }
            case "vendor":
            {
                //the child prints 0x1002 device_id=0x1586, so take the four hex digits after the first 0x
                var hexAt = rest.IndexOf("0x", StringComparison.Ordinal);
                if (hexAt < 0) break;
                var afterHex = rest[(hexAt + 2)..];
                var space = afterHex.IndexOf(' ');
                var hex = (space < 0 ? afterHex : afterHex[..space]).ToUpperInvariant();
                devices[idx] = dev with { Vendor = IsVendorId(hex) ? hex : null };
                break;
            }
            default:
                if (field.StartsWith("heap_", StringComparison.Ordinal))
                    ParseHeapLine(devices, idx, dev, rest);
                break;
        }
    }

    //bit 0 of the flags marks device-local, so test the bit rather than comparing values, and a line that fails either parse is dropped
    private static void ParseHeapLine(Dictionary<int, VkDevice> devices, int idx, VkDevice dev, string rest)
    {
        var tokens = rest.Split(' ', 3);
        if (tokens.Length < 2 || !ulong.TryParse(tokens[0], out var bytes)) return;
        const string flagsPrefix = "flags=0x";
        if (!tokens[1].StartsWith(flagsPrefix, StringComparison.Ordinal)) return;
        uint flags;
        try { flags = Convert.ToUInt32(tokens[1][flagsPrefix.Length..], 16); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { return; }
        if ((flags & 1) != 0)
            devices[idx] = dev with
            {
                LargestLocalHeap = Math.Max(dev.LargestLocalHeap, bytes),
                LocalHeapSum = dev.LocalHeapSum + bytes,
            };
    }

    //a vendor id is exactly four hex digits, and case varies by machine so the caller normalises it
    private static bool IsVendorId(string s)
    {
        if (s.Length != 4) return false;
        foreach (var c in s)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}
