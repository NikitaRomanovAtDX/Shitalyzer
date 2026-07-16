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
    /// Wraps a captured value-type local in a generated reference-type holder class:
    /// <code>
    /// int data = 0;
    /// // becomes:
    /// var dataHolder = new DataHolder { Value = 0 };
    /// // + a nested:  sealed class DataHolder { public int Value { get; set; } }
    /// </code>
    /// All references to the local are rewritten to <c>dataHolder.Value</c>. Only offered for a
    /// single-variable local declaration inside a type.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ValueTypeInLambdaCodeFixProvider)), Shared]
    public sealed class ValueTypeInLambdaCodeFixProvider : CodeFixProvider
    {
        private const string PropertyName = "Value";

        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(DiagnosticIds.ValueTypeCapturedInLambda);

        // No FixAll: multiple holders in one document would require conflict-free name coordination.
        public override FixAllProvider? GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null)
                return;

            var diagnostic = context.Diagnostics[0];
            var declarator = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<VariableDeclaratorSyntax>();
            if (declarator is null)
                return;

            if (declarator.Parent is not VariableDeclarationSyntax declaration
                || declaration.Parent is not LocalDeclarationStatementSyntax
                || declaration.Variables.Count != 1)
            {
                return; // Only the simple single-variable local case is auto-fixable.
            }

            if (declarator.FirstAncestorOrSelf<TypeDeclarationSyntax>() is null)
                return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Wrap in a holder class",
                    createChangedDocument: ct => ApplyAsync(context.Document, declarator, ct),
                    equivalenceKey: DiagnosticIds.ValueTypeCapturedInLambda),
                diagnostic);
        }

        private static async Task<Document> ApplyAsync(Document document, VariableDeclaratorSyntax declarator, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel is null || root is null)
                return document;

            if (semanticModel.GetDeclaredSymbol(declarator, cancellationToken) is not ILocalSymbol local)
                return document;

            var declaration = (VariableDeclarationSyntax)declarator.Parent!;
            var localStatement = (LocalDeclarationStatementSyntax)declaration.Parent!;
            var typeDeclaration = declarator.FirstAncestorOrSelf<TypeDeclarationSyntax>()!;

            var originalName = local.Name;
            var holderVarName = originalName + "Holder";
            var holderClassName = MakeUniqueTypeName(semanticModel, ToPascalCase(originalName) + "Holder", localStatement.SpanStart);

            // Type syntax for the holder property: reuse the written type unless it is 'var'.
            var valueType = declaration.Type.IsVar
                ? ParseTypeName(local.Type.ToMinimalDisplayString(semanticModel, localStatement.SpanStart))
                : declaration.Type.WithoutTrivia();

            var editor = new SyntaxEditor(root, document.Project.Solution.Workspace.Services);

            // 1. Rewrite every reference to the local as `holder.Value`.
            var memberAccess = MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName(holderVarName),
                IdentifierName(PropertyName));

            foreach (var identifier in FindReferences(semanticModel, typeDeclaration, local, cancellationToken))
            {
                editor.ReplaceNode(identifier, memberAccess.WithTriviaFrom(identifier));
            }

            // 2. Replace the local declaration with `var holder = new Holder { Value = <init> };`.
            var initializer = declarator.Initializer?.Value.WithoutTrivia();
            var creation = BuildHolderCreation(holderClassName, initializer);
            var newStatement = LocalDeclarationStatement(
                    VariableDeclaration(IdentifierName("var"))
                        .WithVariables(SingletonSeparatedList(
                            VariableDeclarator(Identifier(holderVarName))
                                .WithInitializer(EqualsValueClause(creation)))))
                .WithTriviaFrom(localStatement);
            editor.ReplaceNode(localStatement, newStatement);

            // 3. Add the holder class to the enclosing type.
            var holderClass = BuildHolderClass(holderClassName, valueType);
            editor.AddMember(typeDeclaration, holderClass);

            return document.WithSyntaxRoot(editor.GetChangedRoot());
        }

        private static ObjectCreationExpressionSyntax BuildHolderCreation(string holderClassName, ExpressionSyntax? initializer)
        {
            var creation = ObjectCreationExpression(IdentifierName(holderClassName)).WithArgumentList(ArgumentList());

            if (initializer is null)
                return creation;

            var assignment = AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(PropertyName),
                initializer);

            return creation.WithInitializer(
                InitializerExpression(
                    SyntaxKind.ObjectInitializerExpression,
                    SingletonSeparatedList<ExpressionSyntax>(assignment)));
        }

        private static ClassDeclarationSyntax BuildHolderClass(string holderClassName, TypeSyntax valueType)
        {
            var property = PropertyDeclaration(valueType, Identifier(PropertyName))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                .WithAccessorList(AccessorList(List(new[]
                {
                    AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                    AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                })));

            return ClassDeclaration(holderClassName)
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.SealedKeyword)))
                .WithMembers(SingletonList<MemberDeclarationSyntax>(property))
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static IEnumerable<IdentifierNameSyntax> FindReferences(
            SemanticModel semanticModel, SyntaxNode scope, ILocalSymbol local, CancellationToken cancellationToken)
        {
            foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (identifier.Identifier.ValueText != local.Name)
                    continue;

                var symbol = semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol;
                if (SymbolEqualityComparer.Default.Equals(symbol, local))
                    yield return identifier;
            }
        }

        private static string MakeUniqueTypeName(SemanticModel semanticModel, string baseName, int position)
        {
            var existing = semanticModel.LookupNamespacesAndTypes(position).Select(s => s.Name).ToImmutableHashSet();
            if (!existing.Contains(baseName))
                return baseName;

            for (var i = 1; ; i++)
            {
                var candidate = baseName + i;
                if (!existing.Contains(candidate))
                    return candidate;
            }
        }

        private static string ToPascalCase(string name)
        {
            var trimmed = name.TrimStart('_');
            if (trimmed.Length == 0)
                return "Value";
            return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
        }
    }
}
