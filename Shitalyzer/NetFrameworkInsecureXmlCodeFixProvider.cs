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
using Microsoft.CodeAnalysis.Simplification;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Shitalyzer
{
    /// <summary>
    /// Routes the XML text, stream, reader or file an <c>XmlDocument</c> loads through a reader with DTD processing
    /// prohibited, which is what CA3075 asks for on the .NET Framework leg:
    /// <code>
    /// var doc = new XmlDocument { PreserveWhitespace = true };   // becomes
    /// doc.LoadXml(xml);                                          //   var doc = SafeXml.CreateDocument(() => new XmlDocument { PreserveWhitespace = true }, xml);
    ///
    /// doc.LoadXml(xml);   // becomes doc.Load(new XmlTextReader(new StringReader(xml)) { DtdProcessing = DtdProcessing.Prohibit });
    /// doc.Load(stream);   // becomes doc.Load(new XmlTextReader(stream) { DtdProcessing = DtdProcessing.Prohibit });
    /// doc.Load(path);     // becomes using (var reader = new XmlTextReader(path) { DtdProcessing = DtdProcessing.Prohibit }) doc.Load(reader);
    /// doc.InnerXml = xml; // becomes doc.Load(new XmlTextReader(new StringReader(xml)) { DtdProcessing = DtdProcessing.Prohibit });
    /// </code>
    /// <c>XmlDocument</c> reads its input through an <c>XmlTextReader</c> itself, so the XmlTextReader form keeps the
    /// parse identical (no end-of-line or attribute normalization, no character checking) except that a DTD now throws.
    /// <c>XmlReader.Create</c> would normalize, which is why it is not offered. The SafeXml form is offered only when
    /// <c>DevExpress.Utils.SafeXml</c> is referenced and the document is created by the statement right before the load.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NetFrameworkInsecureXmlCodeFixProvider)), Shared]
    public sealed class NetFrameworkInsecureXmlCodeFixProvider : CodeFixProvider
    {
        private const string SafeXmlMetadataName = "DevExpress.Utils.SafeXml";

        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(DiagnosticIds.NetFrameworkInsecureXml);

        private const string SafeXmlKey = DiagnosticIds.NetFrameworkInsecureXml + ":SafeXml";
        private const string XmlTextReaderKey = DiagnosticIds.NetFrameworkInsecureXml + ":XmlTextReader";

        /// <summary>
        /// Fix all applies every diagnostic's edits to one tree. The batch fixer merges per-diagnostic text diffs instead,
        /// and drops the SafeXml merges that touch neighbouring statements, so it would need several passes.
        /// </summary>
        public override FixAllProvider GetFixAllProvider() => FixAllProvider.Create(async (fixAllContext, document, diagnostics) =>
        {
            var key = fixAllContext.CodeActionEquivalenceKey;
            var root = await document.GetSyntaxRootAsync(fixAllContext.CancellationToken).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(fixAllContext.CancellationToken).ConfigureAwait(false);
            if (key is null || root is null || model is null)
                return null;

            var edits = diagnostics
                .Select(diagnostic => CreateEdits(root, model, diagnostic, key, fixAllContext.CancellationToken))
                .Where(e => !e.IsDefaultOrEmpty)
                .SelectMany(e => e)
                .ToImmutableArray();
            return edits.IsEmpty ? document : await ApplyAsync(document, root, edits, fixAllContext.CancellationToken).ConfigureAwait(false);
        });

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics[0];
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || model is null)
                return;

            Register(SafeXmlKey, "Create the document with SafeXml.CreateDocument");
            Register(XmlTextReaderKey, "Load through an XmlTextReader with DTD processing prohibited");

            void Register(string key, string title)
            {
                var edits = CreateEdits(root, model, diagnostic, key, context.CancellationToken);
                if (!edits.IsDefaultOrEmpty)
                    context.RegisterCodeFix(CodeAction.Create(title, ct => ApplyAsync(context.Document, root, edits, ct), key), diagnostic);
            }
        }

        /// <summary>Replaces <see cref="Node"/> with <see cref="Replacement"/>, or removes it when there is none.</summary>
        private sealed class Edit
        {
            public Edit(SyntaxNode node, SyntaxNode? replacement)
            {
                Node = node;
                Replacement = replacement;
            }

            public SyntaxNode Node { get; }
            public SyntaxNode? Replacement { get; }
        }

        private static ImmutableArray<Edit> CreateEdits(SyntaxNode root, SemanticModel model, Diagnostic diagnostic, string key, CancellationToken cancellationToken)
        {
            if (!diagnostic.Properties.TryGetValue(NetFrameworkInsecureXmlAnalyzer.LoadKindKey, out var loadKind) || loadKind is null)
                return default;

            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var load = LoadSite.Find(node);
            if (load is null)
                return default;

            return key switch
            {
                SafeXmlKey when loadKind == NetFrameworkInsecureXmlAnalyzer.LoadKindXml => CreateSafeXmlEdits(model, load, cancellationToken),
                XmlTextReaderKey => CreateXmlTextReaderEdits(model, load, loadKind),
                _ => default,
            };
        }

        private static async Task<Document> ApplyAsync(Document document, SyntaxNode root, ImmutableArray<Edit> edits, CancellationToken cancellationToken)
        {
            var newRoot = root.TrackNodes(edits.Select(e => e.Node));
            foreach (var edit in edits)
            {
                // A node inside an earlier replacement is gone; its own fix is left for the next pass.
                var current = newRoot.GetCurrentNode(edit.Node);
                if (current is null)
                    continue;
                newRoot = edit.Replacement is null
                    ? newRoot.RemoveNode(current, SyntaxRemoveOptions.KeepNoTrivia)!
                    : newRoot.ReplaceNode(current, edit.Replacement);
            }

            // Add the usings for the qualified names annotated by QualifiedType; the code action's own cleanup then
            // reduces those names to their short form.
            return await ImportAdder.AddImportsAsync(document.WithSyntaxRoot(newRoot), Simplifier.AddImportsAnnotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The document receiver, the loaded argument and the expression that performs the load.</summary>
        private sealed class LoadSite
        {
            private LoadSite(ExpressionSyntax loadExpression, ExpressionSyntax? receiver, ExpressionSyntax argument)
            {
                LoadExpression = loadExpression;
                Receiver = receiver;
                Argument = argument;
            }

            /// <summary>The <c>LoadXml</c>/<c>Load</c> invocation or the <c>InnerXml</c> assignment.</summary>
            public ExpressionSyntax LoadExpression { get; }

            /// <summary>The document expression; null for a conditional access (<c>doc?.LoadXml(xml)</c>).</summary>
            public ExpressionSyntax? Receiver { get; }

            public ExpressionSyntax Argument { get; }

            /// <summary>The statement the load forms on its own, if any.</summary>
            public ExpressionStatementSyntax? Statement => LoadExpression.Parent as ExpressionStatementSyntax;

            public static LoadSite? Find(SyntaxNode node)
            {
                if (node.FirstAncestorOrSelf<AssignmentExpressionSyntax>() is { Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "InnerXml" } target } assignment)
                {
                    // Rewriting the setter into a call only works where the assigned value is not used.
                    return assignment.Parent is ExpressionStatementSyntax
                        ? new LoadSite(assignment, target.Expression, assignment.Right)
                        : null;
                }

                var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                if (invocation is null || invocation.ArgumentList.Arguments.Count != 1)
                    return null;

                var argument = invocation.ArgumentList.Arguments[0];
                if (argument.NameColon is not null || !argument.RefKindKeyword.IsKind(SyntaxKind.None))
                    return null;

                return invocation.Expression switch
                {
                    MemberAccessExpressionSyntax memberAccess => new LoadSite(invocation, memberAccess.Expression, argument.Expression),
                    MemberBindingExpressionSyntax => new LoadSite(invocation, null, argument.Expression),
                    _ => null,
                };
            }

            /// <summary><c>receiver.Load(reader)</c>, keeping a conditional access as it was.</summary>
            public ExpressionSyntax CreateLoadCall(ExpressionSyntax reader)
            {
                var arguments = ArgumentList(SingletonSeparatedList(Argument(reader)));
                if (LoadExpression is InvocationExpressionSyntax invocation)
                {
                    var expression = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax memberAccess => (ExpressionSyntax)memberAccess.WithName(IdentifierName("Load").WithTriviaFrom(memberAccess.Name)),
                        MemberBindingExpressionSyntax memberBinding => memberBinding.WithName(IdentifierName("Load").WithTriviaFrom(memberBinding.Name)),
                        _ => invocation.Expression,
                    };
                    return invocation.WithExpression(expression).WithArgumentList(arguments.WithTriviaFrom(invocation.ArgumentList));
                }

                return InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, Receiver!.WithoutTrivia(), IdentifierName("Load")),
                        arguments)
                    .WithTriviaFrom(LoadExpression);
            }
        }

        private static ImmutableArray<Edit> CreateXmlTextReaderEdits(SemanticModel model, LoadSite load, string loadKind)
        {
            var argument = load.Argument.WithoutTrivia();

            if (loadKind == NetFrameworkInsecureXmlAnalyzer.LoadKindPath)
            {
                // The reader opens the file, so it has to be disposed: only a standalone statement can host the using.
                var statement = load.Statement;
                if (statement is null || load.LoadExpression is not InvocationExpressionSyntax)
                    return default;

                var readerName = GetUniqueName(model, statement, "reader");
                var declaration = VariableDeclaration(
                    IdentifierName(Identifier(TriviaList(), SyntaxKind.VarKeyword, "var", "var", TriviaList())),
                    SingletonSeparatedList(
                        VariableDeclarator(Identifier(readerName))
                            .WithInitializer(EqualsValueClause(CreateTextReader(argument)))));
                var usingStatement = UsingStatement(
                        declaration,
                        null,
                        ExpressionStatement(load.CreateLoadCall(IdentifierName(readerName)).WithoutTrivia()))
                    .WithTriviaFrom(statement)
                    .WithAdditionalAnnotations(Formatter.Annotation);

                return ImmutableArray.Create(new Edit(statement, usingStatement));
            }

            var source = loadKind == NetFrameworkInsecureXmlAnalyzer.LoadKindXml
                ? ObjectCreationExpression(QualifiedType("System.IO.StringReader"))
                    .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(argument))))
                : argument;
            var replacement = load.CreateLoadCall(CreateTextReader(source)).WithAdditionalAnnotations(Formatter.Annotation);
            return ImmutableArray.Create(new Edit(load.LoadExpression, replacement));
        }

        /// <summary>
        /// <c>var doc = new XmlDocument(...); doc.LoadXml(xml);</c> → <c>var doc = SafeXml.CreateDocument(...);</c>.
        /// SafeXml parses exactly like <c>LoadXml</c>, nulls the resolver and is available to the Java port.
        /// </summary>
        private static ImmutableArray<Edit> CreateSafeXmlEdits(SemanticModel model, LoadSite load, CancellationToken cancellationToken)
        {
            var safeXml = model.Compilation.GetTypeByMetadataName(SafeXmlMetadataName);
            if (safeXml is null || !HasCreateDocumentOverloads(safeXml))
                return default;

            var statement = load.Statement;
            if (statement is null || load.Receiver is not { } receiver)
                return default;
            var target = model.GetSymbolInfo(receiver, cancellationToken).Symbol;
            if (target is not (ILocalSymbol or IFieldSymbol))
                return default;

            if (!TryGetPreviousStatement(statement, out var previous))
                return default;

            var creation = GetDocumentCreation(previous, model, target, cancellationToken);
            if (creation is null)
                return default;

            // The XML argument must not read the document it is about to be loaded into.
            if (load.Argument.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id =>
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, cancellationToken).Symbol, target)))
            {
                return default;
            }

            var createdType = model.GetTypeInfo(creation, cancellationToken).Type;
            if (createdType is null)
                return default;

            var argument = load.Argument.WithoutTrivia();
            ExpressionSyntax newCreation;
            if (IsPlainXmlDocument(creation, createdType))
            {
                newCreation = SafeXmlCall(argument);
            }
            else
            {
                // A creation with arguments, an initializer or a derived type goes through the factory overload.
                var explicitCreation = creation switch
                {
                    ObjectCreationExpressionSyntax objectCreation => objectCreation.WithoutTrivia(),
                    ImplicitObjectCreationExpressionSyntax implicitCreation => ObjectCreationExpression(
                            ParseTypeName(createdType.ToMinimalDisplayString(model, creation.SpanStart)),
                            implicitCreation.ArgumentList,
                            implicitCreation.Initializer)
                        .WithoutTrivia(),
                    _ => null,
                };
                if (explicitCreation is null)
                    return default;

                newCreation = SafeXmlCall(ParenthesizedLambdaExpression(explicitCreation), argument);
            }

            var newCreationWithTrivia = newCreation.WithTriviaFrom(creation).WithAdditionalAnnotations(Formatter.Annotation);
            return ImmutableArray.Create(new Edit(creation, newCreationWithTrivia), new Edit(statement, null));
        }

        private static bool HasCreateDocumentOverloads(INamedTypeSymbol safeXml)
        {
            var methods = safeXml.GetMembers("CreateDocument").OfType<IMethodSymbol>().Where(m => m.IsStatic && m.DeclaredAccessibility == Accessibility.Public).ToList();
            bool fromString = methods.Any(m => m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_String);
            bool fromFactory = methods.Any(m => m.IsGenericMethod && m.Parameters.Length == 2 && m.Parameters[1].Type.SpecialType == SpecialType.System_String);
            return fromString && fromFactory;
        }

        private static bool TryGetPreviousStatement(StatementSyntax statement, out StatementSyntax previous)
        {
            SyntaxList<StatementSyntax> statements;
            switch (statement.Parent)
            {
                case BlockSyntax block:
                    statements = block.Statements;
                    break;
                case SwitchSectionSyntax section:
                    statements = section.Statements;
                    break;
                default:
                    previous = null!;
                    return false;
            }

            var index = statements.IndexOf(statement);
            previous = index > 0 ? statements[index - 1] : null!;
            return index > 0;
        }

        /// <summary>The <c>new XmlDocument(...)</c> that <paramref name="previous"/> stores into <paramref name="target"/>.</summary>
        private static BaseObjectCreationExpressionSyntax? GetDocumentCreation(StatementSyntax previous, SemanticModel model, ISymbol target, CancellationToken cancellationToken)
        {
            switch (previous)
            {
                case LocalDeclarationStatementSyntax { Declaration.Variables: { Count: 1 } variables }
                    when variables[0].Initializer?.Value is BaseObjectCreationExpressionSyntax creation
                         && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(variables[0], cancellationToken), target):
                    return creation;

                case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment }
                    when assignment.Right is BaseObjectCreationExpressionSyntax creation
                         && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol, target):
                    return creation;

                default:
                    return null;
            }
        }

        private static bool IsPlainXmlDocument(BaseObjectCreationExpressionSyntax creation, ITypeSymbol createdType) =>
            createdType.Name == "XmlDocument"
            && createdType.ContainingNamespace?.ToDisplayString() == "System.Xml"
            && (creation.ArgumentList is null || creation.ArgumentList.Arguments.Count == 0)
            && creation.Initializer is null;

        private static InvocationExpressionSyntax SafeXmlCall(params ExpressionSyntax[] arguments) =>
            InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, QualifiedType(SafeXmlMetadataName), IdentifierName("CreateDocument")),
                ArgumentList(SeparatedList(arguments.Select(Argument))));

        /// <summary><c>new XmlTextReader(source) { DtdProcessing = DtdProcessing.Prohibit }</c>.</summary>
        private static ExpressionSyntax CreateTextReader(ExpressionSyntax source) =>
            ObjectCreationExpression(QualifiedType("System.Xml.XmlTextReader"))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(source))))
                .WithInitializer(InitializerExpression(
                    SyntaxKind.ObjectInitializerExpression,
                    SingletonSeparatedList<ExpressionSyntax>(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        IdentifierName("DtdProcessing"),
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, QualifiedType("System.Xml.DtdProcessing"), IdentifierName("Prohibit"))))));

        /// <summary>A fully qualified type that the cleanup reduces to its short name, adding the using it needs.</summary>
        private static TypeSyntax QualifiedType(string metadataName) =>
            ParseTypeName("global::" + metadataName).WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);

        private static string GetUniqueName(SemanticModel model, StatementSyntax statement, string baseName)
        {
            // Names visible here, plus names declared anywhere in the enclosing body (a later declaration would clash too).
            var body = statement.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) ?? statement.Parent!;
            var taken = model.LookupSymbols(statement.SpanStart).Select(s => s.Name)
                .Concat(body.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(v => v.Identifier.ValueText))
                .Concat(body.DescendantNodes().OfType<SingleVariableDesignationSyntax>().Select(v => v.Identifier.ValueText))
                .ToImmutableHashSet();

            var name = baseName;
            for (int i = 1; taken.Contains(name); i++)
                name = baseName + i;
            return name;
        }

    }
}
