using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace Shitalyzer
{
    /// <summary>
    /// Renames a variable named <c>package</c> to <c>_package</c> (solution-wide rename of the symbol).
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PackageVariableNameCodeFixProvider)), Shared]
    public sealed class PackageVariableNameCodeFixProvider : CodeFixProvider
    {
        private const string NewName = "_package";

        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(DiagnosticIds.PackageVariableName);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null)
                return;

            var diagnostic = context.Diagnostics[0];
            var token = root.FindToken(diagnostic.Location.SourceSpan.Start);
            if (!token.IsKind(SyntaxKind.IdentifierToken))
                return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: $"Rename to '{NewName}'",
                    createChangedSolution: ct => RenameAsync(context.Document, token.Parent!, ct),
                    equivalenceKey: DiagnosticIds.PackageVariableName),
                diagnostic);
        }

        private static async Task<Solution> RenameAsync(Document document, SyntaxNode declaration, CancellationToken cancellationToken)
        {
            var solution = document.Project.Solution;
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel is null)
                return solution;

            var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
            if (symbol is null)
                return solution;

            var newName = ResolveAvailableName(semanticModel, declaration.SpanStart);

            var options = new SymbolRenameOptions(
                RenameOverloads: false,
                RenameInStrings: false,
                RenameInComments: false,
                RenameFile: false);

            return await Renamer.RenameSymbolAsync(solution, symbol, options, newName, cancellationToken).ConfigureAwait(false);
        }

        private static string ResolveAvailableName(SemanticModel semanticModel, int position)
        {
            var existing = semanticModel.LookupSymbols(position).Select(s => s.Name).ToImmutableHashSet();
            if (!existing.Contains(NewName))
                return NewName;

            for (var i = 1; ; i++)
            {
                var candidate = NewName + i;
                if (!existing.Contains(candidate))
                    return candidate;
            }
        }
    }
}
