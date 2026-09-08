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
    /// Replaces hand-written argument validation with the equivalent <c>Guard</c> call:
    /// <list type="bullet">
    /// <item><c>ArgumentNullException.ThrowIfNull(x)</c> → <c>Guard.ArgumentNotNull(x, nameof(x))</c></item>
    /// <item><c>if (x == null) throw new ArgumentNullException(nameof(x));</c> → <c>Guard.ArgumentNotNull(x, nameof(x));</c></item>
    /// <item><c>if (i &lt; 0) throw new ArgumentOutOfRangeException(nameof(i));</c> → <c>Guard.ArgumentNonNegative(i, nameof(i));</c></item>
    /// <item><c>if (i &lt; 0 || i &gt;= list.Count) throw …;</c> → <c>Guard.ArgumentIsInRange(list, i, nameof(i));</c></item>
    /// <item><c>if (!IsValid(x)) throw new ArgumentException();</c> → <c>Guard.ArgumentMatch(x, nameof(x), v =&gt; IsValid(v));</c></item>
    /// <item><c>this.x = x ?? throw new ArgumentNullException(nameof(x));</c> →
    /// <c>Guard.ArgumentNotNull(x, nameof(x)); this.x = x;</c></item>
    /// </list>
    /// The exception the check throws is Guard's, so a custom message on the original exception is dropped
    /// and an <c>ArgumentOutOfRangeException</c> becomes the <c>ArgumentException</c> Guard throws — that
    /// normalization is the point of the rule. Validation Guard has no member for (and cases where moving
    /// the check would change when it runs) is reported without a fix; see <see cref="GuardValidation"/>.
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

            var guardType = GuardValidation.FindGuardType(semanticModel.Compilation);
            if (guardType is null)
                return;

            var diagnostic = context.Diagnostics[0];

            // The diagnostic sits on the validation itself (a helper name, an 'if (…)' header, a throw
            // expression); walk out to the node the matcher recognizes.
            SyntaxNode? target = null;
            GuardRewrite? rewrite = null;
            foreach (var candidate in root.FindNode(diagnostic.Location.SourceSpan).AncestorsAndSelf())
            {
                if (GuardValidation.Match(candidate, semanticModel, context.CancellationToken) is not { } match)
                    continue;

                target = candidate;
                rewrite = match;
                break;
            }

            if (target is null || rewrite?.MethodName is null)
                return;

            var guardCall = BuildGuardCall(guardType, rewrite, semanticModel, target.SpanStart);
            var title = $"Use '{guardType.Name}.{rewrite.MethodName}'";

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: title,
                    createChangedDocument: ct => ApplyAsync(context.Document, target, guardCall, ct),
                    // One key for every mapping, so "Fix all occurrences" converts a whole file's worth of
                    // hand-written checks in a single pass.
                    equivalenceKey: DiagnosticIds.ManualArgumentValidation),
                diagnostic);
        }

        private static async Task<Document> ApplyAsync(
            Document document, SyntaxNode target, InvocationExpressionSyntax guardCall, CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);

            switch (target)
            {
                // ArgumentNullException.ThrowIfNull(x) -> Guard.ArgumentNotNull(x, nameof(x))
                case InvocationExpressionSyntax invocation:
                    editor.ReplaceNode(invocation, guardCall.WithTriviaFrom(invocation).WithAdditionalAnnotations(Formatter.Annotation));
                    break;

                // if (x == null) throw ...; -> Guard.ArgumentNotNull(x, nameof(x));
                case IfStatementSyntax ifStatement:
                    editor.ReplaceNode(
                        ifStatement,
                        ExpressionStatement(guardCall).WithTriviaFrom(ifStatement).WithAdditionalAnnotations(Formatter.Annotation));
                    break;

                // x ?? throw ... -> a Guard call on its own line, with the value left in place.
                case BinaryExpressionSyntax coalesce:
                    var statement = GuardValidation.TryGetHoistTarget(coalesce);
                    if (statement is null)
                        return document;

                    editor.InsertBefore(statement, ExpressionStatement(guardCall).WithAdditionalAnnotations(Formatter.Annotation));
                    editor.ReplaceNode(coalesce, coalesce.Left.WithTriviaFrom(coalesce));
                    break;

                default:
                    return document;
            }

            return editor.GetChangedDocument();
        }

        /// <summary>
        /// Builds the <c>Guard.Xxx(…)</c> call. The class is named as briefly as it can be at this position:
        /// just <c>Guard</c> where its namespace is imported, fully qualified where it is not.
        /// </summary>
        private static InvocationExpressionSyntax BuildGuardCall(
            INamedTypeSymbol guardType, GuardRewrite rewrite, SemanticModel semanticModel, int position)
        {
            var guardName = ParseName(guardType.ToMinimalDisplayString(semanticModel, position));

            return InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, guardName, IdentifierName(rewrite.MethodName!)))
                .WithArgumentList(ArgumentList(SeparatedList(rewrite.Arguments.Select(argument => Argument(argument)))));
        }
    }
}
