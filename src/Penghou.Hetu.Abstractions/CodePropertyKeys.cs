namespace Penghou.Hetu;

/// <summary>
/// Well-known normalized property keys. Plugins write these; queries read
/// them. Using the constants keeps both sides in sync instead of failing
/// silently on a typo.
/// </summary>
public static class CodePropertyKeys
{
    public const string Language = "language";
    public const string CanonicalKey = "canonical-key";
    public const string SymbolKind = "symbol-kind";
    public const string Access = "access";
    public const string Modifiers = "modifiers";
    public const string Attributes = "attributes";
    public const string Obsolete = "obsolete";
    public const string TestMethod = "test-method";
    public const string TestProject = "test-project";
    public const string HttpEndpoint = "http-endpoint";
    public const string RouteTemplate = "route-template";
    public const string ConstantValue = "constant-value";
    public const string DocSummary = "doc-summary";
    public const string DocRemarks = "doc-remarks";
    public const string AssemblyName = "assembly-name";
    public const string TargetFramework = "target-framework";
    public const string Nullable = "nullable";
    public const string ImplicitUsings = "implicit-usings";
    public const string DefineConstants = "define-constants";
    public const string ContentHash = "content-hash";
    public const string PackageVersion = "package-version";
    public const string PackageCondition = "package-condition";
    public const string ImportAlias = "import-alias";
    public const string ImportStatic = "import-static";
    public const string ImportGlobal = "import-global";
    public const string ImportScope = "import-scope";
}
