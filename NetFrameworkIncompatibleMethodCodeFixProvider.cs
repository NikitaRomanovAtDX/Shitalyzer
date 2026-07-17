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
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Shitalyzer
{
    /// <summary>
    /// Rewrites .NET Core-only <see cref="string"/> overloads into equivalents that compile on
    /// .NET Framework 4.7.2:
    /// <list type="bullet">
    /// <item><c>s.StartsWith('a')</c> → <c>(s.Length != 0 &amp;&amp; s[0] == 'a')</c></item>
    /// <item><c>s.EndsWith('a')</c> → <c>(s.Length != 0 &amp;&amp; s[s.Length - 1] == 'a')</c></item>
    /// <item><c>s.Contains('a')</c> → <c>(s.IndexOf('a') >= 0)</c></item>
    /// <item><c>Math.Clamp(v, lo, hi)</c> → <c>Math.Min(Math.Max(v, lo), hi)</c></item>
    /// </list>
    /// The fix is only offered when the receiver is side-effect free (no nested invocations).
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NetFrameworkIncompatibleMethodCodeFixProvider)), Shared]
    public sealed class NetFrameworkIncompatibleMethodCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(DiagnosticIds.NetFrameworkIncompatibleMethod);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null)
                return;

            var diagnostic = context.Diagnostics[0];

            if (!diagnostic.Properties.TryGetValue(NetFrameworkIncompatibleMethodAnalyzer.OverloadKindKey, out var overloadKind)
                || overloadKind is null)
            {
                return; // No automatic rewrite for this overload (e.g. StringComparison variants).
            }

            var node = root.FindNode(diagnostic.Location.SourceSpan);
            var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (invocation is null || invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                return;

            if (overloadKind == NetFrameworkIncompatibleMethodAnalyzer.MathClamp)
            {
                RegisterMathClampFix(context, invocation, diagnostic);
                return;
            }

            if (invocation.ArgumentList.Arguments.Count != 1)
                return;

            var receiver = memberAccess.Expression;
            var argument = invocation.ArgumentList.Arguments[0].Expression;

            // Duplicating the receiver is only safe if evaluating it has no side effects.
            if (receiver.DescendantNodesAndSelf().Any(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or AwaitExpressionSyntax))
                return;

            var replacement = BuildReplacement(overloadKind, receiver, argument);
            if (replacement is null)
                return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Use a .NET Framework-compatible expression",
                    createChangedDocument: ct => ReplaceAsync(context.Document, invocation, replacement, ct),
                    equivalenceKey: DiagnosticIds.NetFrameworkIncompatibleMethod + ":" + overloadKind),
                diagnostic);
        }

        private static void RegisterMathClampFix(CodeFixContext context, InvocationExpressionSyntax invocation, Diagnostic diagnostic)
        {
            var arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count != 3)
                return;

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                return;

            // Preserve however 'Math' was referenced (Math / System.Math).
            var mathReceiver = memberAccess.Expression.WithoutTrivia();
            var value = arguments[0].Expression.WithoutTrivia();
            var min = arguments[1].Expression.WithoutTrivia();
            var max = arguments[2].Expression.WithoutTrivia();

            // Math.Min(Math.Max(value, min), max)
            var maxCall = MathCall(mathReceiver, "Max", value, min);
            var replacement = MathCall(mathReceiver, "Min", maxCall, max);

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Use Math.Min/Math.Max instead of Math.Clamp",
                    createChangedDocument: ct => ReplaceAsync(context.Document, invocation, replacement, ct),
                    equivalenceKey: DiagnosticIds.NetFrameworkIncompatibleMethod + ":" + NetFrameworkIncompatibleMethodAnalyzer.MathClamp),
                diagnostic);
        }

        private static ExpressionSyntax MathCall(ExpressionSyntax mathReceiver, string method, ExpressionSyntax arg0, ExpressionSyntax arg1) =>
            InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, mathReceiver, IdentifierName(method)),
                ArgumentList(SeparatedList(new[] { Argument(arg0), Argument(arg1) })));

        private static ExpressionSyntax? BuildReplacement(string overloadKind, ExpressionSyntax receiver, ExpressionSyntax argument)
        {
            var bareReceiver = receiver.WithoutTrivia();

            switch (overloadKind)
            {
                case NetFrameworkIncompatibleMethodAnalyzer.StartsWithChar:
                    // receiver.Length != 0 && receiver[0] == argument
                    return Parenthesize(
                        BinaryExpression(
                            SyntaxKind.LogicalAndExpression,
                            LengthNotZero(bareReceiver),
                            CharEquals(ElementAt(bareReceiver, LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))), argument)));

                case NetFrameworkIncompatibleMethodAnalyzer.EndsWithChar:
                    // receiver.Length != 0 && receiver[receiver.Length - 1] == argument
                    return Parenthesize(
                        BinaryExpression(
                            SyntaxKind.LogicalAndExpression,
                            LengthNotZero(bareReceiver),
                            CharEquals(ElementAt(bareReceiver, LastIndex(bareReceiver)), argument)));

                case NetFrameworkIncompatibleMethodAnalyzer.ContainsChar:
                    // receiver.IndexOf(argument) >= 0
                    return Parenthesize(
                        BinaryExpression(
                            SyntaxKind.GreaterThanOrEqualExpression,
                            InvocationExpression(
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, bareReceiver, IdentifierName("IndexOf")),
                                ArgumentList(SingletonSeparatedList(Argument(argument.WithoutTrivia())))),
                            LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))));

                default:
                    return null;
            }
        }

        private static ExpressionSyntax LengthNotZero(ExpressionSyntax receiver) =>
            BinaryExpression(
                SyntaxKind.NotEqualsExpression,
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, IdentifierName("Length")),
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0)));

        private static ExpressionSyntax LastIndex(ExpressionSyntax receiver) =>
            BinaryExpression(
                SyntaxKind.SubtractExpression,
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, IdentifierName("Length")),
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1)));

        private static ExpressionSyntax ElementAt(ExpressionSyntax receiver, ExpressionSyntax index) =>
            ElementAccessExpression(receiver, BracketedArgumentList(SingletonSeparatedList(Argument(index))));

        private static ExpressionSyntax CharEquals(ExpressionSyntax left, ExpressionSyntax right) =>
            BinaryExpression(SyntaxKind.EqualsExpression, left, right.WithoutTrivia());

        private static ExpressionSyntax Parenthesize(ExpressionSyntax expression) =>
            ParenthesizedExpression(expression);

        private static Task<Document> ReplaceAsync(Document document, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken) =>
            ReplaceCoreAsync(document, oldNode, newNode, cancellationToken);

        private static async Task<Document> ReplaceCoreAsync(Document document, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
                return document;

            var newRoot = root.ReplaceNode(oldNode, newNode.WithTriviaFrom(oldNode));
            return document.WithSyntaxRoot(newRoot);
        }
    }
}
