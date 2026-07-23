using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0002: flags calls to methods that exist in .NET Core / .NET Standard 2.1 but not in
    /// .NET Framework 4.7.2. The project multi-targets both, so these calls fail to compile on the
    /// .NET Framework leg. Coverage is per-type; see <see cref="Classify"/> for the supported set.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NetFrameworkIncompatibleMethodAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>Property key used to pass the matched overload kind to the code fix.</summary>
        internal const string OverloadKindKey = "OverloadKind";

        internal const string StartsWithChar = "StartsWithChar";
        internal const string EndsWithChar = "EndsWithChar";
        internal const string ContainsChar = "ContainsChar";
        internal const string MathClamp = "MathClamp";

        /// <summary>
        /// Namespace of the DevExpress compatibility shims. When it is imported, the char/string
        /// <see cref="string"/> overloads below resolve to polyfill extension methods that also work
        /// on .NET Framework, so there is nothing to warn about.
        /// </summary>
        private const string NetCompatibilityExtensionsNamespace = "DevExpress.Data.NetCompatibility.Extensions";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.NetFrameworkIncompatibleMethod,
            title: "Member is missing in .NET Framework 4.7.2",
            messageFormat: "'{0}' does not exist in .NET Framework 4.7.2; use a compatible alternative",
            category: Categories.Compatibility,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The code multi-targets .NET Core and .NET Framework 4.7.2. This member only exists on .NET Core / .NET Standard 2.1 and will not compile on .NET Framework.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
                return;

            // Resolution runs against the .NET Core leg, so it can pick an overload that only differs
            // from a .NET Framework one by defaulted arguments; we need the real call-site arity.
            var argumentCount = invocation.ArgumentList?.Arguments.Count ?? 0;

            var (isIncompatible, overloadKind, display) = Classify(method, argumentCount);
            if (!isIncompatible)
                return;

            // DevExpress.Data.NetCompatibility.Extensions ships polyfills for a specific set of string
            // members (see IsPolyfilledByNetCompatibility). When that namespace is imported, those calls
            // compile on the .NET Framework leg too, so there is nothing to warn about.
            if (IsPolyfilledByNetCompatibility(method)
                && HasNetCompatibilityExtensionsImport(context.SemanticModel, invocation.SpanStart, context.CancellationToken))
                return;

            var location = invocation.Expression is MemberAccessExpressionSyntax memberAccess
                ? memberAccess.Name.GetLocation()
                : invocation.GetLocation();

            var properties = ImmutableDictionary<string, string?>.Empty;
            if (overloadKind is not null)
                properties = properties.Add(OverloadKindKey, overloadKind);

            context.ReportDiagnostic(Diagnostic.Create(Rule, location, properties, display));
        }

        private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
        {
            var creation = (ObjectCreationExpressionSyntax)context.Node;

            if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol constructor)
                return;

            if (!IsIncompatibleType(constructor.ContainingType, out var display))
                return;

            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.Type.GetLocation(), display));
        }

        private static bool IsIncompatibleType(INamedTypeSymbol? type, out string display)
        {
            // System.IO.Compression.ZipArchive is not available on our .NET Framework 4.7.2 leg.
            if (type is not null
                && type.Name == "ZipArchive"
                && type.ContainingNamespace?.ToDisplayString() == "System.IO.Compression")
            {
                display = "System.IO.Compression.ZipArchive";
                return true;
            }

            display = string.Empty;
            return false;
        }

        private static (bool isIncompatible, string? overloadKind, string display) Classify(IMethodSymbol method, int argumentCount)
        {
            var containingType = method.ContainingType;
            if (containingType is null)
                return (false, null, string.Empty);

            if (containingType.SpecialType == SpecialType.System_String)
                return ClassifyString(method, argumentCount);

            if (IsSystemType(containingType, "Math"))
                return ClassifyMath(method);

            return (false, null, string.Empty);
        }

        private static (bool isIncompatible, string? overloadKind, string display) ClassifyMath(IMethodSymbol method)
        {
            // System.Math.Clamp (all numeric overloads) was added in .NET Core 2.0 and does not
            // exist in .NET Framework.
            if (method.Name == "Clamp")
                return (true, MathClamp, "Math.Clamp");

            return (false, null, string.Empty);
        }

        private static (bool isIncompatible, string? overloadKind, string display) ClassifyString(IMethodSymbol method, int argumentCount)
        {
            var parameters = method.Parameters;

            switch (method.Name)
            {
                case "StartsWith":
                    if (IsSingle(parameters, SpecialType.System_Char))
                        return (true, StartsWithChar, "string.StartsWith(char)");
                    break;

                case "EndsWith":
                    if (IsSingle(parameters, SpecialType.System_Char))
                        return (true, EndsWithChar, "string.EndsWith(char)");
                    break;

                case "Contains":
                    if (IsSingle(parameters, SpecialType.System_Char))
                        return (true, ContainsChar, "string.Contains(char)");
                    if (parameters.Length == 2 && IsStringComparison(parameters[1].Type))
                    {
                        var first = parameters[0].Type.SpecialType == SpecialType.System_Char ? "char" : "string";
                        return (true, null, $"string.Contains({first}, StringComparison)");
                    }
                    break;

                case "GetHashCode":
                    if (parameters.Length == 1 && IsStringComparison(parameters[0].Type))
                        return (true, null, "string.GetHashCode(StringComparison)");
                    break;

                case "Split":
                    // Split(char, StringSplitOptions) and Split(string, StringSplitOptions) are .NET Core only.
                    if (parameters.Length >= 2
                        && (parameters[0].Type.SpecialType == SpecialType.System_Char
                            || parameters[0].Type.SpecialType == SpecialType.System_String)
                        && parameters[1].Type.Name == "StringSplitOptions")
                    {
                        var firstIsChar = parameters[0].Type.SpecialType == SpecialType.System_Char;

                        // Split('c') binds here on .NET Core (StringSplitOptions defaulted), but on
                        // .NET Framework the same one-argument call falls back to Split(params char[]),
                        // so it compiles on both legs. Split("s") has no such fallback, and passing the
                        // options argument explicitly rules the params overload out for either receiver.
                        if (firstIsChar && argumentCount <= 1)
                            break;

                        var first = firstIsChar ? "char" : "string";
                        return (true, null, $"string.Split({first}, StringSplitOptions)");
                    }
                    break;
            }

            return (false, null, string.Empty);
        }

        /// <summary>
        /// Returns whether <paramref name="method"/> is one of the string members that
        /// DevExpress.Data.NetCompatibility.Extensions provides a .NET Framework polyfill for.
        /// Only these calls become safe to make on the .NET Framework leg when that namespace is
        /// imported; other incompatible members (Split, GetHashCode(StringComparison), …) are not
        /// shimmed and must keep warning even when the using is present.
        /// </summary>
        private static bool IsPolyfilledByNetCompatibility(IMethodSymbol method)
        {
            if (method.ContainingType?.SpecialType != SpecialType.System_String)
                return false;

            var parameters = method.Parameters;
            switch (method.Name)
            {
                // StartsWith(char), EndsWith(char), Contains(char)
                case "StartsWith":
                case "EndsWith":
                case "Contains" when IsSingle(parameters, SpecialType.System_Char):
                    return IsSingle(parameters, SpecialType.System_Char);

                // Contains(string, StringComparison) — note the char/StringComparison overload is not shimmed.
                case "Contains":
                    return parameters.Length == 2
                        && parameters[0].Type.SpecialType == SpecialType.System_String
                        && IsStringComparison(parameters[1].Type);

                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns whether <see cref="NetCompatibilityExtensionsNamespace"/> is imported for the file
        /// containing <paramref name="position"/>. Import scopes cover ordinary <c>using</c> directives,
        /// namespace-scoped usings, and <c>global using</c>s (including ones in other files). In addition,
        /// the DevExpress idiom guards the using with <c>#if !NET</c>, so on the .NET Core leg — which is
        /// what the analyzer usually parses — it is excluded as disabled text; that guarded using still
        /// means the .NET Framework leg has the shims, so we detect it there as well.
        /// </summary>
        private static bool HasNetCompatibilityExtensionsImport(SemanticModel semanticModel, int position, CancellationToken cancellationToken)
        {
            foreach (var scope in semanticModel.GetImportScopes(position, cancellationToken))
            {
                foreach (var import in scope.Imports)
                {
                    if (import.NamespaceOrType is INamespaceSymbol ns
                        && ns.ToDisplayString() == NetCompatibilityExtensionsNamespace)
                        return true;
                }
            }

            return ImportedInDisabledText(semanticModel.SyntaxTree, cancellationToken);
        }

        /// <summary>
        /// Detects a <c>using DevExpress.Data.NetCompatibility.Extensions;</c> that sits inside an
        /// <c>#if</c> region the current parse excluded (so it is present only as disabled-text trivia).
        /// </summary>
        private static bool ImportedInDisabledText(SyntaxTree tree, CancellationToken cancellationToken)
        {
            foreach (var trivia in tree.GetRoot(cancellationToken).DescendantTrivia())
            {
                if (!trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                    continue;

                var text = trivia.ToString();
                // Cheap pre-filter before parsing the region.
                if (text.IndexOf(NetCompatibilityExtensionsNamespace, System.StringComparison.Ordinal) < 0)
                    continue;

                var unit = SyntaxFactory.ParseCompilationUnit(text);
                foreach (var directive in unit.Usings)
                {
                    if (directive.Alias is null
                        && directive.Name?.ToString() == NetCompatibilityExtensionsNamespace)
                        return true;
                }
            }

            return false;
        }

        private static bool IsSingle(ImmutableArray<IParameterSymbol> parameters, SpecialType type) =>
            parameters.Length == 1 && parameters[0].Type.SpecialType == type;

        private static bool IsStringComparison(ITypeSymbol type) =>
            IsSystemType(type, "StringComparison");

        private static bool IsSystemType(ITypeSymbol type, string name) =>
            type.Name == name && type.ContainingNamespace?.ToDisplayString() == "System";
    }
}
