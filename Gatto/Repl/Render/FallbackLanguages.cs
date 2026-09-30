using Gatto.Terminal;

namespace Gatto.Repl.Render;

//one of the four ways a fallback scanner reads text (the entry below supplies only that language's own words and delimiters)
public enum ScanFamily { Json, Code, Markup, Shell }

public sealed record QuoteForm(string Open, string Close, bool Escapes, bool CrossesLines, string? HoleOpen = null);

//one language that the fallback scanners highlight. a word list is compared ordinally unless the entry says names ignore case
public sealed record FallbackLanguage
{
    public required CodeLanguage Language { get; init; }
    public required ScanFamily Family { get; init; }
    public required string[] Tags { get; init; }
    public string[] Extensions { get; init; } = [];
    public string[] LineComments { get; init; } = [];
    public string? BlockOpen { get; init; }
    public string? BlockClose { get; init; }
    public bool BlockNests { get; init; }

    //list the longer quote form first, otherwise a triple quote reads as an empty string and then another quote
    public QuoteForm[] Quotes { get; init; } = [];

    public string[] QuotePrefixes { get; init; } = [];
    public bool QuotePrefixesIgnoreCase { get; init; }

    //a prefix holding one of these letters makes each single brace inside its string a hole of code (a doubled brace stays text)
    public string HolePrefixLetters { get; init; } = "";

    //a string after one of these prefixes ignores backslashes and may pad both quotes with the same number of hashes
    public string[] RawQuotePrefixes { get; init; } = [];

    public string[] Keywords { get; init; } = [];

    //words the reference tokenizer reads as names and the scanner paints as keywords (the oracle's role map uses the same list)
    public string[] ContextualKeywords { get; init; } = [];

    public string[] Types { get; init; } = [];
    public string[] FunctionDeclarers { get; init; } = [];
    public string[] TypeDeclarers { get; init; } = [];
    public string[] AttributeOpeners { get; init; } = [];
    public bool CallIsFunction { get; init; }
    public bool CapitalisedCallIsPlain { get; init; }
    public bool DecoratorIsFunction { get; init; }
    public bool MacroIsFunction { get; init; }
    public bool KeywordAfterDotIsPlain { get; init; }
    public bool RegexLiterals { get; init; }
    public bool Lifetimes { get; init; }
    public string[] NumberSuffixes { get; init; } = [];
    public bool NamesIgnoreCase { get; init; }
    public string[] RawTextElements { get; init; } = [];

    //chars a name may hold besides letters, digits and the underscore
    public string IdentifierExtraChars { get; init; } = "";

    public bool PrivateNames { get; init; }
}

//one entry per language (the scanners read the entry and don't name a language)
public static class FallbackLanguages
{
    private static readonly QuoteForm Double = new(@"""", @"""", true, false);
    private static readonly QuoteForm Single = new("'", "'", true, false);
    private static readonly QuoteForm AttributeDouble = new(@"""", @"""", false, true);
    private static readonly QuoteForm AttributeSingle = new("'", "'", false, true);

    private static readonly string[] JavaScriptKeywords =
    [
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "export",
        "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return",
        "super", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with",
    ];

    private static readonly string[] JavaScriptContextual = ["let", "static", "yield", "async", "await", "of", "from", "as"];

    private static readonly string[] RustNumberTypes =
        ["i8", "i16", "i32", "i64", "i128", "isize", "u8", "u16", "u32", "u64", "u128", "usize", "f32", "f64"];

    public static readonly FallbackLanguage[] All =
    [
        new()
        {
            Language = CodeLanguage.Json,
            Family = ScanFamily.Json,
            Tags = ["json", "jsonc"],
            Extensions = [".json"],
            LineComments = ["//"],
            BlockOpen = "/*",
            BlockClose = "*/",
            Quotes = [Double],
            Keywords = ["true", "false", "null"],
        },
        new()
        {
            Language = CodeLanguage.JavaScript,
            Family = ScanFamily.Code,
            Tags = ["javascript", "js", "mjs", "cjs"],
            Extensions = [".js", ".mjs", ".cjs"],
            LineComments = ["//"],
            BlockOpen = "/*",
            BlockClose = "*/",
            Quotes = [Double, Single, new("`", "`", true, true, "${")],
            Keywords = JavaScriptKeywords,
            ContextualKeywords = JavaScriptContextual,
            FunctionDeclarers = ["function"],
            TypeDeclarers = ["class"],
            CallIsFunction = true,
            KeywordAfterDotIsPlain = true,
            RegexLiterals = true,
            IdentifierExtraChars = "$",
            PrivateNames = true,
            NumberSuffixes = ["n"],
        },
        new()
        {
            Language = CodeLanguage.TypeScript,
            Family = ScanFamily.Code,
            Tags = ["typescript", "ts"],
            Extensions = [".ts"],
            LineComments = ["//"],
            BlockOpen = "/*",
            BlockClose = "*/",
            Quotes = [Double, Single, new("`", "`", true, true, "${")],
            Keywords =
            [
                .. JavaScriptKeywords, "interface", "type", "enum", "implements", "namespace", "declare", "abstract", "readonly",
                "private", "public", "protected", "keyof", "infer", "is", "satisfies", "string", "number", "boolean", "any",
                "unknown", "never", "object", "symbol", "bigint", "undefined",
            ],
            ContextualKeywords = JavaScriptContextual,
            FunctionDeclarers = ["function"],
            TypeDeclarers = ["class", "interface", "type", "enum"],
            CallIsFunction = true,
            KeywordAfterDotIsPlain = true,
            RegexLiterals = true,
            IdentifierExtraChars = "$",
            PrivateNames = true,
            NumberSuffixes = ["n"],
        },
        new()
        {
            Language = CodeLanguage.Python,
            Family = ScanFamily.Code,
            Tags = ["python", "py", "python3"],
            Extensions = [".py"],
            LineComments = ["#"],
            Quotes = [new(@"""""""", @"""""""", true, true), new("'''", "'''", true, true), Double, Single],
            QuotePrefixes = ["r", "u", "b", "f", "t", "br", "rb", "fr", "rf", "tr", "rt"],
            QuotePrefixesIgnoreCase = true,
            HolePrefixLetters = "fFtT",
            Keywords =
            [
                "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
                "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal",
                "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
            ],
            FunctionDeclarers = ["def"],
            TypeDeclarers = ["class"],
            CallIsFunction = true,
            DecoratorIsFunction = true,
            KeywordAfterDotIsPlain = true,
            NumberSuffixes = ["j", "J"],
        },
        new()
        {
            Language = CodeLanguage.Xml,
            Family = ScanFamily.Markup,
            Tags = ["xml", "svg", "xaml"],
            Extensions = [".xml", ".csproj", ".svg"],
            BlockOpen = "<!--",
            BlockClose = "-->",
            Quotes = [AttributeDouble, AttributeSingle],
        },
        new()
        {
            Language = CodeLanguage.Html,
            Family = ScanFamily.Markup,
            Tags = ["html", "htm"],
            Extensions = [".html", ".htm"],
            BlockOpen = "<!--",
            BlockClose = "-->",
            Quotes = [AttributeDouble, AttributeSingle],
            NamesIgnoreCase = true,
            RawTextElements = ["script", "style"],
        },
        new()
        {
            Language = CodeLanguage.Rust,
            Family = ScanFamily.Code,
            Tags = ["rust", "rs"],
            Extensions = [".rs"],
            LineComments = ["//"],
            BlockOpen = "/*",
            BlockClose = "*/",
            BlockNests = true,
            Quotes = [new(@"""", @"""", true, true), Single],
            QuotePrefixes = ["b"],
            RawQuotePrefixes = ["r", "br"],
            Keywords =
            [
                "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum", "extern", "false", "fn",
                "for", "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self",
                "static", "struct", "super", "trait", "true", "type", "unsafe", "use", "where", "while",
            ],
            Types = [.. RustNumberTypes, "bool", "char", "str"],
            FunctionDeclarers = ["fn"],
            TypeDeclarers = ["struct", "enum", "trait", "type"],
            AttributeOpeners = ["#[", "#!["],
            CallIsFunction = true,
            CapitalisedCallIsPlain = true,
            MacroIsFunction = true,
            Lifetimes = true,
            NumberSuffixes = RustNumberTypes,
        },
        new()
        {
            Language = CodeLanguage.Bash,
            Family = ScanFamily.Shell,
            Tags = ["bash", "sh", "shell", "zsh"],
            Extensions = [".sh"],
            LineComments = ["#"],
            Quotes = [new("'", "'", false, true), new(@"""", @"""", true, true, "$")],
            Keywords =
            [
                "if", "then", "else", "elif", "fi", "case", "esac", "for", "select", "while", "until", "do", "done", "in",
                "function", "time", "[[", "]]",
            ],
            FunctionDeclarers = ["function"],
        },
    ];

    private static readonly Dictionary<string, FallbackLanguage> ByTag =
        All.SelectMany(e => e.Tags.Select(t => (t, e))).ToDictionary(p => p.t, p => p.e, StringComparer.Ordinal);

    private static readonly Dictionary<string, FallbackLanguage> ByExtension =
        All.SelectMany(e => e.Extensions.Select(x => (x, e))).ToDictionary(p => p.x, p => p.e, StringComparer.Ordinal);

    private static readonly Dictionary<CodeLanguage, FallbackLanguage> ByLanguage = All.ToDictionary(e => e.Language);

    //the language that a trimmed lower-case fence tag names, or none
    public static CodeLanguage OfTag(string tag) => ByTag.TryGetValue(tag, out var e) ? e.Language : CodeLanguage.None;

    //the language that a lower-case extension with its dot names, or none
    public static CodeLanguage OfExtension(string extension) => ByExtension.TryGetValue(extension, out var e) ? e.Language : CodeLanguage.None;

    public static FallbackLanguage? Of(CodeLanguage language) => ByLanguage.GetValueOrDefault(language);
}
