using System.Reflection;
using System.Reflection.Emit;
using Gatto.Cli;

namespace Gatto.Tests.Census;

//the layers depend downward only, a reference that runs up or sideways still compiles inside one assembly
public class LayerDirectionCensusTests
{
    private static readonly string[] Layers = ["Cli", "Repl", "Roles", "Extensions", "Core", "Terminal"];

    //the declared graph: repl, roles and extensions sit beside each other on core alone, and terminal sits under cli and repl naming no layer
    private static readonly HashSet<(string From, string To)> Allowed =
    [
        ("Cli", "Repl"), ("Cli", "Roles"), ("Cli", "Extensions"), ("Cli", "Core"),
        ("Repl", "Core"), ("Roles", "Core"), ("Extensions", "Core"),
        ("Cli", "Terminal"), ("Repl", "Terminal"),
    ];

    //namespaces that hold only generated types and the entry point, so their types sit in no layer
    private static readonly string[] Generated = ["", "System.Text.RegularExpressions.Generated"];

    private static readonly Assembly Product = typeof(GattoApp).Assembly;

    private static readonly Assembly Library = typeof(Gatto.Terminal.TermText).Assembly;

    private static readonly Assembly[] Assemblies = [Product, Library];

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OneByte = [];
    private static readonly Dictionary<short, OpCode> TwoByte = [];

    static LayerDirectionCensusTests()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)f.GetValue(null)!;
            if (op.Size == 1) OneByte[op.Value] = op;
            else TwoByte[(short)(op.Value & 0xFF)] = op;
        }
    }

    private static Type Outermost(Type t)
    {
        while (t.IsNested) t = t.DeclaringType!;
        return t;
    }

    //the layer is the namespace's second segment in either product assembly, so Gatto.Terminal needs no special case
    private static string? ProductLayer(Type t)
    {
        t = Outermost(t);
        if (!Assemblies.Contains(t.Assembly) || t.Namespace is not { } ns || !ns.StartsWith("Gatto.", StringComparison.Ordinal)) return null;
        return ns.Split('.')[1];
    }

    private static (Dictionary<(string From, string To), SortedSet<string>> Edges, int Unresolved) ProductEdges() =>
        MergedEdges(Assemblies, ProductLayer);

    //the library has no edge out, so a known match driven through this merge is what proves it reads every assembly
    private static (Dictionary<(string From, string To), SortedSet<string>> Edges, int Unresolved) MergedEdges(
        IEnumerable<Assembly> assemblies, Func<Type, string?> layerOf)
    {
        var merged = new Dictionary<(string, string), SortedSet<string>>();
        var unresolved = 0;
        foreach (var asm in assemblies)
        {
            var (edges, u) = Edges(asm, layerOf);
            unresolved += u;
            foreach (var (pair, sites) in edges)
                if (merged.TryGetValue(pair, out var set)) set.UnionWith(sites);
                else merged[pair] = sites;
        }
        return (merged, unresolved);
    }

    private static IEnumerable<Type> Named(Type? t)
    {
        if (t is null || t.IsGenericParameter) yield break;
        if (t.HasElementType)
        {
            foreach (var e in Named(t.GetElementType())) yield return e;
            yield break;
        }
        yield return t;
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            foreach (var a in t.GetGenericArguments())
                foreach (var e in Named(a)) yield return e;
    }

    //the reader sees what the compiler bound, so a partly qualified name, a lambda body or an async state machine counts like a using
    internal static (Dictionary<(string From, string To), SortedSet<string>> Edges, int Unresolved) Edges(
        Assembly asm, Func<Type, string?> layerOf)
    {
        var edges = new Dictionary<(string, string), SortedSet<string>>();
        var unresolved = 0;

        void Edge(Type from, Type? to)
        {
            var lf = layerOf(from);
            if (lf is null) return;
            foreach (var t in Named(to))
            {
                var lt = layerOf(t);
                if (lt is null || lt == lf) continue;
                if (!edges.TryGetValue((lf, lt), out var set))
                    edges[(lf, lt)] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add($"{Outermost(from).FullName} -> {Outermost(t).FullName}");
            }
        }

        foreach (var type in asm.GetTypes())
        {
            if (layerOf(type) is null) continue;
            Edge(type, type.BaseType);
            foreach (var i in type.GetInterfaces()) Edge(type, i);
            foreach (var a in type.GetCustomAttributesData()) Edge(type, a.AttributeType);
            foreach (var f in type.GetFields(Declared)) Edge(type, f.FieldType);
            foreach (var p in type.GetProperties(Declared)) Edge(type, p.PropertyType);
            foreach (var m in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            {
                if (m is MethodInfo mi) Edge(type, mi.ReturnType);
                foreach (var p in m.GetParameters()) Edge(type, p.ParameterType);
                if (m.GetMethodBody() is not { } body) continue;
                foreach (var l in body.LocalVariables) Edge(type, l.LocalType);

                var il = body.GetILAsByteArray() ?? [];
                var typeArgs = type.IsGenericType ? type.GetGenericArguments() : null;
                var methodArgs = m.IsGenericMethod ? m.GetGenericArguments() : null;
                for (var pos = 0; pos < il.Length;)
                {
                    var op = il[pos] == 0xFE ? TwoByte[il[pos + 1]] : OneByte[il[pos]];
                    pos += op.Size;
                    var size = op.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
                        _ => 4,
                    };
                    if (op.OperandType is OperandType.InlineField or OperandType.InlineMethod
                        or OperandType.InlineTok or OperandType.InlineType)
                    {
                        MemberInfo? member = null;
                        try { member = type.Module.ResolveMember(BitConverter.ToInt32(il, pos), typeArgs, methodArgs); }
                        catch (ArgumentException) { unresolved++; }
                        switch (member)
                        {
                            case Type t:
                                Edge(type, t);
                                break;
                            case MethodBase mb:
                                Edge(type, mb.DeclaringType);
                                if (mb.IsGenericMethod) foreach (var g in mb.GetGenericArguments()) Edge(type, g);
                                break;
                            case FieldInfo fi:
                                Edge(type, fi.DeclaringType);
                                break;
                        }
                    }
                    pos += size;
                }
            }
        }
        return (edges, unresolved);
    }

    private static List<string> Offences(IReadOnlyDictionary<(string From, string To), SortedSet<string>> edges)
    {
        var offences = new List<string>();
        foreach (var ((from, to), sites) in edges.Where(e => !Allowed.Contains(e.Key)).OrderBy(e => e.Key))
            offences.Add($"{from} depends on {to}, a direction the graph forbids:\n    "
                + string.Join("\n    ", sites.Take(8)));
        foreach (var (from, to) in Allowed.Where(a => !edges.ContainsKey(a)).OrderBy(a => a))
            offences.Add($"{from} no longer depends on {to}. drop the pair from the declared graph if the tree dropped it on purpose");
        return offences;
    }

    [Fact]
    public void THE_LAYERS_DEPEND_ONLY_IN_THE_DECLARED_DIRECTIONS()
    {
        var (edges, unresolved) = ProductEdges();

        Assert.True(unresolved == 0, $"the reader could not resolve {unresolved} operands, so it is blind to part of the tree");
        var offences = Offences(edges);
        Assert.True(offences.Count == 0,
            "the layer graph is Cli over Repl, Roles and Extensions, each over Core, nothing between the middle three, and Terminal under all of them naming none:\n"
            + string.Join("\n", offences)
            + "\nmove the type to the lower layer, or pass the value down. never add a pair to make this pass.");
    }

    //a new top-level namespace must join the layer list, no other check reads its types
    [Fact]
    public void EVERY_TYPE_SITS_IN_A_LAYER_OR_WAS_WRITTEN_BY_A_GENERATOR()
    {
        var outside = Assemblies.SelectMany(a => a.GetTypes()).Where(t => !t.IsNested)
            .Where(t => !(ProductLayer(t) is { } layer && Layers.Contains(layer)))
            .Where(t => !(Generated.Contains(t.Namespace ?? "") && (t.Name.StartsWith('<') || t.Name == "Program")))
            .Select(t => t.FullName).ToList();

        Assert.True(outside.Count == 0,
            "these types sit in no layer:\n  " + string.Join("\n  ", outside));
    }

    //a terminal type cannot name gatto and compile, so the type that does it on purpose must still show its edge from this assembly
    [Fact]
    public void A_TERMINAL_TYPE_THAT_NAMES_GATTO_GOES_RED()
    {
        const string plant = "Gatto.Tests.Census.LayerPlants.Terminal";
        string? PlantedLayer(Type t) => Outermost(t).Namespace == plant ? "Terminal" : ProductLayer(t);

        var (edges, _) = MergedEdges([.. Assemblies, typeof(LayerDirectionCensusTests).Assembly], PlantedLayer);

        Assert.True(edges.TryGetValue(("Terminal", "Cli"), out var sites), "the reader found no edge from the planted terminal type");
        Assert.Contains(sites!, s => s == $"{plant}.NamesGatto -> Gatto.Cli.GattoApp");
        Assert.Contains(Offences(edges), o => o.StartsWith("Terminal depends on Cli,", StringComparison.Ordinal));
    }

    [Fact]
    public void A_PLANTED_EDGE_IN_EACH_FORBIDDEN_DIRECTION_GOES_RED()
    {
        var (real, _) = ProductEdges();
        Assert.Empty(Offences(real));

        var forbidden = Layers.SelectMany(a => Layers.Select(b => (From: a, To: b)))
            .Where(p => p.From != p.To && !Allowed.Contains(p)).ToList();
        Assert.Equal(21, forbidden.Count);

        foreach (var (from, to) in forbidden)
        {
            var planted = new Dictionary<(string From, string To), SortedSet<string>>(real)
            {
                [(from, to)] = new(StringComparer.Ordinal) { "Planted -> Planted" },
            };
            Assert.Contains(Offences(planted), o => o.StartsWith($"{from} depends on {to},", StringComparison.Ordinal));
        }
    }

    //each type written for this check reaches the other layer through one channel, so a blind reader loses exactly that one
    [Fact]
    public void THE_READER_SEES_AN_EDGE_THROUGH_EVERY_CHANNEL()
    {
        const string root = "Gatto.Tests.Census.LayerPlants.";
        static string? PlantLayer(Type t) =>
            Outermost(t).Namespace is { } ns && ns.StartsWith(root, StringComparison.Ordinal) ? ns[root.Length..] : null;

        var (edges, unresolved) = Edges(typeof(LayerDirectionCensusTests).Assembly, PlantLayer);

        Assert.Equal(0, unresolved);
        Assert.False(edges.ContainsKey(("High", "Low")), "a plant in High references nothing in Low, so the reader invented an edge");
        Assert.True(edges.TryGetValue(("Low", "High"), out var sites), "the reader found no edge from any plant, so it reads nothing");
        var seen = sites!.Select(s => s[(s.LastIndexOf(".Low.", StringComparison.Ordinal) + 5)..s.IndexOf(' ')]).ToHashSet();
        string[] channels =
        [
            "ViaBase", "ViaInterface", "ViaAttribute", "ViaField", "ViaProperty", "ViaParameter", "ViaReturn",
            "ViaLocal", "ViaGeneric", "ViaCall", "ViaNew", "ViaLambda", "ViaAsync", "ViaStaticField",
        ];
        Assert.Equal(channels.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
    }
}
