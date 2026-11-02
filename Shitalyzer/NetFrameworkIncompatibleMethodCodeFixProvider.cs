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
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
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
    /// <item><c>d.TryAdd(k, v);</c> → <c>if (!d.ContainsKey(k)) d.Add(k, v);</c> (statement form only)</item>
    /// <item><c>d.Remove(k, out v)</c> → <c>(d.TryGetValue(k, out v) &amp;&amp; d.Remove(k))</c></item>
    /// <item><c>d.EnsureCapacity(n);</c> / <c>d.TrimExcess();</c> → removed (they are only capacity hints)</item>
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

            if (overloadKind == NetFrameworkIncompatibleMethodAnalyzer.DictionaryTryAdd)
            {
                RegisterDictionaryTryAddFix(context, invocation, memberAccess, diagnostic);
                return;
            }

            if (overloadKind == NetFrameworkIncompatibleMethodAnalyzer.DictionaryRemoveOut)
            {
                RegisterDictionaryRemoveOutFix(context, invocation, memberAccess, diagnostic);
                return;
            }

            if (overloadKind == NetFrameworkIncompatibleMethodAnalyzer.CapacityHint)
            {
                RegisterCapacityHintFix(context, invocation, memberAccess, diagnostic);
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

        private static void RegisterDictionaryTryAddFix(CodeFixContext context, InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax memberAccess, Diagnostic diagnostic)
        {
            // Only the discarded-result form is rewritten; when the bool result is consumed the
            // surrounding control flow would have to be restructured.
            if (invocation.Parent is not ExpressionStatementSyntax statement)
                return;

            // Two arguments means instance/extension call syntax; the static
            // CollectionExtensions.TryAdd(d, k, v) form is left alone.
            var arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count != 2)
                return;

            var receiver = memberAccess.Expression;
            var key = arguments[0].Expression;

            // Both the receiver and the key are evaluated twice in the rewrite.
            if (HasSideEffects(receiver) || HasSideEffects(key))
                return;

            var bareReceiver = receiver.WithoutTrivia();
            var bareKey = key.WithoutTrivia();
            var value = arguments[1].Expression.WithoutTrivia();

            // if (!receiver.ContainsKey(key)) receiver.Add(key, value);
            var containsKey = InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, bareReceiver, IdentifierName("ContainsKey")),
                ArgumentList(SingletonSeparatedList(Argument(bareKey))));
            var add = ExpressionStatement(
                InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, bareReceiver, IdentifierName("Add")),
                    ArgumentList(SeparatedList(new[] { Argument(bareKey), Argument(value) }))));
            var replacement = AsStatementReplacement(statement, IfStatement(PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, containsKey), add));

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Use ContainsKey/Add instead of TryAdd",
                    createChangedDocument: ct => ReplaceAsync(context.Document, statement, replacement, ct),
                    equivalenceKey: DiagnosticIds.NetFrameworkIncompatibleMethod + ":" + NetFrameworkIncompatibleMethodAnalyzer.DictionaryTryAdd),
                diagnostic);
        }

        private static void RegisterDictionaryRemoveOutFix(CodeFixContext context, InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax memberAccess, Diagnostic diagnostic)
        {
            // Two arguments means instance/extension call syntax; the static
            // CollectionExtensions.Remove(d, k, out v) form is left alone.
            var arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count != 2 || !arguments[1].RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                return;

            var receiver = memberAccess.Expression;
            var key = arguments[0].Expression;

            // Both the receiver and the key are evaluated twice in the rewrite.
            if (HasSideEffects(receiver) || HasSideEffects(key))
                return;

            var bareReceiver = receiver.WithoutTrivia();
            var bareKey = key.WithoutTrivia();

            // TryGetValue always runs, so an 'out var' declared in it is definitely assigned afterwards,
            // exactly as with Remove(k, out v).
            var tryGetValue = InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, bareReceiver, IdentifierName("TryGetValue")),
                ArgumentList(SeparatedList(new[] { Argument(bareKey), arguments[1].WithoutTrivia() })));
            var remove = InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, bareReceiver, IdentifierName("Remove")),
                ArgumentList(SingletonSeparatedList(Argument(bareKey))));

            SyntaxNode oldNode;
            SyntaxNode replacement;
            if (invocation.Parent is ExpressionStatementSyntax statement)
            {
                // 'a && b;' is not a valid statement: if (d.TryGetValue(k, out v)) d.Remove(k);
                oldNode = statement;
                replacement = AsStatementReplacement(statement, IfStatement(tryGetValue, ExpressionStatement(remove)));
            }
            else
            {
                oldNode = invocation;
                replacement = Parenthesize(BinaryExpression(SyntaxKind.LogicalAndExpression, tryGetValue, remove))
                    .WithAdditionalAnnotations(Simplifier.Annotation);
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Use TryGetValue/Remove instead of Remove(key, out value)",
                    createChangedDocument: ct => ReplaceAsync(context.Document, oldNode, replacement, ct),
                    equivalenceKey: DiagnosticIds.NetFrameworkIncompatibleMethod + ":" + NetFrameworkIncompatibleMethodAnalyzer.DictionaryRemoveOut),
                diagnostic);
        }

        private static void RegisterCapacityHintFix(CodeFixContext context, InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax memberAccess, Diagnostic diagnostic)
        {
            // EnsureCapacity/TrimExcess do not change the dictionary's contents, so a standalone call can
            // simply be dropped. Only offered inside a block, where removing the statement leaves valid code,
            // and when dropping it does not also drop side effects of its operands.
            if (invocation.Parent is not ExpressionStatementSyntax { Parent: BlockSyntax } statement)
                return;

            if (HasSideEffects(memberAccess.Expression) || invocation.ArgumentList.Arguments.Any(a => HasSideEffects(a.Expression)))
                return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: $"Remove the {memberAccess.Name.Identifier.ValueText} call (capacity hint only)",
                    createChangedDocument: ct => RemoveAsync(context.Document, statement, ct),
                    equivalenceKey: DiagnosticIds.NetFrameworkIncompatibleMethod + ":" + NetFrameworkIncompatibleMethodAnalyzer.CapacityHint),
                diagnostic);
        }

        /// <summary>
        /// Prepares <paramref name="replacement"/> to stand in for <paramref name="statement"/>. As an embedded
        /// statement (e.g. <c>if (c) d.TryAdd(k, v); else ...</c>) a bare <c>if</c> would capture the outer
        /// <c>else</c>, so it is wrapped in a block there.
        /// </summary>
        private static StatementSyntax AsStatementReplacement(ExpressionStatementSyntax statement, StatementSyntax replacement)
        {
            if (statement.Parent is not (BlockSyntax or SwitchSectionSyntax or GlobalStatementSyntax))
                replacement = Block(replacement);

            return replacement.WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static bool HasSideEffects(ExpressionSyntax expression) =>
            expression.DescendantNodesAndSelf().Any(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or AwaitExpressionSyntax or AssignmentExpressionSyntax
                || n.IsKind(SyntaxKind.PreIncrementExpression) || n.IsKind(SyntaxKind.PreDecrementExpression)
                || n.IsKind(SyntaxKind.PostIncrementExpression) || n.IsKind(SyntaxKind.PostDecrementExpression));

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

        private static async Task<Document> RemoveAsync(Document document, SyntaxNode node, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var newRoot = root?.RemoveNode(node, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            return newRoot is null ? document : document.WithSyntaxRoot(newRoot);
        }

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
