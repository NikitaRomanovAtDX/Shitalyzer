using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0012: XML calls that CA3075 (insecure DTD processing) reports only when the compilation targets
    /// .NET Framework. The CA3075 analyzer registers nothing unless <c>System.String</c> lives in
    /// <c>mscorlib</c>, so a .NET (Core) or .NET Standard build never shows these warnings, while the
    /// .NET Framework 4.7.2 leg of the same multi-targeted code does (and fails under TreatWarningsAsErrors).
    /// <para>
    /// The rule mirrors what CA3075 reports on .NET Framework 4.5.2 and later, minus the calls that the
    /// cross-target rules CA5366/CA5369/CA5371/CA5372 already report everywhere (DataSet, XmlSerializer,
    /// XmlSchema, XPathDocument):
    /// <list type="bullet">
    /// <item><c>XmlDocument.Load</c>/<c>LoadXml</c> and <c>DataTable.ReadXml</c>/<c>ReadXmlSchema</c> overloads without an <c>XmlReader</c>;</item>
    /// <item>setting <c>XmlDocument.InnerXml</c>;</item>
    /// <item><c>new XmlTextReader(...)</c> whose <c>DtdProcessing</c> is not set in the same member;</item>
    /// <item><c>XmlTextReader.DtdProcessing = DtdProcessing.Parse</c>;</item>
    /// <item><c>XmlResolver</c> of an <c>XmlDocument</c>/<c>XmlTextReader</c> set to anything but <c>null</c> or an <c>XmlSecureResolver</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// On a .NET Framework compilation the rule stays silent because CA3075 itself reports there. A CA3075
    /// suppression (<c>#pragma warning disable CA3075</c> or <c>[SuppressMessage(..., "CA3075...")]</c>) silences
    /// this rule too, so code that already reasoned about CA3075 is not flagged twice.
    /// </para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NetFrameworkInsecureXmlAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>Property key: the kind of <c>XmlDocument</c> input the code fix can route through a safe reader.</summary>
        internal const string LoadKindKey = "LoadKind";

        /// <summary><c>LoadXml(string)</c> or <c>InnerXml = string</c>: the argument is the XML text.</summary>
        internal const string LoadKindXml = "Xml";
        /// <summary><c>Load(Stream)</c>.</summary>
        internal const string LoadKindStream = "Stream";
        /// <summary><c>Load(TextReader)</c>.</summary>
        internal const string LoadKindTextReader = "TextReader";
        /// <summary><c>Load(string)</c>: the argument is a file name or URL, so the reader owns a file handle.</summary>
        internal const string LoadKindPath = "Path";

        private const string Ca3075 = "CA3075";
        private const string XmlNamespace = "System.Xml";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.NetFrameworkInsecureXml,
            title: "XML call reported by CA3075 only on the .NET Framework leg",
            messageFormat: "{0}; CA3075 reports this only when compiling for .NET Framework",
            category: Categories.Compatibility,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "CA3075 (insecure DTD processing) runs only on .NET Framework compilations, so a .NET build of multi-targeted code never shows it while the .NET Framework 4.7.2 leg does. This rule reports the same calls on the other targets. Load XML through an XmlReader with DTD processing prohibited (for example DevExpress.Utils.SafeXml), or set XmlTextReader.DtdProcessing to Prohibit or Ignore.",
            helpLinkUri: "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca3075");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var compilation = context.Compilation;

            // CA3075 analyzes exactly the compilations whose System.String comes from mscorlib; there it reports
            // these calls itself, and repeating them would only double the warnings.
            if (compilation.GetSpecialType(SpecialType.System_String).ContainingAssembly?.Name == "mscorlib")
                return;

            var types = new XmlTypes(compilation);
            if (types.XmlDocument is null && types.XmlTextReader is null && types.DataTable is null)
                return;

            context.RegisterOperationBlockStartAction(blockContext =>
            {
                var block = new BlockAnalyzer(types);
                blockContext.RegisterOperationAction(block.AnalyzeInvocation, OperationKind.Invocation);
                blockContext.RegisterOperationAction(block.AnalyzeAssignment, OperationKind.SimpleAssignment);
                blockContext.RegisterOperationAction(block.AnalyzeObjectCreation, OperationKind.ObjectCreation);
                blockContext.RegisterOperationBlockEndAction(block.AnalyzeBlockEnd);
            });
        }

        private sealed class XmlTypes
        {
            public XmlTypes(Compilation compilation)
            {
                XmlDocument = compilation.GetTypeByMetadataName("System.Xml.XmlDocument");
                XmlReader = compilation.GetTypeByMetadataName("System.Xml.XmlReader");
                XmlTextReader = compilation.GetTypeByMetadataName("System.Xml.XmlTextReader");
                XmlSecureResolver = compilation.GetTypeByMetadataName("System.Xml.XmlSecureResolver");
                DtdProcessing = compilation.GetTypeByMetadataName("System.Xml.DtdProcessing");
                DataTable = compilation.GetTypeByMetadataName("System.Data.DataTable");
                Stream = compilation.GetTypeByMetadataName("System.IO.Stream");
                TextReader = compilation.GetTypeByMetadataName("System.IO.TextReader");
            }

            public INamedTypeSymbol? XmlDocument { get; }
            public INamedTypeSymbol? XmlReader { get; }
            public INamedTypeSymbol? XmlTextReader { get; }
            public INamedTypeSymbol? XmlSecureResolver { get; }
            public INamedTypeSymbol? DtdProcessing { get; }
            public INamedTypeSymbol? DataTable { get; }
            public INamedTypeSymbol? Stream { get; }
            public INamedTypeSymbol? TextReader { get; }
        }

        /// <summary>
        /// Per member state: CA3075 tracks an <c>XmlTextReader</c> from its creation to the end of the member
        /// and reports it when its <c>DtdProcessing</c> was never set there. The driver may run the actions of
        /// one block concurrently, hence the lock.
        /// </summary>
        private sealed class BlockAnalyzer
        {
            private readonly XmlTypes types;
            private readonly object gate = new object();
            private readonly List<(ISymbol Owner, IObjectCreationOperation Creation)> trackedReaders = new List<(ISymbol, IObjectCreationOperation)>();
            private readonly HashSet<ISymbol> readersWithDtdProcessing = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            public BlockAnalyzer(XmlTypes types)
            {
                this.types = types;
            }

            public void AnalyzeInvocation(OperationAnalysisContext context)
            {
                var invocation = (IInvocationOperation)context.Operation;
                var method = invocation.TargetMethod;

                bool isDocumentLoad = (method.Name == "Load" || method.Name == "LoadXml") && DerivesFrom(method.ContainingType, types.XmlDocument);
                bool isTableRead = (method.Name == "ReadXml" || method.Name == "ReadXmlSchema") && DerivesFrom(method.ContainingType, types.DataTable);
                if (!isDocumentLoad && !isTableRead)
                    return;
                if (method.Parameters.Any(p => DerivesFrom(p.Type, types.XmlReader)))
                    return;

                var properties = ImmutableDictionary<string, string?>.Empty;
                var loadKind = isDocumentLoad && !method.IsStatic ? GetLoadKind(method) : null;
                if (loadKind is not null)
                    properties = properties.Add(LoadKindKey, loadKind);

                var message = $"'{FormatMethod(method)}' parses DTDs; load the XML through an XmlReader with DTD processing prohibited";
                Report(context, GetNameLocation(invocation.Syntax), properties, message);
            }

            public void AnalyzeAssignment(OperationAnalysisContext context)
            {
                var assignment = (ISimpleAssignmentOperation)context.Operation;
                if (assignment.Target is not IPropertyReferenceOperation propertyReference)
                    return;

                var property = propertyReference.Property;
                var value = assignment.Value;

                if (property.Name == "InnerXml" && DerivesFrom(property.ContainingType, types.XmlDocument))
                {
                    // XmlDocument.InnerXml's setter is LoadXml(value), so the same rewrite applies.
                    var properties = ImmutableDictionary<string, string?>.Empty.Add(LoadKindKey, LoadKindXml);
                    Report(context, assignment.Syntax.GetLocation(), properties, $"Setting '{property.ContainingType.Name}.InnerXml' parses DTDs; load the XML through an XmlReader with DTD processing prohibited");
                    return;
                }

                if (property.Name == "XmlResolver"
                    && (DerivesFrom(property.ContainingType, types.XmlDocument) || DerivesFrom(property.ContainingType, types.XmlTextReader)))
                {
                    if (!IsSecureResolver(value))
                        Report(context, assignment.Syntax.GetLocation(), null, $"'{property.ContainingType.Name}.XmlResolver' is set to a resolver other than null or an XmlSecureResolver");
                    return;
                }

                if (property.Name == "DtdProcessing" && DerivesFrom(property.ContainingType, types.XmlTextReader))
                {
                    if (IsDtdProcessingParse(value))
                        Report(context, assignment.Syntax.GetLocation(), null, $"'{property.ContainingType.Name}.DtdProcessing' is set to DtdProcessing.Parse");

                    // Initializer assignments are read by AnalyzeObjectCreation; this records later assignments.
                    if (GetReferencedSymbol(propertyReference.Instance) is { } owner)
                    {
                        lock (gate)
                            readersWithDtdProcessing.Add(owner);
                    }
                }
            }

            public void AnalyzeObjectCreation(OperationAnalysisContext context)
            {
                var creation = (IObjectCreationOperation)context.Operation;

                // CA3075 trusts XmlTextReader subclasses to configure themselves.
                if (types.XmlTextReader is null || !SymbolEqualityComparer.Default.Equals(creation.Type, types.XmlTextReader))
                    return;

                if (creation.Initializer is { } initializer
                    && initializer.Initializers.OfType<ISimpleAssignmentOperation>().Any(a =>
                        a.Target is IPropertyReferenceOperation { Property.Name: "DtdProcessing" }))
                {
                    return;
                }

                var owner = GetOwner(creation);
                if (owner is null)
                {
                    // A temporary reader cannot get its DtdProcessing set later.
                    ReportUnsetDtdProcessing(context.ReportDiagnostic, context.ContainingSymbol, creation, context.CancellationToken);
                    return;
                }

                lock (gate)
                    trackedReaders.Add((owner, creation));
            }

            public void AnalyzeBlockEnd(OperationBlockAnalysisContext context)
            {
                lock (gate)
                {
                    foreach (var (owner, creation) in trackedReaders)
                    {
                        if (!readersWithDtdProcessing.Contains(owner))
                            ReportUnsetDtdProcessing(context.ReportDiagnostic, context.OwningSymbol, creation, context.CancellationToken);
                    }
                }
            }

            private static void ReportUnsetDtdProcessing(System.Action<Diagnostic> report, ISymbol containingSymbol, IObjectCreationOperation creation, CancellationToken cancellationToken)
            {
                var location = creation.Syntax is ObjectCreationExpressionSyntax explicitCreation
                    ? explicitCreation.Type.GetLocation()
                    : creation.Syntax.GetLocation();
                Report(report, containingSymbol, location, null, "'XmlTextReader' is created without setting DtdProcessing to Prohibit or Ignore", cancellationToken);
            }

            private string? GetLoadKind(IMethodSymbol method)
            {
                if (method.Parameters.Length != 1)
                    return null;

                var type = method.Parameters[0].Type;
                if (type.SpecialType == SpecialType.System_String)
                    return method.Name == "LoadXml" ? LoadKindXml : LoadKindPath;
                if (DerivesFrom(type, types.Stream))
                    return LoadKindStream;
                if (DerivesFrom(type, types.TextReader))
                    return LoadKindTextReader;
                return null;
            }

            private bool IsSecureResolver(IOperation value)
            {
                var operand = WithoutConversions(value);
                if (operand.ConstantValue is { HasValue: true, Value: null })
                    return true;
                return operand.Type is not null && DerivesFrom(operand.Type, types.XmlSecureResolver);
            }

            private bool IsDtdProcessingParse(IOperation value)
            {
                var operand = WithoutConversions(value);
                return operand.Type is not null
                    && SymbolEqualityComparer.Default.Equals(operand.Type, types.DtdProcessing)
                    && operand.ConstantValue is { HasValue: true, Value: int raw }
                    && raw == 2; // DtdProcessing.Parse
            }

            private static void Report(OperationAnalysisContext context, Location location, ImmutableDictionary<string, string?>? properties, string message) =>
                Report(context.ReportDiagnostic, context.ContainingSymbol, location, properties, message, context.CancellationToken);

            /// <summary>Every report goes through here, so a CA3075 suppression silences all of them.</summary>
            private static void Report(System.Action<Diagnostic> report, ISymbol containingSymbol, Location location, ImmutableDictionary<string, string?>? properties, string message, CancellationToken cancellationToken)
            {
                if (IsCa3075Suppressed(containingSymbol, location, cancellationToken))
                    return;
                report(Diagnostic.Create(Rule, location, properties, message));
            }
        }

        /// <summary>The local, field, parameter or property a freshly created reader is stored in, or null for a temporary.</summary>
        private static ISymbol? GetOwner(IOperation creation)
        {
            var current = creation;
            while (current.Parent is IConversionOperation conversion)
                current = conversion;

            switch (current.Parent)
            {
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator }:
                    return declarator.Symbol;
                case IFieldInitializerOperation { InitializedFields.Length: 1 } fieldInitializer:
                    return fieldInitializer.InitializedFields[0];
                case IPropertyInitializerOperation { InitializedProperties.Length: 1 } propertyInitializer:
                    return propertyInitializer.InitializedProperties[0];
                case ISimpleAssignmentOperation assignment when assignment.Value == current:
                    return GetReferencedSymbol(assignment.Target);
                default:
                    return null;
            }
        }

        private static ISymbol? GetReferencedSymbol(IOperation? operation) => operation switch
        {
            ILocalReferenceOperation local => local.Local,
            IFieldReferenceOperation field => field.Field,
            IParameterReferenceOperation parameter => parameter.Parameter,
            IPropertyReferenceOperation property => property.Property,
            _ => null,
        };

        private static IOperation WithoutConversions(IOperation operation)
        {
            while (operation is IConversionOperation conversion)
                operation = conversion.Operand;
            return operation;
        }

        private static bool DerivesFrom(ITypeSymbol? type, INamedTypeSymbol? baseType)
        {
            if (baseType is null)
                return false;
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                    return true;
            }
            return false;
        }

        private static string FormatMethod(IMethodSymbol method)
        {
            var parameters = string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            return $"{method.ContainingType.Name}.{method.Name}({parameters})";
        }

        private static Location GetNameLocation(SyntaxNode syntax)
        {
            if (syntax is InvocationExpressionSyntax invocation)
            {
                switch (invocation.Expression)
                {
                    case MemberAccessExpressionSyntax memberAccess:
                        return memberAccess.Name.GetLocation();
                    case MemberBindingExpressionSyntax memberBinding:
                        return memberBinding.Name.GetLocation();
                }
            }
            return syntax.GetLocation();
        }

        /// <summary>
        /// Whether CA3075 is suppressed at <paramref name="location"/> by a <c>#pragma warning disable</c> in effect
        /// there, or by a <c>SuppressMessage</c> attribute on an enclosing member, type or the assembly.
        /// </summary>
        private static bool IsCa3075Suppressed(ISymbol containingSymbol, Location location, CancellationToken cancellationToken)
        {
            return IsSuppressedByPragma(location, cancellationToken) || IsSuppressedByAttribute(containingSymbol);
        }

        private static bool IsSuppressedByPragma(Location location, CancellationToken cancellationToken)
        {
            var tree = location.SourceTree;
            if (tree is null)
                return false;

            var position = location.SourceSpan.Start;
            var root = tree.GetRoot(cancellationToken);
            bool suppressed = false;

            // Directives inside inactive #if regions are disabled text, not directives, so they are skipped here
            // exactly as the compiler skips them.
            for (var directive = root.GetFirstDirective(); directive is not null && directive.SpanStart < position; directive = directive.GetNextDirective())
            {
                if (directive is not PragmaWarningDirectiveTriviaSyntax pragma)
                    continue;

                bool appliesToCa3075 = pragma.ErrorCodes.Count == 0
                    || pragma.ErrorCodes.OfType<IdentifierNameSyntax>().Any(code => code.Identifier.ValueText == Ca3075);
                if (appliesToCa3075)
                    suppressed = pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword);
            }

            return suppressed;
        }

        private static bool IsSuppressedByAttribute(ISymbol? symbol)
        {
            for (var current = symbol; current is not null; current = current.ContainingSymbol)
            {
                if (HasSuppressMessage(current.GetAttributes()))
                    return true;
                if (current is IAssemblySymbol)
                    break;
            }
            return false;
        }

        private static bool HasSuppressMessage(ImmutableArray<AttributeData> attributes)
        {
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeClass is not { Name: "SuppressMessageAttribute" or "UnconditionalSuppressMessageAttribute" })
                    continue;
                if (attribute.ConstructorArguments.Length < 2 || attribute.ConstructorArguments[1].Value is not string checkId)
                    continue;

                // Assembly-level suppressions aimed at a specific member carry a Target; those are not matched here.
                if (attribute.NamedArguments.Any(a => a.Key == "Target"))
                    continue;

                // "CA3075", "CA3075:Insecure DTD processing" and "CA3075 ..." all name the rule.
                if (checkId == Ca3075 || checkId.StartsWith(Ca3075 + ":", System.StringComparison.Ordinal) || checkId.StartsWith(Ca3075 + " ", System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
