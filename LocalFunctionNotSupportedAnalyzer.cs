using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0005: flags local functions (methods declared inside another method, constructor, accessor,
    /// lambda, or local function). Java has no concept of a method nested inside another method, so the
    /// C#-to-Java converter cannot translate them; the local function must be lifted out to a member of
    /// the containing type.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class LocalFunctionNotSupportedAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.LocalFunctionNotSupported,
            title: "Local functions cannot be converted to Java",
            messageFormat: "Local function '{0}' has no Java equivalent; move it to a method",
            category: Categories.Conversion,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Our code converts to Java, which has no equivalent of a method declared inside another method. Move the local function to a method of the containing type.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(AnalyzeLocalFunction, SyntaxKind.LocalFunctionStatement);
        }

        private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext context)
        {
            var localFunction = (LocalFunctionStatementSyntax)context.Node;
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, localFunction.Identifier.GetLocation(), localFunction.Identifier.ValueText));
        }
    }
}
