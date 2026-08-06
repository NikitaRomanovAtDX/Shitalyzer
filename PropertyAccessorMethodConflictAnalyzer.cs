using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0006: flags a property that coexists with an explicit <c>Get</c>/<c>Set</c> method whose name
    /// mirrors the property. Our C#-to-Java converter maps a property <c>MyName</c> to <c>getMyName()</c>/
    /// <c>setMyName()</c>, so an explicit <c>GetMyName</c>/<c>SetMyName</c> method — declared in the same
    /// class or any base class — collides with the generated accessor.
    /// <para>
    /// A property with a getter forbids <c>GetMyName</c>; a property with a setter forbids <c>SetMyName</c>.
    /// A read-only property (<c>MyName { get; }</c>) therefore only forbids <c>GetMyName</c>.
    /// </para>
    /// <para>
    /// A conflicting method decorated with <c>[JavaRename("...")]</c> is exempt: the converter emits it under
    /// a different name in Java, so it no longer collides with the generated accessor.
    /// </para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PropertyAccessorMethodConflictAnalyzer : DiagnosticAnalyzer
    {
        // A method decorated with [JavaRename("...")] is emitted under a different name in Java, so it
        // no longer collides with the generated getX()/setX() accessor.
        private const string JavaRenameAttributeName = "DevExpress.Data.JavaConversion.Internal.JavaRenameAttribute";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.PropertyAccessorMethodConflict,
            title: "Property collides with a Get/Set method",
            messageFormat: "Property '{0}' cannot coexist with method '{1}'; the Java converter generates '{1}' from the property",
            category: Categories.Conversion,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Our code converts to Java, where property 'MyName' becomes 'getMyName()'/'setMyName()'. Declaring an explicit 'GetMyName'/'SetMyName' method in the same class or a base class collides with the generated accessor. A property with a getter forbids 'GetMyName'; one with a setter forbids 'SetMyName'.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
        }

        private static void AnalyzeNamedType(SymbolAnalysisContext context)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.TypeKind != TypeKind.Class)
                return;

            foreach (var member in type.GetMembers())
            {
                if (member is not IPropertySymbol property || property.IsIndexer)
                    continue;

                // The Java converter turns the property's accessors into getX()/setX(); an explicit
                // method of the same name would clash. A read-only property only forbids GetX.
                if (property.GetMethod is not null)
                    ReportIfMethodExists(context, type, property, "Get" + property.Name);
                if (property.SetMethod is not null)
                    ReportIfMethodExists(context, type, property, "Set" + property.Name);
            }
        }

        /// <summary>
        /// Reports a diagnostic when a source-declared ordinary method named <paramref name="methodName"/>
        /// exists on <paramref name="declaringType"/> or any of its base classes. Methods inherited from
        /// metadata (e.g. <c>object.GetType()</c>) are ignored — they are not part of the C#-to-Java
        /// conversion, so, for example, a <c>Type</c> property does not collide with <c>object.GetType()</c>.
        /// </summary>
        private static void ReportIfMethodExists(SymbolAnalysisContext context, INamedTypeSymbol declaringType, IPropertySymbol property, string methodName)
        {
            for (var current = declaringType; current is not null; current = current.BaseType)
            {
                var hasConflict = current.GetMembers(methodName)
                    .OfType<IMethodSymbol>()
                    .Any(m => m.MethodKind == MethodKind.Ordinary && m.DeclaringSyntaxReferences.Length > 0 && !HasJavaRenameAttribute(m));
                if (hasConflict)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, property.Locations[0], property.Name, methodName));
                    return;
                }
            }
        }

        private static bool HasJavaRenameAttribute(IMethodSymbol method)
        {
            return method.GetAttributes()
                .Any(a => a.AttributeClass?.ToDisplayString() == JavaRenameAttributeName);
        }
    }
}
