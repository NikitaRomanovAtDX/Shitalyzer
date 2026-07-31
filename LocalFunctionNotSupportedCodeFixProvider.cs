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
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Shitalyzer
{
    /// <summary>
    /// Lifts a local function out to a private method of the containing type. Any enclosing state the
    /// local function reads (locals and parameters of the enclosing method) is threaded through added
    /// parameters, and every call site is updated to pass those values:
    /// <code>
    /// class C {
    ///     void M() {
    ///         int seed = 10;
    ///         int Add(int a) => a + seed;           // captures 'seed'
    ///         var x = Add(1);
    ///     }
    /// }
    /// // becomes:
    /// class C {
    ///     void M() {
    ///         int seed = 10;
    ///         var x = Add(1, seed);                 // capture passed as an argument
    ///     }
    ///     private int Add(int a, int seed) => a + seed;
    /// }
    /// </code>
    /// A captured variable the function <em>mutates</em> is passed by <c>ref</c> so writes still flow back
    /// to the caller, matching C#'s by-reference capture semantics. The fix is not offered when the local
    /// function captures something that cannot be turned into a parameter (an enclosing method type
    /// parameter or another local function), when a capture has no expressible parameter type, when a call
    /// site uses named arguments, or when the local function is used as a method group.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(LocalFunctionNotSupportedCodeFixProvider)), Shared]
    public sealed class LocalFunctionNotSupportedCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(DiagnosticIds.LocalFunctionNotSupported);

        // No FixAll: moving several local functions out of one document mixes node removals and member
        // insertions on overlapping scopes, which a shared batch cannot merge reliably.
        public override FixAllProvider? GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
                return;

            var diagnostic = context.Diagnostics[0];
            var localFunction = root.FindNode(diagnostic.Location.SourceSpan)
                .AncestorsAndSelf()
                .OfType<LocalFunctionStatementSyntax>()
                .FirstOrDefault();
            if (localFunction is null)
                return;

            // Must live inside a type we can add a member to (not a top-level-statements program).
            var containingType = localFunction.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (containingType is null)
                return;

            if (semanticModel.GetDeclaredSymbol(localFunction, context.CancellationToken) is not IMethodSymbol localSymbol)
                return;

            // Enclosing state the function reads becomes parameters; bail if any capture can't be expressed.
            if (!TryCollectCaptures(localFunction, semanticModel, context.CancellationToken, out var captures))
                return;

            // Locate every call site so we can pass the captured values along; bail on unsupported uses.
            if (!TryCollectCallSites(localFunction, localSymbol, containingType, semanticModel, context.CancellationToken, out var callSites))
                return;

            var recursiveCalls = callSites.Where(c => localFunction.Span.Contains(c.Span)).ToImmutableArray();
            var externalCalls = callSites.Where(c => !localFunction.Span.Contains(c.Span)).ToImmutableArray();

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Move local function to a method",
                    createChangedDocument: ct => ApplyAsync(
                        context.Document, localFunction, containingType, captures, externalCalls, recursiveCalls, ct),
                    equivalenceKey: DiagnosticIds.LocalFunctionNotSupported),
                diagnostic);
        }

        private static async Task<Document> ApplyAsync(
            Document document,
            LocalFunctionStatementSyntax localFunction,
            TypeDeclarationSyntax containingType,
            ImmutableArray<Capture> captures,
            ImmutableArray<InvocationExpressionSyntax> externalCalls,
            ImmutableArray<InvocationExpressionSyntax> recursiveCalls,
            CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);

            // Recursive calls live inside the body we are moving, so they are rewritten as part of the new
            // method; external calls are rewritten in place before the declaration is removed.
            var method = ToMethod(localFunction, captures, recursiveCalls);

            foreach (var call in externalCalls)
                editor.ReplaceNode(call, AppendCapturedArguments(call, captures));

            editor.RemoveNode(localFunction, SyntaxRemoveOptions.KeepNoTrivia);
            editor.AddMember(containingType, method);

            return editor.GetChangedDocument();
        }

        /// <summary>
        /// Turns a <see cref="LocalFunctionStatementSyntax"/> into an equivalent private
        /// <see cref="MethodDeclarationSyntax"/>, appending the captured variables as parameters,
        /// rewriting any recursive calls in the body to pass them, and preserving
        /// <c>static</c>/<c>async</c>/<c>unsafe</c>/<c>extern</c> modifiers, type parameters, constraints,
        /// attributes, and the (block or expression) body.
        /// </summary>
        private static MethodDeclarationSyntax ToMethod(
            LocalFunctionStatementSyntax localFunction, ImmutableArray<Capture> captures, ImmutableArray<InvocationExpressionSyntax> recursiveCalls)
        {
            var modifiers = TokenList(Token(SyntaxKind.PrivateKeyword))
                .AddRange(localFunction.Modifiers.Select(m => m.WithoutTrivia()));

            var parameters = localFunction.ParameterList.AddParameters(
                captures.Select(c => c.ToParameter()).ToArray());

            var method = MethodDeclaration(localFunction.ReturnType.WithoutLeadingTrivia(), localFunction.Identifier)
                .WithAttributeLists(localFunction.AttributeLists)
                .WithModifiers(modifiers)
                .WithTypeParameterList(localFunction.TypeParameterList)
                .WithParameterList(parameters)
                .WithConstraintClauses(localFunction.ConstraintClauses);

            if (localFunction.Body is { } block)
            {
                if (recursiveCalls.Length > 0)
                    block = block.ReplaceNodes(recursiveCalls, (original, _) => AppendCapturedArguments(original, captures));
                method = method.WithBody(block);
            }
            else if (localFunction.ExpressionBody is { } arrow)
            {
                if (recursiveCalls.Length > 0)
                    arrow = arrow.ReplaceNodes(recursiveCalls, (original, _) => AppendCapturedArguments(original, captures));
                method = method.WithExpressionBody(arrow).WithSemicolonToken(localFunction.SemicolonToken);
            }

            return method.WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static InvocationExpressionSyntax AppendCapturedArguments(InvocationExpressionSyntax invocation, ImmutableArray<Capture> captures) =>
            invocation.WithArgumentList(invocation.ArgumentList.AddArguments(captures.Select(c => c.ToArgument()).ToArray()));

        /// <summary>
        /// Collects, in first-use order, the enclosing locals and parameters the local function reads.
        /// Returns <c>false</c> (fix not offered) when the function captures a construct that cannot be
        /// turned into a parameter — an enclosing method type parameter or another local function — or when
        /// a captured variable has no expressible parameter type, or when write analysis is unavailable.
        /// </summary>
        private static bool TryCollectCaptures(
            LocalFunctionStatementSyntax localFunction, SemanticModel semanticModel, CancellationToken cancellationToken, out ImmutableArray<Capture> captures)
        {
            captures = default;

            if (!TryGetWrittenSymbols(localFunction, semanticModel, out var written))
                return false;

            var result = new List<Capture>();
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            foreach (var name in localFunction.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbol = semanticModel.GetSymbolInfo(name, cancellationToken).Symbol;
                switch (symbol)
                {
                    case ILocalSymbol or IParameterSymbol when !IsDeclaredWithin(symbol!, localFunction):
                        if (!seen.Add(symbol!))
                            break;
                        var type = symbol is ILocalSymbol local ? local.Type : ((IParameterSymbol)symbol!).Type;
                        if (type is null || type.TypeKind == TypeKind.Error || type.IsAnonymousType)
                            return false;
                        var typeSyntax = ParseTypeName(type.ToMinimalDisplayString(semanticModel, localFunction.SpanStart));
                        result.Add(new Capture(symbol!.Name, typeSyntax, byRef: written.Contains(symbol!)));
                        break;

                    // References to a method type parameter or a sibling local function cannot be passed as
                    // ordinary arguments, so the function cannot be lifted out mechanically.
                    case IMethodSymbol { MethodKind: MethodKind.LocalFunction } when !IsDeclaredWithin(symbol, localFunction):
                    case ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } when !IsDeclaredWithin(symbol, localFunction):
                        return false;
                }
            }

            captures = result.ToImmutableArray();
            return true;
        }

        /// <summary>
        /// The set of symbols written inside the local function's body, used to decide which captures must
        /// be passed by <c>ref</c>. Returns <c>false</c> if data-flow analysis cannot be performed, in
        /// which case the fix is not offered rather than risk dropping a write.
        /// </summary>
        private static bool TryGetWrittenSymbols(
            LocalFunctionStatementSyntax localFunction, SemanticModel semanticModel, out ImmutableHashSet<ISymbol> written)
        {
            written = ImmutableHashSet<ISymbol>.Empty.WithComparer(SymbolEqualityComparer.Default);

            SyntaxNode? region = localFunction.Body ?? (SyntaxNode?)localFunction.ExpressionBody?.Expression;
            if (region is null)
                return false;

            var dataFlow = semanticModel.AnalyzeDataFlow(region);
            if (dataFlow is null || !dataFlow.Succeeded)
                return false;

            written = dataFlow.WrittenInside.ToImmutableHashSet(SymbolEqualityComparer.Default);
            return true;
        }

        /// <summary>
        /// Finds every invocation of the local function (searching the whole containing type and matching by
        /// symbol, which is precise because a local function symbol is only visible in its own scope).
        /// Returns <c>false</c> when the function is referenced in a way this fix cannot rewrite — as a
        /// method group, or through a call that uses named arguments.
        /// </summary>
        private static bool TryCollectCallSites(
            LocalFunctionStatementSyntax localFunction,
            IMethodSymbol localSymbol,
            TypeDeclarationSyntax containingType,
            SemanticModel semanticModel,
            CancellationToken cancellationToken,
            out ImmutableArray<InvocationExpressionSyntax> callSites)
        {
            callSites = default;
            var result = new List<InvocationExpressionSyntax>();

            foreach (var reference in containingType.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = semanticModel.GetSymbolInfo(reference, cancellationToken).Symbol;
                if (!SymbolEqualityComparer.Default.Equals(symbol?.OriginalDefinition, localSymbol))
                    continue;

                if (IsInsideNameOf(reference))
                    continue; // nameof(LocalFunc) yields a string; no arguments to thread.

                if (reference.Parent is not InvocationExpressionSyntax invocation || invocation.Expression != reference)
                    return false; // Used as a method group; cannot append captured arguments.

                if (invocation.ArgumentList.Arguments.Any(a => a.NameColon is not null))
                    return false; // Appending positional arguments after named ones would not compile.

                result.Add(invocation);
            }

            callSites = result.ToImmutableArray();
            return true;
        }

        private static bool IsInsideNameOf(SyntaxNode reference) =>
            reference.Ancestors()
                .OfType<InvocationExpressionSyntax>()
                .Any(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } && i.Span.Contains(reference.Span));

        private static bool IsDeclaredWithin(ISymbol symbol, SyntaxNode container) =>
            symbol.DeclaringSyntaxReferences.Any(
                r => r.SyntaxTree == container.SyntaxTree && container.Span.Contains(r.Span));

        /// <summary>A captured enclosing variable, rendered as an added parameter and matching argument.</summary>
        private readonly struct Capture
        {
            private readonly string name;
            private readonly TypeSyntax type;
            private readonly bool byRef;

            public Capture(string name, TypeSyntax type, bool byRef)
            {
                this.name = name;
                this.type = type;
                this.byRef = byRef;
            }

            public ParameterSyntax ToParameter()
            {
                var parameter = Parameter(Identifier(name)).WithType(type);
                return byRef ? parameter.WithModifiers(TokenList(Token(SyntaxKind.RefKeyword))) : parameter;
            }

            public ArgumentSyntax ToArgument()
            {
                var argument = Argument(IdentifierName(name));
                return byRef ? argument.WithRefKindKeyword(Token(SyntaxKind.RefKeyword)) : argument;
            }
        }
    }
}
