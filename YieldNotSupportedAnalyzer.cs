using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0004: flags iterator methods and local functions that use <c>yield return</c>/
    /// <c>yield break</c>. The C#-to-Java converter has no equivalent of C# iterator state machines,
    /// so the body must be rewritten to build and return a <c>List&lt;T&gt;</c>.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class YieldNotSupportedAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.YieldNotSupported,
            title: "'yield' cannot be converted to Java",
            messageFormat: "'{0}' uses 'yield'; build and return a List<T> instead",
            category: Categories.Conversion,
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "Our code converts to Java, which has no equivalent of C# iterator methods. Replace 'yield return'/'yield break' by populating a List<T> and returning it.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeLocalFunction, SyntaxKind.LocalFunctionStatement);
        }

        private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
        {
            var method = (MethodDeclarationSyntax)context.Node;
            if (method.Body is null || !ContainsBelongingYield(method.Body))
                return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), method.Identifier.ValueText));
        }

        private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext context)
        {
            var localFunction = (LocalFunctionStatementSyntax)context.Node;
            if (localFunction.Body is null || !ContainsBelongingYield(localFunction.Body))
                return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, localFunction.Identifier.GetLocation(), localFunction.Identifier.ValueText));
        }

        /// <summary>
        /// True if <paramref name="body"/> directly contains a yield statement — i.e. one that is not
        /// nested inside a lambda or local function, which would define its own separate iterator.
        /// </summary>
        internal static bool ContainsBelongingYield(SyntaxNode body) =>
            body.DescendantNodes(descendIntoChildren: n => n == body || !IsNestedFunction(n))
                .Any(n => n is YieldStatementSyntax);

        private static bool IsNestedFunction(SyntaxNode node) =>
            node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;
    }
}
