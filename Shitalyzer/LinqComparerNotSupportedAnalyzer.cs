using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0008: flags a <c>System.Linq.Enumerable</c> call that passes an <c>IEqualityComparer&lt;T&gt;</c> or
    /// <c>IComparer&lt;T&gt;</c> where the CS2J Java runtime has no overload taking one. The converted call then
    /// compares by the element's own <c>equals</c>/<c>hashCode</c> or <c>Comparable</c>, so a case-insensitive
    /// or otherwise custom comparison is lost without any error. Measured against
    /// <c>devexpress-bcl-system 26.2.0.128</c> (<c>EnumerableExtensions</c> and <c>com.devexpress.system.linq.Enumerable</c>):
    /// <c>distinct</c> has only <c>(source)</c>, and only the
    /// methods in <see cref="SupportedWithComparer"/> (plus <c>Contains</c> over strings with a
    /// <c>StringComparer</c>) accept a comparer. On an array, .NET 10 binds <c>Contains</c>/<c>SequenceEqual</c>
    /// with a comparer to the span overloads of <c>System.MemoryExtensions</c>, which are checked the same way.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class LinqComparerNotSupportedAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.LinqComparerNotSupported,
            title: "LINQ comparer overload is not supported by the Java runtime",
            messageFormat: "'{0}' with a comparer has no Java counterpart; the converted call ignores the comparer and compares by the element's own equality",
            category: Categories.Conversion,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The CS2J runtime implements this Enumerable method without a comparer parameter, so the Java output silently drops the comparer. Compare by hand (a loop with comparer.Equals) or use a method whose comparer overload the runtime supports.");

        private static readonly ImmutableHashSet<string> SupportedWithComparer = ImmutableHashSet.Create(
            "GroupBy", "OrderBy", "OrderByDescending", "SequenceEqual", "ThenBy", "ThenByDescending", "ToDictionary");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var method = invocation.TargetMethod;
            var containingType = method.ContainingType?.ToDisplayString();
            if (containingType != "System.Linq.Enumerable" && containingType != "System.MemoryExtensions")
                return;

            var comparer = invocation.Arguments.FirstOrDefault(argument =>
                argument.Parameter is not null && IsComparerType(argument.Parameter.Type));
            if (comparer is null || comparer.ArgumentKind == ArgumentKind.DefaultValue)
                return;
            if (containingType == "System.Linq.Enumerable" && SupportedWithComparer.Contains(method.Name))
                return;
            if (method.Name == "Contains" && IsStringContainsWithStringComparer(method, comparer))
                return;

            var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess }
                ? memberAccess.Name.GetLocation()
                : invocation.Syntax.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, method.ContainingType!.Name + "." + method.Name));
        }

        private static bool IsComparerType(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol { IsGenericType: true } named)
                return false;
            var definition = named.OriginalDefinition.ToDisplayString();
            return definition == "System.Collections.Generic.IEqualityComparer<T>"
                || definition == "System.Collections.Generic.IComparer<T>";
        }

        private static bool IsStringContainsWithStringComparer(IMethodSymbol method, IArgumentOperation comparer)
        {
            if (method.TypeArguments.Length != 1 || method.TypeArguments[0].SpecialType != SpecialType.System_String)
                return false;
            var value = comparer.Value;
            while (value is IConversionOperation { IsImplicit: true } conversion)
                value = conversion.Operand;
            for (var type = value.Type; type is not null; type = type.BaseType)
            {
                if (type.ToDisplayString() == "System.StringComparer")
                    return true;
            }
            return false;
        }
    }
}
