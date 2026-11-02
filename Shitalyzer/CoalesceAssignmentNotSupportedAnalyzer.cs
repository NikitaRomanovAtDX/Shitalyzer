using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0010: flags the null-coalescing assignment <c>x ??= y</c>. The C#-to-Java converter has no
    /// translation for it and copies the expression into the Java output verbatim, which javac rejects
    /// ("illegal start of expression"). Measured on dxvcs PR #53184: <c>return chartData ??= new ...;</c>
    /// broke the Java build. Write <c>if(x == null) x = y;</c> instead.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class CoalesceAssignmentNotSupportedAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.CoalesceAssignmentNotSupported,
            title: "Null-coalescing assignment cannot be converted to Java",
            messageFormat: "'{0} ??= ...' is copied into Java verbatim and does not compile; write 'if({0} == null) {0} = ...;' instead",
            category: Categories.Conversion,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Our code converts to Java, and the converter has no translation for the '??=' operator: it emits the C# expression unchanged and javac fails. Replace it with an explicit null check and assignment.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(AnalyzeCoalesceAssignment, SyntaxKind.CoalesceAssignmentExpression);
        }

        private static void AnalyzeCoalesceAssignment(SyntaxNodeAnalysisContext context)
        {
            var assignment = (AssignmentExpressionSyntax)context.Node;
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, assignment.OperatorToken.GetLocation(), assignment.Left.ToString()));
        }
    }
}
