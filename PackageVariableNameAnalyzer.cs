using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0001: flags variables named <c>package</c>. The C#-to-Java converter treats
    /// <c>package</c> as a reserved word, so such a name cannot be converted.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PackageVariableNameAnalyzer : DiagnosticAnalyzer
    {
        internal const string ForbiddenName = "package";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.PackageVariableName,
            title: "Variable named 'package' cannot be converted to Java",
            messageFormat: "Rename '{0}': 'package' is a reserved word in the Java converter",
            category: Categories.Naming,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Our code converts to Java, where 'package' is a reserved word. A variable named 'package' cannot be converted.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

            context.RegisterSyntaxNodeAction(AnalyzeVariableDeclarator, SyntaxKind.VariableDeclarator);
            context.RegisterSyntaxNodeAction(AnalyzeForEach, SyntaxKind.ForEachStatement);
            context.RegisterSyntaxNodeAction(AnalyzeParameter, SyntaxKind.Parameter);
            context.RegisterSyntaxNodeAction(AnalyzeSingleVariableDesignation, SyntaxKind.SingleVariableDesignation);
        }

        private static void AnalyzeVariableDeclarator(SyntaxNodeAnalysisContext context)
        {
            var declarator = (VariableDeclaratorSyntax)context.Node;
            Report(context, declarator.Identifier);
        }

        private static void AnalyzeForEach(SyntaxNodeAnalysisContext context)
        {
            var forEach = (ForEachStatementSyntax)context.Node;
            Report(context, forEach.Identifier);
        }

        private static void AnalyzeParameter(SyntaxNodeAnalysisContext context)
        {
            var parameter = (ParameterSyntax)context.Node;
            if (parameter.Identifier.IsKind(SyntaxKind.None))
                return;
            Report(context, parameter.Identifier);
        }

        private static void AnalyzeSingleVariableDesignation(SyntaxNodeAnalysisContext context)
        {
            var designation = (SingleVariableDesignationSyntax)context.Node;
            Report(context, designation.Identifier);
        }

        private static void Report(SyntaxNodeAnalysisContext context, SyntaxToken identifier)
        {
            if (identifier.ValueText == ForbiddenName)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, identifier.GetLocation(), identifier.ValueText));
            }
        }
    }
}
