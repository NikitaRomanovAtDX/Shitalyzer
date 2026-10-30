using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Shitalyzer
{
    /// <summary>
    /// Rewrites an NUnit constraint member the Java shim lacks onto one it has, where the rewrite is mechanical:
    /// <code>
    /// Is.Positive                 // becomes Is.GreaterThan(0)
    /// Is.Negative                 // becomes Is.LessThan(0)
    /// Is.Not.Positive             // becomes Is.Not.GreaterThan(0)
    /// Has.Count.AtLeast(n)        // becomes Has.Count.GreaterThanOrEqualTo(n)
    /// Has.Count.AtMost(n)         // becomes Has.Count.LessThanOrEqualTo(n)
    /// </code>
    /// The analyzer decides which diagnostics are fixable and passes the replacement in the diagnostic's properties.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NUnitShimMemberNotSupportedCodeFixProvider)), Shared]
    public sealed class NUnitShimMemberNotSupportedCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(DiagnosticIds.NUnitShimMemberNotSupported);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics[0];
            if (!diagnostic.Properties.TryGetValue(NUnitShimMemberNotSupportedAnalyzer.ReplacementKey, out var replacement) || replacement is null)
                return;
            var addZeroArgument = diagnostic.Properties.TryGetValue(NUnitShimMemberNotSupportedAnalyzer.AddZeroArgumentKey, out var flag) && flag == "true";

            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not SimpleNameSyntax name
                || name.Parent is not MemberAccessExpressionSyntax memberAccess)
                return;

            var newName = IdentifierName(Identifier(replacement)).WithTriviaFrom(name);
            ExpressionSyntax oldNode = memberAccess;
            ExpressionSyntax newNode = memberAccess.WithName(newName);
            if (addZeroArgument)
                newNode = InvocationExpression(memberAccess.WithName(newName.WithoutTrailingTrivia()), ArgumentList(SingletonSeparatedList(
                    Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))))))
                    .WithTrailingTrivia(name.GetTrailingTrivia());

            var title = addZeroArgument ? $"Replace with '{replacement}(0)'" : $"Replace with '{replacement}'";
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: title,
                    createChangedDocument: _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(oldNode, newNode))),
                    equivalenceKey: DiagnosticIds.NUnitShimMemberNotSupported),
                diagnostic);
        }
    }
}
