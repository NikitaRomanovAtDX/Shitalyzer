using System.Collections.Generic;
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
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Shitalyzer
{
    /// <summary>
    /// Rewrites a null-coalescing assignment into an explicit null check:
    /// <code>
    /// x ??= y;                          // expression statement
    /// // becomes:
    /// if(x == null)
    ///     x = y;
    ///
    /// return x ??= y;                   // return statement
    /// // becomes:
    /// if(x == null)
    ///     x = y;
    /// return x;
    ///
    /// T P => x ??= y;                   // expression-bodied member or accessor
    /// // becomes:
    /// T P {
    ///     get {
    ///         if(x == null)
    ///             x = y;
    ///         return x;
    ///     }
    /// }
    /// </code>
    /// The fix is offered only where the rewrite keeps the evaluation order: the assignment is a whole
    /// statement, the whole returned value, or the whole body of a member. It is not offered when the
    /// assignment sits inside a larger expression or a lambda, when its left side has side effects
    /// (an invocation, an object creation, an assignment, an increment), or when a returned left side is a
    /// nullable value type (<c>x ??= y</c> yields <c>T</c> there, while <c>x</c> is <c>T?</c>).
    /// A rewritten statement that is the body of an <c>if</c>, <c>else</c> or loop is wrapped in a block, so a
    /// following <c>else</c> keeps binding to the outer <c>if</c>.
    /// Spacing (<c>if(</c> or <c>if (</c>) follows the document's formatting options.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CoalesceAssignmentNotSupportedCodeFixProvider)), Shared]
    public sealed class CoalesceAssignmentNotSupportedCodeFixProvider : CodeFixProvider
    {
        private const string Title = "Replace '??=' with an explicit null check";

        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(DiagnosticIds.CoalesceAssignmentNotSupported);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
                return;

            var diagnostic = context.Diagnostics[0];
            var assignment = root.FindToken(diagnostic.Location.SourceSpan.Start).Parent as AssignmentExpressionSyntax;
            if (assignment is null || !assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression))
                return;
            if (HasSideEffects(assignment.Left))
                return;

            var rewrite = TryCreateRewrite(assignment, semanticModel, context.CancellationToken);
            if (rewrite is null)
                return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: Title,
                    createChangedDocument: _ => Task.FromResult(context.Document.WithSyntaxRoot(rewrite(root))),
                    equivalenceKey: DiagnosticIds.CoalesceAssignmentNotSupported),
                diagnostic);
        }

        private delegate SyntaxNode Rewrite(SyntaxNode root);

        private static Rewrite? TryCreateRewrite(AssignmentExpressionSyntax assignment, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            var outer = WalkUpParentheses(assignment);
            switch (outer.Parent)
            {
                case ExpressionStatementSyntax statement:
                    return root => ReplaceStatement(root, statement, new StatementSyntax[] { CreateNullCheck(assignment) });

                case ReturnStatementSyntax returnStatement:
                    if (IsNullableValueType(assignment.Left, semanticModel, cancellationToken))
                        return null;
                    return root => ReplaceStatement(root, returnStatement, CreateCheckAndReturn(assignment, returnStatement));

                case ArrowExpressionClauseSyntax arrow:
                    return TryCreateArrowRewrite(assignment, arrow, semanticModel, cancellationToken);

                default:
                    return null;
            }
        }

        private static Rewrite? TryCreateArrowRewrite(AssignmentExpressionSyntax assignment, ArrowExpressionClauseSyntax arrow, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            switch (arrow.Parent)
            {
                case PropertyDeclarationSyntax property:
                    if (IsNullableValueType(assignment.Left, semanticModel, cancellationToken))
                        return null;
                    return root => root.ReplaceNode(property, property
                        .WithExpressionBody(null)
                        .WithSemicolonToken(default)
                        .WithAccessorList(AccessorList(SingletonList(
                            AccessorDeclaration(SyntaxKind.GetAccessorDeclaration, CreateBody(assignment, returnsValue: true))))
                            .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithTrailingTrivia(property.SemicolonToken.TrailingTrivia)))
                        .WithAdditionalAnnotations(Formatter.Annotation));

                case IndexerDeclarationSyntax indexer:
                    if (IsNullableValueType(assignment.Left, semanticModel, cancellationToken))
                        return null;
                    return root => root.ReplaceNode(indexer, indexer
                        .WithExpressionBody(null)
                        .WithSemicolonToken(default)
                        .WithAccessorList(AccessorList(SingletonList(
                            AccessorDeclaration(SyntaxKind.GetAccessorDeclaration, CreateBody(assignment, returnsValue: true))))
                            .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithTrailingTrivia(indexer.SemicolonToken.TrailingTrivia)))
                        .WithAdditionalAnnotations(Formatter.Annotation));

                case AccessorDeclarationSyntax accessor:
                    var accessorReturns = accessor.IsKind(SyntaxKind.GetAccessorDeclaration);
                    if (accessorReturns && IsNullableValueType(assignment.Left, semanticModel, cancellationToken))
                        return null;
                    return root => root.ReplaceNode(accessor, accessor
                        .WithExpressionBody(null)
                        .WithSemicolonToken(default)
                        .WithBody(CreateBody(assignment, accessorReturns)
                            .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithTrailingTrivia(accessor.SemicolonToken.TrailingTrivia)))
                        .WithAdditionalAnnotations(Formatter.Annotation));

                case BaseMethodDeclarationSyntax method:
                    if (semanticModel.GetDeclaredSymbol(method, cancellationToken) is not IMethodSymbol symbol)
                        return null;
                    var methodReturns = !symbol.ReturnsVoid;
                    if (methodReturns && IsNullableValueType(assignment.Left, semanticModel, cancellationToken))
                        return null;
                    return root => root.ReplaceNode(method, method
                        .WithExpressionBody(null)
                        .WithSemicolonToken(default)
                        .WithBody(CreateBody(assignment, methodReturns)
                            .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken).WithTrailingTrivia(method.SemicolonToken.TrailingTrivia)))
                        .WithAdditionalAnnotations(Formatter.Annotation));

                default:
                    return null;
            }
        }

        private static SyntaxNode ReplaceStatement(SyntaxNode root, StatementSyntax statement, IReadOnlyList<StatementSyntax> replacement)
        {
            var statements = replacement.Select(s => s.WithAdditionalAnnotations(Formatter.Annotation)).ToArray();
            statements[0] = statements[0].WithLeadingTrivia(statement.GetLeadingTrivia());
            statements[statements.Length - 1] = statements[statements.Length - 1].WithTrailingTrivia(statement.GetTrailingTrivia());

            if (statement.Parent is BlockSyntax || statement.Parent is SwitchSectionSyntax)
                return root.ReplaceNode(statement, statements);
            return root.ReplaceNode(statement, Block(statements)
                .WithLeadingTrivia(statement.GetLeadingTrivia())
                .WithTrailingTrivia(statement.GetTrailingTrivia())
                .WithAdditionalAnnotations(Formatter.Annotation));
        }

        private static StatementSyntax[] CreateCheckAndReturn(AssignmentExpressionSyntax assignment, ReturnStatementSyntax returnStatement) =>
            new StatementSyntax[]
            {
                CreateNullCheck(assignment),
                returnStatement.WithExpression(Left(assignment)).WithoutTrivia(),
            };

        private static BlockSyntax CreateBody(AssignmentExpressionSyntax assignment, bool returnsValue)
        {
            var statements = new List<StatementSyntax> { CreateNullCheck(assignment) };
            if (returnsValue)
                statements.Add(ReturnStatement(Left(assignment)));
            return Block(statements);
        }

        private static IfStatementSyntax CreateNullCheck(AssignmentExpressionSyntax assignment)
        {
            var left = Left(assignment);
            var condition = BinaryExpression(
                SyntaxKind.EqualsExpression,
                left,
                LiteralExpression(SyntaxKind.NullLiteralExpression));
            var store = ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                left,
                assignment.Right.WithoutTrivia()));
            return IfStatement(condition, store);
        }

        private static ExpressionSyntax Left(AssignmentExpressionSyntax assignment)
        {
            var left = assignment.Left;
            while (left is ParenthesizedExpressionSyntax parenthesized)
                left = parenthesized.Expression;
            return left.WithoutTrivia();
        }

        private static ExpressionSyntax WalkUpParentheses(ExpressionSyntax expression)
        {
            while (expression.Parent is ParenthesizedExpressionSyntax parenthesized)
                expression = parenthesized;
            return expression;
        }

        private static bool HasSideEffects(ExpressionSyntax expression) =>
            expression.DescendantNodesAndSelf().Any(node =>
                node is InvocationExpressionSyntax
                || node is BaseObjectCreationExpressionSyntax
                || node is AssignmentExpressionSyntax
                || node is AwaitExpressionSyntax
                || node.IsKind(SyntaxKind.PreIncrementExpression)
                || node.IsKind(SyntaxKind.PreDecrementExpression)
                || node.IsKind(SyntaxKind.PostIncrementExpression)
                || node.IsKind(SyntaxKind.PostDecrementExpression));

        private static bool IsNullableValueType(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken cancellationToken) =>
            semanticModel.GetTypeInfo(expression, cancellationToken).Type is INamedTypeSymbol type
            && type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }
}
