using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Penghou.Hetu;

public sealed partial class CSharpCodeGraphPlugin
{
    /// <summary>Builds normalized symbol properties and doc summaries.</summary>
    private static class CSharpSymbolPropertyFactory
    {
        private static readonly HashSet<string> TestMethodAttributes = new(
            [
                "FactAttribute",
                "TheoryAttribute",
                "TestAttribute",
                "TestCaseAttribute",
                "TestMethodAttribute",
                "DataTestMethodAttribute"
            ],
            StringComparer.Ordinal);

        internal static Dictionary<string, CodePropertyValue> BuildProperties(ISymbol symbol, SyntaxNode syntax)
        {
            var properties = new Dictionary<string, CodePropertyValue>
            {
                [CodePropertyKeys.Language] = new CodeTextProperty("csharp"),
                [CodePropertyKeys.CanonicalKey] = new CodeTextProperty(CanonicalSymbolKey(symbol)),
                [CodePropertyKeys.SymbolKind] = new CodeTextProperty(symbol.Kind.ToString().ToLowerInvariant()),
                [CodePropertyKeys.Access] = new CodeTextProperty(
                    symbol.DeclaredAccessibility.ToString().ToLowerInvariant())
            };

            var modifiers = new List<string>();
            AddModifier(modifiers, symbol.IsStatic, "static");
            AddModifier(modifiers, symbol.IsAbstract, "abstract");
            AddModifier(modifiers, symbol.IsVirtual, "virtual");
            AddModifier(modifiers, symbol.IsOverride, "override");
            AddModifier(modifiers, symbol.IsSealed, "sealed");
            if (symbol is IFieldSymbol fieldSymbol)
            {
                AddModifier(modifiers, fieldSymbol.IsReadOnly, "readonly");
                AddModifier(modifiers, fieldSymbol.IsConst, "const");
            }
            if (modifiers.Count > 0)
            {
                properties[CodePropertyKeys.Modifiers] = new CodeTextProperty(
                    string.Join(" ", modifiers.OrderBy(value => value, StringComparer.Ordinal)));
            }

            var attributes = symbol.GetAttributes();
            if (attributes.Length > 0)
            {
                var names = attributes
                    .Select(attribute => attribute.AttributeClass?.Name)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Take(16)
                    .ToArray();
                properties[CodePropertyKeys.Attributes] = new CodeTextProperty(string.Join(" ", names));
                if (names.Contains("ObsoleteAttribute"))
                    properties[CodePropertyKeys.Obsolete] = new CodeBooleanProperty(true);
                // Test entry points are an exact framework-attribute allowlist;
                // anything else is not a test, never guessed.
                if (symbol is IMethodSymbol &&
                    names.Intersect(TestMethodAttributes, StringComparer.Ordinal).Any())
                    properties[CodePropertyKeys.TestMethod] = new CodeBooleanProperty(true);
            }

            if (symbol is IFieldSymbol { HasConstantValue: true } constant &&
                constant.ConstantValue is not null)
            {
                var literal = ToLiteral(constant.ConstantValue);
                if (literal is not null)
                    properties[CodePropertyKeys.ConstantValue] = literal;
            }

            var summary = GetDocSummary(syntax);
            if (summary is not null)
                properties[CodePropertyKeys.DocSummary] = new CodeTextProperty(summary);

            return properties;
        }

        private static string? GetDocSummary(SyntaxNode syntax)
        {
            var firstToken = syntax.GetFirstToken(includeZeroWidth: true);
            var allTrivia = new List<SyntaxTrivia>();
            var current = firstToken;
            for (var i = 0; i < 10; i++)
            {
                allTrivia.AddRange(current.LeadingTrivia);
                var previous = current.GetPreviousToken();
                if (previous == default)
                    break;
                allTrivia.AddRange(previous.TrailingTrivia);
                current = previous;
            }

            foreach (var trivia in allTrivia)
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
                    !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                    continue;

                return ExtractSummaryText(trivia.ToFullString());
            }

            return null;
        }

        private static string? ExtractSummaryText(string text)
        {
            var summaryStart = text.IndexOf("<summary>", StringComparison.Ordinal);
            var summaryEnd = text.IndexOf("</summary>", StringComparison.Ordinal);
            if (summaryStart < 0 || summaryEnd <= summaryStart)
                return null;

            var inner = text[(summaryStart + 9)..summaryEnd];
            var collapsed = string.Join(
                ' ',
                inner.Split(
                    [' ', '\r', '\n', '\t', '/'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (collapsed.Length == 0)
                return null;

            return collapsed.Length <= 512
                ? collapsed
                : collapsed[..512];
        }

        private static void AddModifier(List<string> modifiers, bool condition, string name)
        {
            if (condition)
                modifiers.Add(name);
        }

        private static CodePropertyValue? ToLiteral(object value) => value switch
        {
            string text => new CodeTextProperty(text),
            bool flag => new CodeBooleanProperty(flag),
            char character => new CodeTextProperty(character.ToString()),
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                new CodeIntegerProperty(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
            float or double or decimal =>
                new CodeNumberProperty(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => null
        };
    }
}
