using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Shitalyzer
{
    /// <summary>
    /// SHIT0011: flags an NUnit constraint member that the Java NUnit shim (<c>com.devexpress.nunit.framework</c> in
    /// <c>devexpress-bcl-nunit</c>) does not implement. cs2j converts <c>Is.Positive</c> into <c>Is.getPositive()</c>
    /// without checking that the member exists, so the converted test fails in javac (dxvcs PR #54231).
    /// The analyzer follows a constraint chain from <c>Is</c>, <c>Has</c>, <c>Does</c> or <c>Throws</c> through
    /// <see cref="Shim"/>, a model of the shim types, and reports the first member the current shim type lacks.
    /// <c>Assume</c> and <c>Iz</c> have no shim class at all, so every member of theirs is reported.
    /// The model is read with <c>javap</c> from <c>devexpress-bcl-nunit 26.2.0.144</c> (the <c>bclVersion</c> dxvcs pins on
    /// 2026-10-30). Run over the C# of all 52 Java-mirrored NUnit test projects in dxvcs it rejects no constraint chain.
    /// Overloads, constraint operators (<c>!</c>, <c>&amp;</c>, <c>|</c>) and constraints kept in variables are not checked.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NUnitShimMemberNotSupportedAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>Diagnostic property: the member that replaces the reported one (see <see cref="Replacements"/>).</summary>
        internal const string ReplacementKey = "Replacement";

        /// <summary>Diagnostic property: <c>true</c> when the reported member is a property that becomes a call with a <c>0</c> argument.</summary>
        internal const string AddZeroArgumentKey = "AddZeroArgument";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticIds.NUnitShimMemberNotSupported,
            title: "NUnit member is missing in the Java NUnit shim",
            messageFormat: "'{0}' has no counterpart in the Java NUnit shim (devexpress-bcl-nunit), so the converted test does not compile; {1}",
            category: Categories.Conversion,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "cs2j maps NUnit constraints onto a hand-written Java shim that implements only part of NUnit. A member missing there converts into a call javac cannot resolve.");

        private const string C = "Constraint";
        private const string EqualTo = "EqualToConstraint";
        private const string ComposedEqualTo = "ComposedEqualToConstraint";
        private const string Not = "Not";
        private const string And = "AndConstraintExpression";
        private const string AndNot = "AndNotConstraintExpression";
        private const string Or = "OrConstraintExpression";
        private const string ThrowsConstraint = "ThrowsConstraint";
        private const string Message = "MessageConstraintExpression";
        private const string AllItems = "AllItemsConstraintExpression";
        private const string NoItems = "NoItemsConstraintExpression";
        private const string Exactly = "ExactlyConstraint";
        private static readonly string[] Comparisons = { "GreaterThan", "GreaterThanOrEqualTo", "LessThan", "LessThanOrEqualTo" };

        /// <summary>
        /// Shim type -> (C# member name -> shim type the member returns). A Java getter <c>getX()</c> is the C# property
        /// <c>X</c>, a Java method <c>x(...)</c> the C# method <c>X</c>; <c>isInstanceOf</c> is <c>InstanceOf</c>.
        /// Every <c>internal.Constraint</c> inherits <c>Or</c>/<c>And</c> from the interface's default methods.
        /// </summary>
        private static readonly ImmutableDictionary<string, ImmutableDictionary<string, string>> Shim =
            new Dictionary<string, ImmutableDictionary<string, string>>
            {
                ["Is"] = Members(Same(C, "True", "False", "SameAs", "TypeOf", "InstanceOf", "SubsetOf", "AtLeast", "AtMost",
                    "EquivalentTo", "Empty", "Zero", "Unique"), Same(C, Comparisons),
                    Same(EqualTo, "Null", "EqualTo"), Same(Not, "Not"), Same(AllItems, "All")),
                [Not] = Members(Same(C, "True", "False", "SameAs", "TypeOf", "InstanceOf", "Empty", "Zero"), Same(C, Comparisons),
                    Same(EqualTo, "Null", "EqualTo", "Exactly")),
                ["Has"] = Members(Same(C, "Member"), Same("CountConstraintExpression", "Count"), Same("NotHas", "Not"),
                    Same(Exactly, "Exactly"), Same("LengthConstraintExpression", "Length"),
                    Same("NoneConstraintExpression", "None"), Same(AllItems, "All"), Same(NoItems, "No")),
                ["NotHas"] = Members(Same(Exactly, "Exactly"), Same(C, "Member")),
                ["Does"] = Members(Same(C, "Contain", "ContainKey", "StartWith", "EndWith"), Same("NotDoes", "Not")),
                ["NotDoes"] = Members(Same(C, "Contain", "ContainKey", "StartWith", "EndWith")),
                ["Throws"] = Members(Same(ThrowsConstraint, "Exception", "TypeOf", "InstanceOf", "ArgumentException",
                    "ArgumentNullException", "InvalidOperationException"), Same(C, "Nothing")),
                [ThrowsConstraint] = Members(Same(ThrowsConstraint, "TypeOf", "ExactTypeOf", "InstanceOf", "Property",
                    "EqualTo", "Contains", "With"), Same(Message, "Message"), Same(And, "And"), Same(Or, "Or")),
                [Message] = Members(Same(C, "EqualTo", "Contains")),
                [EqualTo] = Members(Same(EqualTo, "Within", "Using", "Percent", "IgnoreCase"), Same(Or, "Or"), Same(And, "And")),
                [ComposedEqualTo] = Members(Same(ComposedEqualTo, "Within", "Percent"), Same(Or, "Or"), Same(And, "And")),
                [C] = Members(Same(Or, "Or"), Same(And, "And")),
                [Or] = Members(Same(ComposedEqualTo, "EqualTo"), Same(C, Comparisons), Same(C, "InstanceOf", "Null", "Empty")),
                [And] = Members(Same(C, "True", "False", "Null", "Member", "InstanceOf", "EndsWith", "EndWith", "StartsWith",
                    "StartWith", "Contains", "Contain"), Same(C, Comparisons), Same(AndNot, "Not"),
                    Same(ComposedEqualTo, "EqualTo"), Same(NoItems, "No"), Same(Message, "Message")),
                [AndNot] = Members(Same(C, "True", "False", "Null", "EqualTo", "SameAs", "Empty")),
                [NoItems] = Members(Same(C, "Member")),
                ["LengthConstraintExpression"] = Members(Same(EqualTo, "EqualTo"), Same(C, Comparisons), Same(C, "AtLeast", "AtMost")),
                ["NoneConstraintExpression"] = Members(Same(C, "TypeOf", "InstanceOf", "EqualTo")),
                [Exactly] = Members(Same(C, "Items", "InstanceOf", "EqualTo"), Same(Or, "Or"), Same(And, "And")),
                ["CountConstraintExpression"] = Members(Same(Not, "Not"), Same(EqualTo, "EqualTo", "Exactly"), Same(C, Comparisons)),
                [AllItems] = Members(Same(C, "True", "False", "Null", "EqualTo", "Matches"), Same("AllItemsNotExpression", "Not")),
                ["AllItemsNotExpression"] = Members(Same(C, "Null", "EqualTo")),
            }.ToImmutableDictionary();

        /// <summary>NUnit classes with no shim counterpart at all.</summary>
        private static readonly ImmutableHashSet<string> MissingRoots = ImmutableHashSet.Create("Assume", "Iz");

        /// <summary>
        /// Mechanical rewrites onto members the shim has, keyed by (shim type, member). The shim compares numbers through
        /// <c>Number.doubleValue()</c>, so an <c>int</c> <c>0</c> is right for every numeric type.
        /// </summary>
        private static readonly ImmutableDictionary<(string Type, string Member), (string Replacement, bool AddZeroArgument)> Replacements =
            new Dictionary<(string, string), (string, bool)>
            {
                [("Is", "Positive")] = ("GreaterThan", true),
                [("Is", "Negative")] = ("LessThan", true),
                [(Not, "Positive")] = ("GreaterThan", true),
                [(Not, "Negative")] = ("LessThan", true),
                [("CountConstraintExpression", "AtLeast")] = ("GreaterThanOrEqualTo", false),
                [("CountConstraintExpression", "AtMost")] = ("LessThanOrEqualTo", false),
            }.ToImmutableDictionary();

        private static readonly ImmutableHashSet<string> RootNames = Shim.Keys.Where(key => key is "Is" or "Has" or "Does" or "Throws")
            .Concat(MissingRoots).ToImmutableHashSet();

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
        }

        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            // Each access in a chain is analyzed on its own and reports only on its own name, so a diagnostic stays inside
            // the node that raised it. An access below an earlier missing member resolves to nothing and stays silent.
            var memberAccess = (MemberAccessExpressionSyntax)context.Node;
            if (IsRoot(memberAccess.Expression, context) is { } root)
            {
                var path = new List<string> { root };
                if (MissingRoots.Contains(root))
                    Report(context, memberAccess, path, type: null);
                else if (!Shim[root].ContainsKey(memberAccess.Name.Identifier.ValueText))
                    Report(context, memberAccess, path, root);
                return;
            }

            var receiver = memberAccess.Expression is InvocationExpressionSyntax invocation ? invocation.Expression : memberAccess.Expression;
            if (receiver is not MemberAccessExpressionSyntax receiverAccess || !receiverAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression))
                return;
            var chain = new List<string>();
            if (Resolve(receiverAccess, context, chain) is { } type && !Shim[type].ContainsKey(memberAccess.Name.Identifier.ValueText))
                Report(context, memberAccess, chain, type);
        }

        /// <summary>The shim type <paramref name="memberAccess"/> evaluates to, or <c>null</c> when it is not an NUnit constraint chain or the shim lacks a member of it.</summary>
        private static string? Resolve(MemberAccessExpressionSyntax memberAccess, SyntaxNodeAnalysisContext context, List<string> path)
        {
            string? type;
            if (IsRoot(memberAccess.Expression, context) is { } root)
            {
                if (MissingRoots.Contains(root))
                    return null;
                path.Add(root);
                type = root;
            }
            else
            {
                var receiver = memberAccess.Expression is InvocationExpressionSyntax invocation ? invocation.Expression : memberAccess.Expression;
                if (receiver is not MemberAccessExpressionSyntax receiverAccess || !receiverAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression))
                    return null;
                type = Resolve(receiverAccess, context, path);
                if (type is null)
                    return null;
            }

            var name = memberAccess.Name.Identifier.ValueText;
            if (!Shim[type].TryGetValue(name, out var next))
                return null;
            path.Add(name);
            return next;
        }

        /// <summary>The name of the NUnit class <paramref name="expression"/> names when it is one of <see cref="RootNames"/>; otherwise <c>null</c>.</summary>
        private static string? IsRoot(ExpressionSyntax expression, SyntaxNodeAnalysisContext context)
        {
            var name = expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax qualified => qualified.Name.Identifier.ValueText,
                _ => null,
            };
            if (name is null || !RootNames.Contains(name))
                return null;
            return context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol is INamedTypeSymbol type
                && type.ContainingAssembly?.Name == "nunit.framework"
                && type.ContainingNamespace?.ToDisplayString() == "NUnit.Framework"
                ? type.Name
                : null;
        }

        private static void Report(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax memberAccess, List<string> path, string? type)
        {
            var name = memberAccess.Name.Identifier.ValueText;
            var properties = ImmutableDictionary<string, string?>.Empty;
            string hint;
            if (type is not null && Replacements.TryGetValue((type, name), out var replacement))
            {
                properties = properties
                    .Add(ReplacementKey, replacement.Replacement)
                    .Add(AddZeroArgumentKey, replacement.AddZeroArgument ? "true" : "false");
                hint = replacement.AddZeroArgument ? $"use '{replacement.Replacement}(0)'" : $"use '{replacement.Replacement}'";
            }
            else if (type is null)
                hint = "rewrite it without this class";
            else
                hint = "use a constraint the shim has: " + string.Join(", ", Shim[type].Keys.OrderBy(key => key));

            context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.Name.GetLocation(), properties,
                string.Join(".", path) + "." + name, hint));
        }

        private static ImmutableDictionary<string, string> Members(params IEnumerable<KeyValuePair<string, string>>[] groups) =>
            groups.SelectMany(group => group).ToImmutableDictionary();

        private static IEnumerable<KeyValuePair<string, string>> Same(string type, params string[] names) =>
            names.Select(name => new KeyValuePair<string, string>(name, type));
    }
}
