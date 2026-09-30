using Gatto.Cli.Setup;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the screens every test crosses before its own subject. a test whose subject is one of them answers it by name
internal static class WalkOpening
{
    //a screen added to the opening is one row here and nothing else, the three vocabularies all derive from this table
    private static readonly (string Screen, string Answer, ConsoleKeyInfo Key)[] Steps =
    [
        (SetupFlow.WelcomeKey, SetupFlow.WelcomeGo, WizardRig.Enter),
        (SetupFlow.MachineKey, SetupFlow.MachineNext, WizardRig.Enter),
    ];

    //the opening as screen keys, in the order a face shows them
    public static IReadOnlyList<string> Screens => [.. Steps.Select(s => s.Screen)];

    //the opening as answers, for a surface scripted with answer keys
    public static string?[] Answers => [.. Steps.Select(s => (string?)s.Answer)];

    //the opening plus the engine step's answer, only for a machine that already has an engine. a machine with none uses Answers
    public static string?[] PastEngine => [.. Answers, SetupFlow.FoundUse];

    //the opening as keystrokes, for a test that drives the real face
    public static ConsoleKeyInfo[] Keys => [.. Steps.Select(s => s.Key)];
}
