using System.Reflection;
using Gatto.Cli.Setup;

namespace Gatto.Tests.Census;

//a transitional default body throws, so a fake falling off can't change behaviour quietly, and the census names the real implementors
public class ProbeDeclarationCensusTests
{
    //these members have a throwing default, so the real implementors must declare them themselves. a new transitional default on ISetupProbes needs a row here
    private static readonly string[] Transitional = ["FetchModel", "DeletePartial"];

    //these classes do the real work. the other fakes are the reason the defaults exist, so they are excluded on purpose.
    private static readonly Type[] Real = [typeof(LiveSetupProbes), typeof(Gatto.Tests.Fakes.WizardProbes)];

    //resolve the member through the interface map, a same-named method left by a drifted signature would keep a by-name check green
    private static MethodInfo? Implements(Type t, string name)
    {
        var map = t.GetInterfaceMap(typeof(ISetupProbes));
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
            if (map.InterfaceMethods[i].Name == name)
                return map.TargetMethods[i].DeclaringType == t ? map.TargetMethods[i] : null;
        return null;
    }

    [Fact]
    public void EVERY_REAL_IMPLEMENTOR_DECLARES_EACH_TRANSITIONAL_MEMBER()
    {
        var missing = new List<string>();
        foreach (var t in Real)
            foreach (var name in Transitional)
                if (Implements(t, name) is null)
                    missing.Add($"{t.Name} does not declare {name}");

        Assert.True(missing.Count == 0,
            "a real implementor is running ISetupProbes' throwing default instead of its own body — "
            + "the signature has drifted, or the member was never written:\n  "
            + string.Join("\n  ", missing));
    }

    //a member neither class declares must be reported missing, or the check can't fail at all
    [Fact]
    public void AND_THE_CHECK_CAN_SEE_A_MEMBER_THAT_IS_NOT_DECLARED()
    {
        foreach (var t in Real)
            Assert.Null(Implements(t, "AMemberNoProbeDeclares"));
    }

    //a declared member must be found, or the census above would pass by finding nothing
    [Theory]
    [InlineData("Scan")]
    [InlineData("FetchModel")]
    public void AND_IT_FINDS_A_MEMBER_THAT_IS_DECLARED(string name)
    {
        foreach (var t in Real)
            Assert.NotNull(Implements(t, name));
    }
}
