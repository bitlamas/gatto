using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//classifies C# with Roslyn's parser. a fragment parses as a script, and malformed text arrives as tokens instead of an exception
public static class CSharpClassifier
{
    private static readonly CSharpParseOptions Options = CSharpParseOptions.Default
        .WithKind(SourceCodeKind.Script)
        .WithLanguageVersion(LanguageVersion.Preview);

    public static IReadOnlyList<RoleSpan> Classify(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text, Options).GetRoot();
        var spans = new List<RoleSpan>();
        foreach (var token in root.DescendantTokens())
        {
            AddTrivia(token.LeadingTrivia, spans);
            var role = RoleOf(token);
            if (role != SpanRole.None && token.Span.Length > 0)
                spans.Add(new RoleSpan(token.SpanStart, token.Span.Length, role));
            AddTrivia(token.TrailingTrivia, spans);
        }
        return spans;
    }

    private static void AddTrivia(SyntaxTriviaList list, List<RoleSpan> spans)
    {
        foreach (var trivia in list)
        {
            var role = trivia.Kind() switch
            {
                SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
                    or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia
                    or SyntaxKind.DisabledTextTrivia => SpanRole.Comment,
                _ when trivia.IsDirective => SpanRole.Keyword,
                _ => SpanRole.None,
            };
            if (role != SpanRole.None && trivia.FullSpan.Length > 0)
                spans.Add(new RoleSpan(trivia.FullSpan.Start, trivia.FullSpan.Length, role));
        }
    }

    private static SpanRole RoleOf(SyntaxToken token)
    {
        var kind = token.Kind();
        if (kind == SyntaxKind.IdentifierToken) return IdentifierRole(token);
        if (SyntaxFacts.IsKeywordKind(kind)) return SpanRole.Keyword;
        return kind switch
        {
            SyntaxKind.NumericLiteralToken => SpanRole.Number,
            SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken or SyntaxKind.Utf8StringLiteralToken
                or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
                or SyntaxKind.Utf8SingleLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken
                or SyntaxKind.InterpolatedStringStartToken or SyntaxKind.InterpolatedVerbatimStringStartToken
                or SyntaxKind.InterpolatedSingleLineRawStringStartToken or SyntaxKind.InterpolatedMultiLineRawStringStartToken
                or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.InterpolatedStringEndToken
                or SyntaxKind.InterpolatedRawStringEndToken => SpanRole.String,
            _ => SyntaxFacts.IsPunctuation(kind) ? SpanRole.Punct : SpanRole.None,
        };
    }

    private static SpanRole IdentifierRole(SyntaxToken token)
    {
        var parent = token.Parent;
        if (parent is BaseTypeDeclarationSyntax type && type.Identifier == token) return SpanRole.Type;
        if (parent is DelegateDeclarationSyntax del && del.Identifier == token) return SpanRole.Type;
        if (parent is ConstructorDeclarationSyntax ctor && ctor.Identifier == token) return SpanRole.Type;
        if (parent is TypeParameterSyntax) return SpanRole.Type;
        if (parent is MethodDeclarationSyntax method && method.Identifier == token) return SpanRole.Function;
        if (parent is LocalFunctionStatementSyntax local && local.Identifier == token) return SpanRole.Function;
        return parent is SimpleNameSyntax name ? NameRole(name, token.ValueText) : SpanRole.None;
    }

    //a name's role needs its parent. a call is a function, a type position is a type, and anything else needs semantics gatto does not run
    private static SpanRole NameRole(SimpleNameSyntax name, string text)
    {
        var invoked = IsInvoked(name);
        if (invoked) return text == "nameof" ? SpanRole.Keyword : SpanRole.Function;
        if (name.Parent is AttributeSyntax) return SpanRole.Type;
        if (!SyntaxFacts.IsInTypeOnlyContext(name)) return SpanRole.None;
        return name is IdentifierNameSyntax && text is "var" or "dynamic" ? SpanRole.Keyword : SpanRole.Type;
    }

    private static bool IsInvoked(SimpleNameSyntax name)
    {
        ExpressionSyntax callee = name;
        if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name) callee = access;
        else if (name.Parent is MemberBindingExpressionSyntax binding && binding.Name == name) callee = binding;
        return callee.Parent is InvocationExpressionSyntax invocation && invocation.Expression == callee;
    }
}
