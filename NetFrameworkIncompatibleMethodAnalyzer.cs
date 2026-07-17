using System.Collections.Immutable;
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

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.NetFrameworkIncompatibleMethod,
            title: "Method is missing in .NET Framework 4.7.2",
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
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
                return;

            var (isIncompatible, overloadKind, display) = Classify(method);
            if (!isIncompatible)
                return;

            var location = invocation.Expression is MemberAccessExpressionSyntax memberAccess
                ? memberAccess.Name.GetLocation()
                : invocation.GetLocation();

            var properties = ImmutableDictionary<string, string?>.Empty;
            if (overloadKind is not null)
                properties = properties.Add(OverloadKindKey, overloadKind);

            context.ReportDiagnostic(Diagnostic.Create(Rule, location, properties, display));
        }

        private static (bool isIncompatible, string? overloadKind, string display) Classify(IMethodSymbol method)
        {
            var containingType = method.ContainingType;
            if (containingType is null)
                return (false, null, string.Empty);

            if (containingType.SpecialType == SpecialType.System_String)
                return ClassifyString(method);

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

        private static (bool isIncompatible, string? overloadKind, string display) ClassifyString(IMethodSymbol method)
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
                        var first = parameters[0].Type.SpecialType == SpecialType.System_Char ? "char" : "string";
                        return (true, null, $"string.Split({first}, StringSplitOptions)");
                    }
                    break;
            }

            return (false, null, string.Empty);
        }

        private static bool IsSingle(ImmutableArray<IParameterSymbol> parameters, SpecialType type) =>
            parameters.Length == 1 && parameters[0].Type.SpecialType == type;

        private static bool IsStringComparison(ITypeSymbol type) =>
            IsSystemType(type, "StringComparison");

        private static bool IsSystemType(ITypeSymbol type, string name) =>
            type.Name == name && type.ContainingNamespace?.ToDisplayString() == "System";
    }
}
