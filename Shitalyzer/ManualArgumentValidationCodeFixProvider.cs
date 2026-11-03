using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Shitalyzer
{
    /// <summary>
    /// Replaces hand-written argument validation with the equivalent helper call:
    /// <list type="bullet">
    /// <item><c>ArgumentNullException.ThrowIfNull(x)</c> → <c>Guard.ArgumentNotNull(x, nameof(x))</c></item>
    /// <item><c>if (x == null) throw new ArgumentNullException(nameof(x));</c> → <c>Guard.ArgumentNotNull(x, nameof(x));</c></item>
    /// <item><c>if (i &lt; 0) throw new ArgumentOutOfRangeException(nameof(i));</c> → <c>Guard.ArgumentNonNegative(i, nameof(i));</c>
    /// or <c>ValueChecker.CheckMinValue(i, 0, nameof(i));</c></item>
    /// <item><c>if (i &lt; 0 || i &gt;= list.Count) throw …;</c> → <c>Guard.ArgumentIsInRange(list, i, nameof(i));</c>
    /// or <c>ValueChecker.CheckValue(i, 0, list.Count - 1, nameof(i));</c></item>
    /// <item><c>if (!string.IsNullOrEmpty(s) &amp;&amp; s.Length &gt; max) throw …;</c> → <c>ValueChecker.CheckLength(s, max, nameof(s));</c></item>
    /// <item><c>if (!IsValid(x)) throw new ArgumentException();</c> → <c>Guard.ArgumentMatch(x, nameof(x), v =&gt; IsValid(v));</c></item>
    /// <item><c>this.x = x ?? throw new ArgumentNullException(nameof(x));</c> →
    /// <c>Guard.ArgumentNotNull(x, nameof(x)); this.x = x;</c></item>
    /// </list>
    /// One action is offered per helper that can take the check (<c>ValueChecker</c> only in Office code),
    /// the one that keeps the original exception type first. The exception the check throws becomes the
    /// helper's, so a custom message on the original exception is dropped, and so is its type where the
    /// helper throws another — the analyzer reports those cases silently. Validation no helper has a member
    /// for (and cases where moving the check would change when it runs) is not reported in the first place;
    /// see <see cref="GuardValidation"/>.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ManualArgumentValidationCodeFixProvider)), Shared]
    public sealed class ManualArgumentValidationCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(DiagnosticIds.ManualArgumentValidation);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
                return;

            var helpers = ValidationHelpers.Find(semanticModel.Compilation);
            if (helpers is null)
                return;

            var diagnostic = context.Diagnostics[0];

            // The diagnostic sits on the validation itself (a helper name, an 'if (…)' header, a throw
            // expression); walk out to the node the matcher recognizes.
            SyntaxNode? target = null;
            ValidationMatch? match = null;
            foreach (var candidate in root.FindNode(diagnostic.Location.SourceSpan).AncestorsAndSelf())
            {
                match = GuardValidation.Match(candidate, helpers, semanticModel, context.CancellationToken);
                if (match is not null)
                {
                    target = candidate;
                    break;
                }
            }

            if (target is null || match is null)
                return;

            foreach (var rewrite in match.Rewrites)
            {
                var helperType = helpers.Get(rewrite.Helper);
                var call = BuildHelperCall(helperType, rewrite, semanticModel, target.SpanStart);

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Use '{helperType.Name}.{rewrite.MethodName}'",
                        createChangedDocument: ct => ApplyAsync(context.Document, target, call, ct),
                        // One key per helper, so "Fix all occurrences" converts a whole file's worth of
                        // hand-written checks to that helper in a single pass.
                        equivalenceKey: EquivalenceKey(rewrite.Helper)),
                    diagnostic);
            }
        }

        internal static string EquivalenceKey(ValidationHelper helper) => DiagnosticIds.ManualArgumentValidation + ":" + helper;

        private static async Task<Document> ApplyAsync(
            Document document, SyntaxNode target, InvocationExpressionSyntax helperCall, CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);

            switch (target)
            {
                // ArgumentNullException.ThrowIfNull(x) -> Guard.ArgumentNotNull(x, nameof(x))
                case InvocationExpressionSyntax invocation:
                    editor.ReplaceNode(invocation, helperCall.WithTriviaFrom(invocation).WithAdditionalAnnotations(Formatter.Annotation));
                    break;

                // if (x == null) throw ...; -> Guard.ArgumentNotNull(x, nameof(x));
                case IfStatementSyntax ifStatement:
                    editor.ReplaceNode(
                        ifStatement,
                        ExpressionStatement(helperCall).WithTriviaFrom(ifStatement).WithAdditionalAnnotations(Formatter.Annotation));
                    break;

                // x ?? throw ... -> a Guard call on its own line, with the value left in place.
                case BinaryExpressionSyntax coalesce:
                    var statement = GuardValidation.TryGetHoistTarget(coalesce);
                    if (statement is null)
                        return document;

                    editor.InsertBefore(statement, ExpressionStatement(helperCall).WithAdditionalAnnotations(Formatter.Annotation));
                    editor.ReplaceNode(coalesce, coalesce.Left.WithTriviaFrom(coalesce));
                    break;

                default:
                    return document;
            }

            return editor.GetChangedDocument();
        }

        /// <summary>
        /// Builds the <c>Guard.Xxx(…)</c> / <c>ValueChecker.Xxx(…)</c> call. The class is named as briefly as
        /// it can be at this position: just its name where its namespace is imported, fully qualified where it is not.
        /// </summary>
        private static InvocationExpressionSyntax BuildHelperCall(
            INamedTypeSymbol helperType, HelperRewrite rewrite, SemanticModel semanticModel, int position)
        {
            var helperName = ParseExpression(helperType.ToMinimalDisplayString(semanticModel, position));

            return InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, helperName, IdentifierName(rewrite.MethodName)))
                .WithArgumentList(ArgumentList(SeparatedList(rewrite.Arguments.Select(argument => Argument(argument)))));
        }
    }
}
