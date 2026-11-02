using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0003: flags value-type local variables that are captured (read or written) inside a lambda
    /// or anonymous method. The C#-to-Java converter cannot translate captured value-type locals, so
    /// they must be wrapped in a reference-type holder.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ValueTypeInLambdaAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.ValueTypeCapturedInLambda,
            title: "Value-type local captured in a lambda cannot be converted to Java",
            messageFormat: "Value-type local '{0}' is captured by a lambda; wrap it in a holder class",
            category: Categories.Conversion,
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "Our code converts to Java. A value-type local variable that is captured by a lambda cannot be compiled by the converter; move it into a reference-type holder class.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement);
        }

        private static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context)
        {
            var localDeclaration = (LocalDeclarationStatementSyntax)context.Node;

            // 'ref'/'ref readonly' locals are already reference-like aliases; skip them.
            if (localDeclaration.Declaration.Type.IsKind(SyntaxKind.RefType))
                return;

            foreach (var declarator in localDeclaration.Declaration.Variables)
            {
                if (context.SemanticModel.GetDeclaredSymbol(declarator, context.CancellationToken) is not ILocalSymbol local)
                    continue;

                var type = local.Type;
                if (type is null || type.TypeKind == TypeKind.Error || !type.IsValueType)
                    continue;

                if (IsCapturedByLambda(context, local, declarator))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, declarator.Identifier.GetLocation(), local.Name));
                }
            }
        }

        private static bool IsCapturedByLambda(SyntaxNodeAnalysisContext context, ILocalSymbol local, VariableDeclaratorSyntax declarator)
        {
            var scope = FindScope(declarator);
            if (scope is null)
                return false;

            foreach (var lambda in scope.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
            {
                // A lambda that lexically contains the declaration is where the variable lives; a
                // reference from inside it is not a cross-boundary capture of an outer local.
                if (lambda.Span.Contains(declarator.Span))
                    continue;

                foreach (var identifier in lambda.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (identifier.Identifier.ValueText != local.Name)
                        continue;

                    var symbol = context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol;
                    if (SymbolEqualityComparer.Default.Equals(symbol, local))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns the nearest construct whose body may contain both the declaration and any lambda
        /// that captures it (a member body, local function, or the enclosing anonymous function).
        /// </summary>
        private static SyntaxNode? FindScope(SyntaxNode declarator) =>
            declarator.Ancestors().FirstOrDefault(a =>
                a is BaseMethodDeclarationSyntax
                    or AccessorDeclarationSyntax
                    or LocalFunctionStatementSyntax
                    or AnonymousFunctionExpressionSyntax
                    or BasePropertyDeclarationSyntax
                    or GlobalStatementSyntax);
    }
}
