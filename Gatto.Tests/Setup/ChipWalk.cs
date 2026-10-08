using Gatto.Cli.Setup;
using Gatto.Core.Acquire;

namespace Gatto.Tests.Setup;

//the chip presses that leave one family lit alone, since a chip toggles and the shelf opens on the landing pair
internal static class ChipWalk
{
    //from the landing pair: light the family when the pair lacks it, then turn every other landing family off
    public static IReadOnlyList<string> PressesFor(string family)
    {
        var landing = Families.Load().Landing;
        var presses = new List<string>();
        if (!landing.Contains(family, StringComparer.OrdinalIgnoreCase)) presses.Add(family);
        presses.AddRange(landing.Where(f => !string.Equals(f, family, StringComparison.OrdinalIgnoreCase)));
        return presses;
    }

    //the presses answered on the flow, the screen of the last one returned
    public static WizardScreen Narrow(SetupFlow flow, string family)
    {
        WizardScreen? last = null;
        foreach (var chip in PressesFor(family)) last = flow.Answer(ShelfControls.FamilyAnswer(chip));
        return last!;
    }
}
