using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0007: flags argument validation written by hand — a BCL throw helper
    /// (<c>ArgumentNullException.ThrowIfNull</c>, <c>ArgumentException.ThrowIfNullOrEmpty</c>,
    /// <c>ArgumentOutOfRangeException.ThrowIf*</c>), an <c>if (…) throw new Argument*Exception(…);</c>
    /// statement, or <c>x ?? throw new ArgumentNullException(nameof(x))</c> — and points at the
    /// <c>Guard</c> helper instead. Beyond consistency, the throw helpers are also .NET 6+ only, so they
    /// do not compile on the .NET Framework 4.7.2 leg.
    /// <para>
    /// The rule only runs when the compilation actually has a <c>Guard</c> class
    /// (<c>DevExpress.Utils.Guard</c>, or <c>DevExpress.Mvvm.Native.GuardHelper</c> in MVVM builds),
    /// because that is the class both the message and the fix point at.
    /// </para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ManualArgumentValidationAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.ManualArgumentValidation,
            title: "Use Guard for argument validation",
            messageFormat: "Use '{0}' instead of validating the argument by hand",
            category: Categories.Usage,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Argument validation goes through the Guard helper so that every check throws the same way. Guard also works on both target frameworks, unlike the ArgumentNullException.ThrowIfNull family, which does not exist in .NET Framework 4.7.2.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(compilationStart =>
            {
                var guardType = GuardValidation.FindGuardType(compilationStart.Compilation);
                if (guardType is null)
                    return;

                compilationStart.RegisterSyntaxNodeAction(
                    nodeContext => Analyze(nodeContext, guardType),
                    SyntaxKind.InvocationExpression,
                    SyntaxKind.IfStatement,
                    SyntaxKind.CoalesceExpression);
            });
        }

        private static void Analyze(SyntaxNodeAnalysisContext context, INamedTypeSymbol guardType)
        {
            // Guard's own implementation is the one place these checks belong.
            if (IsInsideGuard(context.ContainingSymbol, guardType))
                return;

            var rewrite = GuardValidation.Match(context.Node, context.SemanticModel, context.CancellationToken);
            if (rewrite is null)
                return;

            var display = rewrite.MethodName is null ? guardType.Name : guardType.Name + "." + rewrite.MethodName;

            context.ReportDiagnostic(Diagnostic.Create(Rule, GuardValidation.GetLocation(context.Node), display));
        }

        private static bool IsInsideGuard(ISymbol? containingSymbol, INamedTypeSymbol guardType)
        {
            for (var type = containingSymbol as INamedTypeSymbol ?? containingSymbol?.ContainingType; type is not null; type = type.ContainingType)
            {
                if (SymbolEqualityComparer.Default.Equals(type, guardType))
                    return true;
            }

            return false;
        }
    }
}
