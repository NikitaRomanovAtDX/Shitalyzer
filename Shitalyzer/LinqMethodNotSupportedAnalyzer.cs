using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0009: flags a <c>System.Linq.Enumerable</c> method, or an overload of one, that the CS2J Java runtime
    /// does not implement, so the call cannot be converted. Measured against <c>devexpress-bcl-system 26.2.0.128</c>
    /// (<c>EnumerableExtensions</c>, <c>OrderedEnumerableExtensions</c> and <c>com.devexpress.system.linq.Enumerable</c>):
    /// the converted code in dxvcs avoids every one of them, e.g. <c>PdfBrickFactory.GetPageOrDefault</c> is
    /// <c>[JavaNotImplemented]</c> because of its <c>ElementAtOrDefault</c>. <c>Sum</c>/<c>Max</c> are present under typed
    /// names (<c>sumInt</c>, <c>maxDouble</c>, ...) and are not flagged.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class LinqMethodNotSupportedAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.LinqMethodNotSupported,
            title: "LINQ method is not implemented by the Java runtime",
            messageFormat: "'{0}' has no counterpart in the CS2J Java runtime and cannot be converted; use a supported LINQ method or a loop",
            category: Categories.Conversion,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The CS2J runtime implements only part of System.Linq.Enumerable. This method, or this overload of it, is missing there, so the converted Java does not compile.");

        private static readonly ImmutableHashSet<string> MissingMethods = ImmutableHashSet.Create(
            "AggregateBy", "Append", "Chunk", "CountBy", "DefaultIfEmpty", "DistinctBy", "ElementAtOrDefault", "ExceptBy",
            "GroupJoin", "Index", "InfiniteSequence", "Intersect", "IntersectBy", "Join", "LeftJoin", "LongCount", "MaxBy",
            "MinBy", "Order", "OrderDescending", "Prepend", "RightJoin", "Sequence", "Shuffle", "SkipLast", "SkipWhile",
            "TakeLast", "TakeWhile", "ToHashSet", "ToLookup", "TryGetNonEnumeratedCount", "UnionBy", "Zip");

        private static readonly ImmutableDictionary<string, ImmutableHashSet<int>> MissingParameterCounts =
            ImmutableDictionary.CreateRange(new[]
            {
                Pair("Average", 1),
                Pair("FirstOrDefault", 3),
                Pair("LastOrDefault", 3),
                Pair("SingleOrDefault", 3),
                Pair("GroupBy", 4, 5),
                Pair("SelectMany", 3),
                Pair("ToDictionary", 1),
            });

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
            var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
            if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable")
                return;

            var missing = MissingMethods.Contains(method.Name)
                || MissingParameterCounts.TryGetValue(method.Name, out var counts) && counts.Contains(method.Parameters.Length);
            if (!missing)
                return;

            var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess }
                ? memberAccess.Name.GetLocation()
                : invocation.Syntax.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, "Enumerable." + method.Name + Signature(method)));
        }

        private static string Signature(IMethodSymbol method) =>
            "(" + string.Join(", ", method.Parameters.Select(parameter => parameter.Name)) + ")";

        private static KeyValuePair<string, ImmutableHashSet<int>> Pair(string name, params int[] parameterCounts) =>
            new KeyValuePair<string, ImmutableHashSet<int>>(name, ImmutableHashSet.Create(parameterCounts));
    }
}
