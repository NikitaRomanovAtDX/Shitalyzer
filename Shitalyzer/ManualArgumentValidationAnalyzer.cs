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
    /// statement, or <c>x ?? throw new ArgumentNullException(nameof(x))</c> — and points at a validation
    /// helper instead: <c>Guard</c>, or in Office code also <c>DevExpress.Office.Utils.ValueChecker</c>.
    /// Beyond consistency, the throw helpers are also .NET 6+ only, so they do not compile on the
    /// .NET Framework 4.7.2 leg.
    /// <para>
    /// The rule only runs when the compilation actually has one of the helpers
    /// (<c>DevExpress.Utils.Guard</c>, <c>DevExpress.Mvvm.Native.GuardHelper</c> in MVVM builds, or
    /// <c>ValueChecker</c>), because that is what both the message and the fix point at. Every diagnostic
    /// it reports comes with a fix: validation no helper has a member for — a condition over two
    /// parameters, say — is left alone rather than warned about.
    /// </para>
    /// <para>
    /// A check whose replacement would change what the caller sees is reported at
    /// <see cref="DiagnosticSeverity.Hidden"/> instead of as a warning: one that throws with its own
    /// message — <c>throw new ArgumentException(Msg.InvalidRangeOwner, nameof(range))</c> — since the
    /// helpers throw messages of their own making, and one that throws a different exception type than the
    /// best helper does — <c>throw new ArgumentOutOfRangeException(…)</c> where only <c>Guard</c>, which
    /// throws <c>ArgumentException</c>, can take it. The fix is still offered, the warning is not. The BCL
    /// throw helpers stay warnings regardless, because they break the .NET Framework build.
    /// </para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ManualArgumentValidationAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.ManualArgumentValidation,
            title: "Use Guard or ValueChecker for argument validation",
            messageFormat: "Use '{0}' instead of validating the argument by hand",
            category: Categories.Usage,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Argument validation goes through the Guard helper (or ValueChecker in Office code) so that every check throws the same way. The helpers also work on both target frameworks, unlike the ArgumentNullException.ThrowIfNull family, which does not exist in .NET Framework 4.7.2. A check that would lose its own message or exception type is reported silently.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(compilationStart =>
            {
                var helpers = ValidationHelpers.Find(compilationStart.Compilation);
                if (helpers is null)
                    return;

                compilationStart.RegisterSyntaxNodeAction(
                    nodeContext => Analyze(nodeContext, helpers),
                    SyntaxKind.InvocationExpression,
                    SyntaxKind.IfStatement,
                    SyntaxKind.CoalesceExpression);
            });
        }

        private static void Analyze(SyntaxNodeAnalysisContext context, ValidationHelpers helpers)
        {
            // The helpers' own implementations are the one place these checks belong.
            if (IsInsideHelper(context.ContainingSymbol, helpers))
                return;

            // Only validation that maps onto a helper member is reported: a check no helper can express
            // leaves the developer with a warning and nothing to do about it.
            var match = GuardValidation.Match(context.Node, helpers, context.SemanticModel, context.CancellationToken);
            if (match is null)
                return;

            // Validation that spells out its own message, or throws an exception type the helper would not,
            // is reported silently: the fix stays available in the light bulb, while the squiggle would only
            // be pushing the developer to trade a deliberate choice for the helper's generic one.
            var severity = match.IsWarning ? Rule.DefaultSeverity : DiagnosticSeverity.Hidden;
            var preferred = match.Preferred;

            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                GuardValidation.GetLocation(context.Node),
                severity,
                additionalLocations: null,
                properties: null,
                helpers.Get(preferred.Helper).Name + "." + preferred.MethodName));
        }

        private static bool IsInsideHelper(ISymbol? containingSymbol, ValidationHelpers helpers)
        {
            for (var type = containingSymbol as INamedTypeSymbol ?? containingSymbol?.ContainingType; type is not null; type = type.ContainingType)
            {
                if (helpers.Contains(type))
                    return true;
            }

            return false;
        }
    }
}
