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

        private static readonly HashSet<string> HttpEndpointAttributes = new(
            [
                "RouteAttribute",
                "HttpGetAttribute",
                "HttpPostAttribute",
                "HttpPutAttribute",
                "HttpDeleteAttribute",
                "HttpPatchAttribute",
                "HttpHeadAttribute",
                "HttpOptionsAttribute"
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
                // HTTP endpoints are the same: an exact ASP.NET Core
                // route/verb allowlist on methods; anything else is not an
                // endpoint, never guessed. The template is bounded syntax
                // evidence, never evaluated routing semantics.
                if (symbol is IMethodSymbol &&
                    names.Intersect(HttpEndpointAttributes, StringComparer.Ordinal).Any())
                {
                    properties[CodePropertyKeys.HttpEndpoint] = new CodeBooleanProperty(true);
                    var template = GetRouteTemplate(symbol);
                    if (template is not null)
                        properties[CodePropertyKeys.RouteTemplate] = new CodeTextProperty(template);
                }
            }

            if (symbol is IFieldSymbol { HasConstantValue: true } constant &&
                constant.ConstantValue is not null)
            {
                var literal = ToLiteral(constant.ConstantValue);
                if (literal is not null)
                    properties[CodePropertyKeys.ConstantValue] = literal;
            }

            var summary = GetDocTag(syntax, "summary");
            if (summary is not null)
                properties[CodePropertyKeys.DocSummary] = new CodeTextProperty(summary);

            var remarks = GetDocTag(syntax, "remarks");
            if (remarks is not null)
                properties[CodePropertyKeys.DocRemarks] = new CodeTextProperty(remarks);

            return properties;
        }

        private static string? GetDocTag(SyntaxNode syntax, string tag)
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

                return ExtractTagText(trivia.ToFullString(), tag);
            }

            return null;
        }

        private static string? ExtractTagText(string text, string tag)
        {
            var open = $"<{tag}>";
            var close = $"</{tag}>";
            var tagStart = text.IndexOf(open, StringComparison.Ordinal);
            var tagEnd = text.IndexOf(close, StringComparison.Ordinal);
            if (tagStart < 0 || tagEnd <= tagStart)
                return null;

            var inner = text[(tagStart + open.Length)..tagEnd];
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

        private static string? GetRouteTemplate(ISymbol symbol)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                if (attribute.AttributeClass?.Name is not string name ||
                    !HttpEndpointAttributes.Contains(name))
                    continue;

                foreach (var argument in attribute.ConstructorArguments)
                {
                    if (argument.Value is string template && !string.IsNullOrWhiteSpace(template))
                        return BoundTemplate(template);
                }

                foreach (var pair in attribute.NamedArguments)
                {
                    if (string.Equals(pair.Key, "Template", StringComparison.Ordinal) &&
                        pair.Value.Value is string named && !string.IsNullOrWhiteSpace(named))
                        return BoundTemplate(named);
                }
            }

            return null;
        }

        private static string BoundTemplate(string template)
        {
            var trimmed = template.Trim();
            return trimmed.Length <= 256 ? trimmed : trimmed[..256];
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
