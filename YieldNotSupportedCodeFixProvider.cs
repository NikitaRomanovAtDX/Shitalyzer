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
    /// Rewrites an iterator method/local function into one that populates a <c>List&lt;T&gt;</c> and
    /// returns it:
    /// <code>
    /// IEnumerable&lt;int&gt; Get() { yield return 1; yield return 2; }
    /// // becomes:
    /// IEnumerable&lt;int&gt; Get() {
    ///     var result = new List&lt;int&gt;();
    ///     result.Add(1);
    ///     result.Add(2);
    ///     return result;
    /// }
    /// </code>
    /// <c>yield break;</c> becomes <c>return result;</c>. Only offered for block-bodied members that
    /// return <c>IEnumerable</c>/<c>IEnumerable&lt;T&gt;</c>, since a <c>List&lt;T&gt;</c> is assignable
    /// to those but not to <c>IEnumerator&lt;T&gt;</c>.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(YieldNotSupportedCodeFixProvider)), Shared]
    public sealed class YieldNotSupportedCodeFixProvider : CodeFixProvider
    {
        private const string ResultNameBase = "result";

        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(DiagnosticIds.YieldNotSupported);

        // No FixAll: each rewrite introduces a body-scoped local whose unique name is resolved from
        // that body's scope, which a shared batch cannot coordinate across nested iterators.
        public override FixAllProvider? GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
                return;

            var diagnostic = context.Diagnostics[0];
            var container = root.FindNode(diagnostic.Location.SourceSpan)
                .AncestorsAndSelf()
                .FirstOrDefault(n => n is MethodDeclarationSyntax or LocalFunctionStatementSyntax);

            var body = container switch
            {
                MethodDeclarationSyntax m => m.Body,
                LocalFunctionStatementSyntax lf => lf.Body,
                _ => null,
            };
            if (container is null || body is null)
                return;

            if (semanticModel.GetDeclaredSymbol(container, context.CancellationToken) is not IMethodSymbol method
                || !TryGetElementType(method.ReturnType, semanticModel, body.SpanStart, out var elementType))
            {
                return; // Not an IEnumerable-returning iterator we can safely turn into a List<T>.
            }

            var listType = GenericName(Identifier("List"))
                .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(elementType)));
            var resultName = ResolveResultName(semanticModel, body.SpanStart);

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Return a List<T> instead of using 'yield'",
                    createChangedDocument: ct => ApplyAsync(context.Document, body, listType, resultName, ct),
                    equivalenceKey: DiagnosticIds.YieldNotSupported),
                diagnostic);
        }

        private static async Task<Document> ApplyAsync(
            Document document, BlockSyntax body, TypeSyntax listType, string resultName, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
                return document;

            var rewritten = (BlockSyntax)new YieldRewriter(resultName).Visit(body)!;

            // var result = new List<T>();
            var declaration = LocalDeclarationStatement(
                VariableDeclaration(IdentifierName("var"))
                    .WithVariables(SingletonSeparatedList(
                        VariableDeclarator(Identifier(resultName))
                            .WithInitializer(EqualsValueClause(
                                ObjectCreationExpression(listType).WithArgumentList(ArgumentList()))))));

            var statements = new List<StatementSyntax> { declaration };
            statements.AddRange(rewritten.Statements);

            // Ensure the list is returned; a trailing 'yield break' already produced 'return result;'.
            if (statements.Count == 1 || statements[statements.Count - 1] is not ReturnStatementSyntax)
                statements.Add(ReturnStatement(IdentifierName(resultName)));

            var newBody = body.WithStatements(List(statements)).WithAdditionalAnnotations(Formatter.Annotation);
            return document.WithSyntaxRoot(root.ReplaceNode(body, newBody));
        }

        /// <summary>
        /// Determines the <c>List&lt;T&gt;</c> element type for an iterator's return type. Succeeds only
        /// for <c>IEnumerable</c> (element <c>object</c>) and <c>IEnumerable&lt;T&gt;</c> (element T).
        /// </summary>
        private static bool TryGetElementType(ITypeSymbol returnType, SemanticModel semanticModel, int position, out TypeSyntax elementType)
        {
            // System.Collections.IEnumerable -> List<object>
            if (IsType(returnType, "System.Collections", "IEnumerable"))
            {
                elementType = PredefinedType(Token(SyntaxKind.ObjectKeyword));
                return true;
            }

            // System.Collections.Generic.IEnumerable<T> -> List<T>
            if (returnType is INamedTypeSymbol named
                && named.TypeArguments.Length == 1
                && IsType(named.OriginalDefinition, "System.Collections.Generic", "IEnumerable"))
            {
                elementType = ParseTypeName(named.TypeArguments[0].ToMinimalDisplayString(semanticModel, position));
                return true;
            }

            elementType = null!;
            return false;
        }

        private static bool IsType(ITypeSymbol type, string containingNamespace, string name) =>
            type.Name == name && type.ContainingNamespace?.ToDisplayString() == containingNamespace;

        private static string ResolveResultName(SemanticModel semanticModel, int position)
        {
            var existing = semanticModel.LookupSymbols(position).Select(s => s.Name).ToImmutableHashSet();
            if (!existing.Contains(ResultNameBase))
                return ResultNameBase;

            for (var i = 1; ; i++)
            {
                var candidate = ResultNameBase + i;
                if (!existing.Contains(candidate))
                    return candidate;
            }
        }

        /// <summary>
        /// Turns <c>yield return expr;</c> into <c>result.Add(expr);</c> and <c>yield break;</c> into
        /// <c>return result;</c>. Nested lambdas and local functions are left untouched — their yields
        /// belong to their own iterators.
        /// </summary>
        private sealed class YieldRewriter : CSharpSyntaxRewriter
        {
            private readonly string resultName;

            public YieldRewriter(string resultName) => this.resultName = resultName;

            public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;
            public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;
            public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;
            public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;

            public override SyntaxNode? VisitYieldStatement(YieldStatementSyntax node)
            {
                if (node.IsKind(SyntaxKind.YieldBreakStatement))
                    return ReturnStatement(IdentifierName(resultName)).WithTriviaFrom(node);

                var add = ExpressionStatement(
                    InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName(resultName), IdentifierName("Add")),
                        ArgumentList(SingletonSeparatedList(Argument(node.Expression!.WithoutTrivia())))));
                return add.WithTriviaFrom(node);
            }
        }
    }
}
