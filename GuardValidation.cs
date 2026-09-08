using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Shitalyzer
{
    /// <summary>
    /// The <c>Guard</c> call a hand-written argument check should become. <see cref="MethodName"/> is
    /// <see langword="null"/> for a pattern that is worth reporting but that cannot be rewritten
    /// mechanically (see <see cref="ReportOnly"/>).
    /// </summary>
    internal sealed class GuardRewrite
    {
        /// <summary>Manual validation with no automatic rewrite; the move to <c>Guard</c> is done by hand.</summary>
        public static readonly GuardRewrite ReportOnly = new GuardRewrite(null, ImmutableArray<ExpressionSyntax>.Empty);

        private GuardRewrite(string? methodName, ImmutableArray<ExpressionSyntax> arguments)
        {
            MethodName = methodName;
            Arguments = arguments;
        }

        public static GuardRewrite Call(string methodName, params ExpressionSyntax[] arguments) =>
            new GuardRewrite(methodName, arguments.ToImmutableArray());

        /// <summary>Name of the <c>Guard</c> member to call, or <see langword="null"/> when there is no fix.</summary>
        public string? MethodName { get; }

        /// <summary>The full argument list of the <c>Guard</c> call, in declaration order.</summary>
        public ImmutableArray<ExpressionSyntax> Arguments { get; }
    }

    /// <summary>
    /// Recognizes hand-written argument validation and maps it onto the <c>Guard</c> helper
    /// (<c>DevExpress.Utils.Guard</c>, or <c>DevExpress.Mvvm.Native.GuardHelper</c> in MVVM builds).
    /// Shared by <see cref="ManualArgumentValidationAnalyzer"/> and
    /// <see cref="ManualArgumentValidationCodeFixProvider"/> so that both agree, exactly, on which
    /// patterns are fixable and on what the replacement looks like.
    /// <para>
    /// Three syntax shapes are matched:
    /// <list type="number">
    /// <item>a BCL throw helper — <c>ArgumentNullException.ThrowIfNull(x)</c>, <c>ArgumentException.ThrowIfNullOrEmpty(s)</c>,
    /// <c>ArgumentOutOfRangeException.ThrowIfNegative(i)</c>, …;</item>
    /// <item><c>if (condition) throw new Argument*Exception(…);</c>;</item>
    /// <item><c>x ?? throw new ArgumentNullException(nameof(x))</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class GuardValidation
    {
        public const string ArgumentNotNull = "ArgumentNotNull";
        public const string ArgumentNonNegative = "ArgumentNonNegative";
        public const string ArgumentPositive = "ArgumentPositive";
        public const string ArgumentIsNotNullOrEmpty = "ArgumentIsNotNullOrEmpty";
        public const string ArgumentIsInRange = "ArgumentIsInRange";
        public const string ArgumentMatch = "ArgumentMatch";

        private const string GuardMetadataName = "DevExpress.Utils.Guard";
        private const string GuardHelperMetadataName = "DevExpress.Mvvm.Native.GuardHelper";

        private const string SystemNamespace = "System";
        private const string ArgumentExceptionName = "ArgumentException";
        private const string ArgumentNullExceptionName = "ArgumentNullException";
        private const string ArgumentOutOfRangeExceptionName = "ArgumentOutOfRangeException";

        /// <summary>Preferred name for the <c>Guard.ArgumentMatch</c> predicate parameter.</summary>
        private const string PredicateParameterName = "v";

        /// <summary>
        /// The <c>Guard</c> class of the compilation being analyzed, or <see langword="null"/> when the
        /// code does not have one — the rule stays silent there, because its fix would not compile.
        /// </summary>
        public static INamedTypeSymbol? FindGuardType(Compilation compilation) =>
            compilation.GetTypeByMetadataName(GuardMetadataName)
            ?? compilation.GetTypeByMetadataName(GuardHelperMetadataName);

        /// <summary>
        /// Matches one of the manual validation shapes. Returns <see langword="null"/> when
        /// <paramref name="node"/> is not manual argument validation at all.
        /// </summary>
        public static GuardRewrite? Match(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken) =>
            node switch
            {
                InvocationExpressionSyntax invocation => MatchThrowHelper(invocation, semanticModel, cancellationToken),
                IfStatementSyntax ifStatement => MatchIfThrow(ifStatement, semanticModel, cancellationToken),
                BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression) =>
                    MatchCoalesceThrow(coalesce, semanticModel, cancellationToken),
                _ => null,
            };

        /// <summary>Squiggles the validation itself: the helper name, the <c>if (…)</c> header, or the throw expression.</summary>
        public static Location GetLocation(SyntaxNode node) =>
            node switch
            {
                InvocationExpressionSyntax invocation => (invocation.Expression is MemberAccessExpressionSyntax memberAccess
                    ? memberAccess.Name
                    : invocation.Expression).GetLocation(),
                IfStatementSyntax ifStatement => Location.Create(
                    ifStatement.SyntaxTree,
                    TextSpan.FromBounds(ifStatement.IfKeyword.SpanStart, ifStatement.CloseParenToken.Span.End)),
                BinaryExpressionSyntax coalesce => coalesce.Right.GetLocation(),
                _ => node.GetLocation(),
            };

        // ---------------------------------------------------------------------------------------------
        // Shape 1: BCL throw helpers.
        // ---------------------------------------------------------------------------------------------

        private static GuardRewrite? MatchThrowHelper(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol { IsStatic: true } method)
                return null;

            var containingType = method.ContainingType;
            if (containingType is null || containingType.ContainingNamespace?.ToDisplayString() != SystemNamespace)
                return null;

            if (containingType.Name is not (ArgumentNullExceptionName or ArgumentExceptionName or ArgumentOutOfRangeExceptionName))
                return null;

            if (!method.Name.StartsWith("ThrowIf", StringComparison.Ordinal))
                return null;

            var argumentList = invocation.ArgumentList;

            // The first parameter is 'argument' on the ArgumentException helpers and 'value' on the
            // ArgumentOutOfRangeException ones; 'paramName' is optional ([CallerArgumentExpression]).
            var value = TryGetArgument(argumentList, method, "argument") ?? TryGetArgument(argumentList, method, "value");
            if (value is null)
                return null;

            var explicitName = TryGetArgument(argumentList, method, "paramName");
            var name = BuildName(explicitName, value);
            var valueType = semanticModel.GetTypeInfo(value, cancellationToken).Type;

            switch (containingType.Name, method.Name)
            {
                case (ArgumentNullExceptionName, "ThrowIfNull"):
                    return CanPassAsObject(valueType)
                        ? GuardRewrite.Call(ArgumentNotNull, Strip(value), name)
                        : GuardRewrite.ReportOnly;

                case (ArgumentExceptionName, "ThrowIfNullOrEmpty"):
                    return GuardRewrite.Call(ArgumentIsNotNullOrEmpty, Strip(value), name);

                case (ArgumentExceptionName, "ThrowIfNullOrWhiteSpace"):
                    return BuildNotNullOrWhiteSpaceMatch(value, name, semanticModel);

                case (ArgumentOutOfRangeExceptionName, _):
                    return MatchOutOfRangeHelper(
                        method.Name, value, TryGetArgument(argumentList, method, "other"), valueType, name, semanticModel);

                default:
                    // Another Argument* throw helper: still manual validation, but nothing to map it onto.
                    return GuardRewrite.ReportOnly;
            }
        }

        /// <summary>
        /// Maps the <c>ArgumentOutOfRangeException.ThrowIf*</c> family. The two comparisons <c>Guard</c>
        /// has dedicated members for become <c>ArgumentNonNegative</c>/<c>ArgumentPositive</c>; the rest
        /// become an <c>ArgumentMatch</c> predicate that states the <em>valid</em> condition.
        /// </summary>
        private static GuardRewrite MatchOutOfRangeHelper(
            string methodName,
            ExpressionSyntax value,
            ExpressionSyntax? other,
            ITypeSymbol? valueType,
            ExpressionSyntax name,
            SemanticModel semanticModel)
        {
            switch (methodName)
            {
                case "ThrowIfNegative":
                    return HasNumericGuardOverload(valueType)
                        ? GuardRewrite.Call(ArgumentNonNegative, Strip(value), name)
                        : BuildComparisonMatch(value, name, SyntaxKind.GreaterThanOrEqualExpression, Zero(), valueType, semanticModel);

                case "ThrowIfNegativeOrZero":
                    return HasNumericGuardOverload(valueType)
                        ? GuardRewrite.Call(ArgumentPositive, Strip(value), name)
                        : BuildComparisonMatch(value, name, SyntaxKind.GreaterThanExpression, Zero(), valueType, semanticModel);

                case "ThrowIfZero":
                    return BuildComparisonMatch(value, name, SyntaxKind.NotEqualsExpression, Zero(), valueType, semanticModel);

                case "ThrowIfLessThan" when other is not null:
                    return BuildComparisonMatch(value, name, SyntaxKind.GreaterThanOrEqualExpression, other, valueType, semanticModel);

                case "ThrowIfLessThanOrEqual" when other is not null:
                    return BuildComparisonMatch(value, name, SyntaxKind.GreaterThanExpression, other, valueType, semanticModel);

                case "ThrowIfGreaterThan" when other is not null:
                    return BuildComparisonMatch(value, name, SyntaxKind.LessThanOrEqualExpression, other, valueType, semanticModel);

                case "ThrowIfGreaterThanOrEqual" when other is not null:
                    return BuildComparisonMatch(value, name, SyntaxKind.LessThanExpression, other, valueType, semanticModel);

                case "ThrowIfEqual" when other is not null:
                    return BuildComparisonMatch(value, name, SyntaxKind.NotEqualsExpression, other, valueType, semanticModel);

                case "ThrowIfNotEqual" when other is not null:
                    return BuildComparisonMatch(value, name, SyntaxKind.EqualsExpression, other, valueType, semanticModel);

                default:
                    return GuardRewrite.ReportOnly;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Shape 2: if (condition) throw new Argument*Exception(...);
        // ---------------------------------------------------------------------------------------------

        private static GuardRewrite? MatchIfThrow(
            IfStatementSyntax ifStatement, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (ifStatement.Else is not null)
                return null;

            var creation = TryGetThrownArgumentException(ifStatement.Statement, semanticModel, cancellationToken);
            if (creation is null)
                return null;

            var explicitName = TryGetParamNameArgument(creation, semanticModel, cancellationToken);
            return MatchCondition(ifStatement.Condition, explicitName, semanticModel, cancellationToken) ?? GuardRewrite.ReportOnly;
        }

        /// <summary>
        /// The <c>Argument*Exception</c> created by an <c>if</c> body that consists of a single
        /// <c>throw</c> (with or without braces), or <see langword="null"/> when the body is anything else.
        /// </summary>
        private static BaseObjectCreationExpressionSyntax? TryGetThrownArgumentException(
            StatementSyntax statement, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            var body = statement is BlockSyntax block
                ? (block.Statements.Count == 1 ? block.Statements[0] : null)
                : statement;

            if (body is not ThrowStatementSyntax { Expression: BaseObjectCreationExpressionSyntax creation })
                return null;

            return IsArgumentException(semanticModel.GetTypeInfo(creation, cancellationToken).Type) ? creation : null;
        }

        /// <summary>
        /// Maps a validation condition (true = the argument is invalid) onto a <c>Guard</c> call.
        /// Returns <see langword="null"/> when no rewrite can be produced.
        /// </summary>
        private static GuardRewrite? MatchCondition(
            ExpressionSyntax condition, ExpressionSyntax? explicitName, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            var inner = Unparenthesize(condition);

            if (TryGetNullCheckOperand(inner, semanticModel, cancellationToken) is { } nullOperand
                && CanPassAsObject(semanticModel.GetTypeInfo(nullOperand, cancellationToken).Type))
            {
                return GuardRewrite.Call(ArgumentNotNull, Strip(nullOperand), BuildName(explicitName, nullOperand));
            }

            if (TryGetStringHelperArgument(inner, "IsNullOrEmpty", semanticModel, cancellationToken) is { } nullOrEmpty)
                return GuardRewrite.Call(ArgumentIsNotNullOrEmpty, Strip(nullOrEmpty), BuildName(explicitName, nullOrEmpty));

            if (TryGetStringHelperArgument(inner, "IsNullOrWhiteSpace", semanticModel, cancellationToken) is { } whiteSpace)
                return BuildNotNullOrWhiteSpaceMatch(whiteSpace, BuildName(explicitName, whiteSpace), semanticModel);

            if (inner is BinaryExpressionSyntax or && or.IsKind(SyntaxKind.LogicalOrExpression))
            {
                if (MatchNullOrEmptyPair(or, explicitName, semanticModel, cancellationToken) is { } emptyString)
                    return emptyString;

                if (MatchRangePair(or, explicitName, semanticModel, cancellationToken) is { } range)
                    return range;
            }

            if (inner is BinaryExpressionSyntax comparison
                && MatchSignComparison(comparison, explicitName, semanticModel, cancellationToken) is { } sign)
            {
                return sign;
            }

            return BuildConditionMatch(condition, explicitName, semanticModel, cancellationToken);
        }

        /// <summary>Matches <c>s == null || s.Length == 0</c> and <c>s == null || s == string.Empty</c>.</summary>
        private static GuardRewrite? MatchNullOrEmptyPair(
            BinaryExpressionSyntax or, ExpressionSyntax? explicitName, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (TryGetNullCheckOperand(Unparenthesize(or.Left), semanticModel, cancellationToken) is not { } value)
                return null;

            if (semanticModel.GetTypeInfo(value, cancellationToken).Type?.SpecialType != SpecialType.System_String)
                return null;

            if (Unparenthesize(or.Right) is not BinaryExpressionSyntax equals || !equals.IsKind(SyntaxKind.EqualsExpression))
                return null;

            var isEmptyCheck =
                (equals.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Length" } length
                    && AreEquivalent(length.Expression, value, topLevel: false)
                    && IsNumericConstant(equals.Right, semanticModel, 0))
                || (AreEquivalent(equals.Left, value, topLevel: false)
                    && (semanticModel.GetConstantValue(equals.Right, cancellationToken).Value as string) == string.Empty);

            return isEmptyCheck
                ? GuardRewrite.Call(ArgumentIsNotNullOrEmpty, Strip(value), BuildName(explicitName, value))
                : null;
        }

        /// <summary>
        /// Matches a two-sided range check — <c>i &lt; min || i &gt; max</c> and its <c>&lt;=</c>/<c>&gt;=</c>
        /// variants — and maps it onto <c>Guard.ArgumentIsInRange</c>. <c>i &lt; 0 || i &gt;= list.Count</c>
        /// uses the <c>IList&lt;T&gt;</c> overload; otherwise the bounds are adjusted to the inclusive
        /// form the <c>int</c> overload expects.
        /// </summary>
        private static GuardRewrite? MatchRangePair(
            BinaryExpressionSyntax or, ExpressionSyntax? explicitName, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (Unparenthesize(or.Left) is not BinaryExpressionSyntax left || Unparenthesize(or.Right) is not BinaryExpressionSyntax right)
                return null;

            if (!TryNormalizeComparison(left, semanticModel, out var lowValue, out var lowOperator, out var lowBound)
                || !TryNormalizeComparison(right, semanticModel, out var highValue, out var highOperator, out var highBound))
            {
                return null;
            }

            // Accept the two orders the check can be written in: below-then-above, or above-then-below.
            if (IsAboveCheck(lowOperator) && IsBelowCheck(highOperator))
            {
                (lowValue, lowOperator, lowBound, highValue, highOperator, highBound) =
                    (highValue, highOperator, highBound, lowValue, lowOperator, lowBound);
            }

            if (!IsBelowCheck(lowOperator) || !IsAboveCheck(highOperator))
                return null;

            if (!AreEquivalent(lowValue, highValue, topLevel: false))
                return null;

            var valueType = semanticModel.GetTypeInfo(lowValue, cancellationToken).Type;
            if (!HasIntGuardOverload(valueType))
                return null;

            // 'i < 0 || i >= list.Count' is exactly what the IList<T> overload checks.
            if (lowOperator == SyntaxKind.LessThanExpression
                && IsNumericConstant(lowBound, semanticModel, 0)
                && highOperator == SyntaxKind.GreaterThanOrEqualExpression
                && TryGetListOfCount(highBound, semanticModel, cancellationToken) is { } list)
            {
                return GuardRewrite.Call(ArgumentIsInRange, Strip(list), Strip(lowValue), BuildName(explicitName, lowValue));
            }

            // Guard.ArgumentIsInRange takes inclusive bounds: shift the exclusive forms by one.
            var minimum = lowOperator == SyntaxKind.LessThanExpression ? Strip(lowBound) : Offset(lowBound, 1, semanticModel);
            var maximum = highOperator == SyntaxKind.GreaterThanExpression ? Strip(highBound) : Offset(highBound, -1, semanticModel);
            if (minimum is null || maximum is null)
                return null;

            if (!HasIntGuardOverload(semanticModel.GetTypeInfo(lowBound, cancellationToken).Type)
                || !HasIntGuardOverload(semanticModel.GetTypeInfo(highBound, cancellationToken).Type))
            {
                return null;
            }

            return GuardRewrite.Call(ArgumentIsInRange, minimum, maximum, Strip(lowValue), BuildName(explicitName, lowValue));
        }

        /// <summary>Matches <c>i &lt; 0</c> (non-negative) and <c>i &lt;= 0</c> / <c>i &lt; 1</c> (positive).</summary>
        private static GuardRewrite? MatchSignComparison(
            BinaryExpressionSyntax comparison, ExpressionSyntax? explicitName, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (!TryNormalizeComparison(comparison, semanticModel, out var value, out var comparisonOperator, out var bound))
                return null;

            var valueType = semanticModel.GetTypeInfo(value, cancellationToken).Type;
            var name = BuildName(explicitName, value);

            if (comparisonOperator == SyntaxKind.LessThanExpression && IsNumericConstant(bound, semanticModel, 0))
            {
                return HasNumericGuardOverload(valueType)
                    ? GuardRewrite.Call(ArgumentNonNegative, Strip(value), name)
                    : BuildComparisonMatch(value, name, SyntaxKind.GreaterThanOrEqualExpression, Zero(), valueType, semanticModel);
            }

            var isPositiveCheck =
                (comparisonOperator == SyntaxKind.LessThanOrEqualExpression && IsNumericConstant(bound, semanticModel, 0))
                || (comparisonOperator == SyntaxKind.LessThanExpression
                    && IsIntegral(valueType)
                    && IsNumericConstant(bound, semanticModel, 1));

            if (isPositiveCheck)
            {
                return HasNumericGuardOverload(valueType)
                    ? GuardRewrite.Call(ArgumentPositive, Strip(value), name)
                    : BuildComparisonMatch(value, name, SyntaxKind.GreaterThanExpression, Zero(), valueType, semanticModel);
            }

            return null;
        }

        // ---------------------------------------------------------------------------------------------
        // Shape 3: x ?? throw new ArgumentNullException(nameof(x))
        // ---------------------------------------------------------------------------------------------

        private static GuardRewrite? MatchCoalesceThrow(
            BinaryExpressionSyntax coalesce, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (coalesce.Right is not ThrowExpressionSyntax { Expression: BaseObjectCreationExpressionSyntax creation })
                return null;

            if (!IsArgumentException(semanticModel.GetTypeInfo(creation, cancellationToken).Type))
                return null;

            var value = coalesce.Left;

            // The value stays in place and is also passed to Guard, so it must be safe to evaluate twice,
            // and the Guard call has to have a statement of its own to live in.
            if (!IsSideEffectFree(value)
                || TryGetHoistTarget(coalesce) is null
                || !CanPassAsObject(semanticModel.GetTypeInfo(value, cancellationToken).Type))
            {
                return GuardRewrite.ReportOnly;
            }

            var explicitName = TryGetParamNameArgument(creation, semanticModel, cancellationToken);
            return GuardRewrite.Call(ArgumentNotNull, Strip(value), BuildName(explicitName, value));
        }

        /// <summary>
        /// The statement the hoisted <c>Guard</c> call is inserted before, or <see langword="null"/> when
        /// the coalesce cannot be split — it is not inside a block, it sits in a loop header (where the
        /// check would stop running on every iteration), or it is inside a nested lambda (where the check
        /// would stop running when the lambda does).
        /// </summary>
        public static StatementSyntax? TryGetHoistTarget(BinaryExpressionSyntax coalesce)
        {
            foreach (var ancestor in coalesce.Ancestors())
            {
                switch (ancestor)
                {
                    case AnonymousFunctionExpressionSyntax:
                    case LocalFunctionStatementSyntax:
                        return null;
                    case WhileStatementSyntax:
                    case DoStatementSyntax:
                    case ForStatementSyntax:
                    case CommonForEachStatementSyntax:
                        return null;
                    case StatementSyntax statement:
                        return statement.Parent is BlockSyntax ? statement : null;
                }
            }

            return null;
        }

        // ---------------------------------------------------------------------------------------------
        // Guard.ArgumentMatch predicates.
        // ---------------------------------------------------------------------------------------------

        /// <summary>Builds <c>Guard.ArgumentMatch(s, name, v =&gt; !string.IsNullOrWhiteSpace(v))</c>.</summary>
        private static GuardRewrite BuildNotNullOrWhiteSpaceMatch(ExpressionSyntax value, ExpressionSyntax name, SemanticModel semanticModel)
        {
            var parameter = ResolvePredicateParameterName(semanticModel, value.SpanStart);
            var body = PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                InvocationExpression(
                        MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            PredefinedType(Token(SyntaxKind.StringKeyword)),
                            IdentifierName("IsNullOrWhiteSpace")))
                    .AddArgumentListArguments(Argument(IdentifierName(parameter))));

            return GuardRewrite.Call(ArgumentMatch, Strip(value), name, Lambda(parameter, body));
        }

        /// <summary>
        /// Builds <c>Guard.ArgumentMatch(value, name, v =&gt; v &lt;operator&gt; bound)</c>, the general form
        /// for a comparison <c>Guard</c> has no dedicated member for. The operand types have to support the
        /// operator with plain C# semantics, otherwise no fix is offered.
        /// </summary>
        private static GuardRewrite BuildComparisonMatch(
            ExpressionSyntax value,
            ExpressionSyntax name,
            SyntaxKind comparisonOperator,
            ExpressionSyntax bound,
            ITypeSymbol? valueType,
            SemanticModel semanticModel)
        {
            var isEquality = comparisonOperator is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;
            var isSupported = isEquality ? HasValueEquality(valueType) : IsNumeric(valueType);
            if (!isSupported)
                return GuardRewrite.ReportOnly;

            var parameter = ResolvePredicateParameterName(semanticModel, value.SpanStart);
            var body = BinaryExpression(comparisonOperator, IdentifierName(parameter), Strip(bound));

            return GuardRewrite.Call(ArgumentMatch, Strip(value), name, Lambda(parameter, body));
        }

        /// <summary>
        /// Last resort for an <c>if (condition) throw</c> whose condition has no dedicated <c>Guard</c>
        /// member: <c>Guard.ArgumentMatch(x, name, v =&gt; !condition)</c>, with references to the validated
        /// symbol renamed to the predicate parameter. Offered only when the condition tests exactly one
        /// parameter (or, failing that, exactly one local), so that the predicate has an unambiguous subject.
        /// A condition that calls something is fine here: <c>Guard.ArgumentMatch</c> invokes the predicate
        /// exactly once, in the place the <c>if</c> used to test it.
        /// </summary>
        private static GuardRewrite? BuildConditionMatch(
            ExpressionSyntax condition, ExpressionSyntax? explicitName, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            var parameters = new List<IdentifierNameSyntax>();
            var locals = new List<IdentifierNameSyntax>();
            ISymbol? parameterSymbol = null;
            ISymbol? localSymbol = null;

            foreach (var identifier in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                switch (semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol)
                {
                    case IParameterSymbol parameter:
                        if (parameterSymbol is not null && !SymbolEqualityComparer.Default.Equals(parameterSymbol, parameter))
                            return null; // More than one parameter tested; the subject is ambiguous.
                        parameterSymbol = parameter;
                        parameters.Add(identifier);
                        break;

                    case ILocalSymbol local:
                        if (localSymbol is not null && !SymbolEqualityComparer.Default.Equals(localSymbol, local))
                            return null;
                        localSymbol = local;
                        locals.Add(identifier);
                        break;
                }
            }

            var (symbol, references) = parameterSymbol is not null
                ? (parameterSymbol, parameters)
                : (localSymbol, locals);

            if (symbol is null || references.Count == 0)
                return null;

            var symbolType = symbol is IParameterSymbol p ? p.Type : ((ILocalSymbol)symbol).Type;
            if (symbolType is null || symbolType.TypeKind is TypeKind.Error or TypeKind.Pointer || symbolType.IsRefLikeType || symbolType.IsAnonymousType)
                return null;

            var parameterName = ResolvePredicateParameterName(semanticModel, condition.SpanStart);
            var renamed = condition.ReplaceNodes(references, (original, _) => IdentifierName(parameterName).WithTriviaFrom(original));
            var value = IdentifierName(symbol.Name);

            return GuardRewrite.Call(ArgumentMatch, value, BuildName(explicitName, value), Lambda(parameterName, Negate(renamed)));
        }

        private static SimpleLambdaExpressionSyntax Lambda(string parameterName, ExpressionSyntax body) =>
            SimpleLambdaExpression(Parameter(Identifier(parameterName))).WithExpressionBody(body.WithoutTrivia());

        /// <summary>A predicate parameter name that shadows nothing at <paramref name="position"/>.</summary>
        private static string ResolvePredicateParameterName(SemanticModel semanticModel, int position)
        {
            var taken = semanticModel.LookupSymbols(position).Select(s => s.Name).ToImmutableHashSet();
            if (!taken.Contains(PredicateParameterName))
                return PredicateParameterName;

            for (var i = 1; ; i++)
            {
                var candidate = PredicateParameterName + i;
                if (!taken.Contains(candidate))
                    return candidate;
            }
        }

        /// <summary>
        /// Logical negation that keeps the meaning of the expression exactly: only inversions that hold for
        /// every operand (including NaN) are applied, anything else is wrapped in <c>!(…)</c>.
        /// </summary>
        private static ExpressionSyntax Negate(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    return Negate(parenthesized.Expression);

                case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.LogicalNotExpression):
                    return Strip(unary.Operand);

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression):
                    return Strip(binary.WithOperatorToken(Retrivia(binary.OperatorToken, SyntaxKind.ExclamationEqualsToken)));

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.NotEqualsExpression):
                    return Strip(binary.WithOperatorToken(Retrivia(binary.OperatorToken, SyntaxKind.EqualsEqualsToken)));

                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression):
                    return LiteralExpression(SyntaxKind.FalseLiteralExpression);

                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.FalseLiteralExpression):
                    return LiteralExpression(SyntaxKind.TrueLiteralExpression);

                default:
                    return PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, Parenthesize(expression));
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Condition building blocks.
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// The operand of a null test — <c>x == null</c>, <c>null == x</c>, <c>x is null</c>,
        /// <c>x == default</c> or <c>ReferenceEquals(x, null)</c> — or <see langword="null"/>.
        /// </summary>
        private static ExpressionSyntax? TryGetNullCheckOperand(
            ExpressionSyntax condition, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            switch (condition)
            {
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression):
                    if (IsNullLike(binary.Right))
                        return Unparenthesize(binary.Left);
                    if (IsNullLike(binary.Left))
                        return Unparenthesize(binary.Right);
                    return null;

                case IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax constant } isPattern when IsNullLike(constant.Expression):
                    return Unparenthesize(isPattern.Expression);

                case InvocationExpressionSyntax invocation:
                    if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol
                        {
                            Name: "ReferenceEquals",
                            IsStatic: true,
                            ContainingType.SpecialType: SpecialType.System_Object,
                        })
                    {
                        return null;
                    }

                    var arguments = invocation.ArgumentList.Arguments;
                    if (arguments.Count != 2)
                        return null;
                    if (IsNullLike(arguments[1].Expression))
                        return Unparenthesize(arguments[0].Expression);
                    if (IsNullLike(arguments[0].Expression))
                        return Unparenthesize(arguments[1].Expression);
                    return null;

                default:
                    return null;
            }
        }

        /// <summary>The single argument of <c>string.IsNullOrEmpty(s)</c> / <c>string.IsNullOrWhiteSpace(s)</c>.</summary>
        private static ExpressionSyntax? TryGetStringHelperArgument(
            ExpressionSyntax condition, string methodName, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (condition is not InvocationExpressionSyntax invocation)
                return null;

            if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method
                || method.Name != methodName
                || method.ContainingType?.SpecialType != SpecialType.System_String)
            {
                return null;
            }

            var arguments = invocation.ArgumentList.Arguments;
            return arguments.Count == 1 ? Unparenthesize(arguments[0].Expression) : null;
        }

        /// <summary>The receiver of an <c>xs.Count</c> access when <c>xs</c> is an <c>IList&lt;T&gt;</c>.</summary>
        private static ExpressionSyntax? TryGetListOfCount(
            ExpressionSyntax bound, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (bound is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Count" } memberAccess)
                return null;

            var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type;
            if (receiverType is null)
                return null;

            var isList = receiverType.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IList_T
                || receiverType.AllInterfaces.Any(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IList_T);

            return isList ? Unparenthesize(memberAccess.Expression) : null;
        }

        /// <summary>
        /// Rewrites a comparison so that the validated value is on the left, flipping the operator when the
        /// source has it the other way round (<c>0 &gt; i</c> becomes <c>i &lt; 0</c>). The value is the side
        /// that is not a constant; a comparison of two constants is not a match.
        /// </summary>
        private static bool TryNormalizeComparison(
            BinaryExpressionSyntax comparison,
            SemanticModel semanticModel,
            out ExpressionSyntax value,
            out SyntaxKind comparisonOperator,
            out ExpressionSyntax bound)
        {
            value = comparison.Left;
            bound = comparison.Right;
            comparisonOperator = comparison.Kind();

            if (comparisonOperator is not (SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression))
            {
                return false;
            }

            // 'min < i' reads as 'i > min': keep the validated value on the left.
            if (semanticModel.GetConstantValue(comparison.Left).HasValue && !semanticModel.GetConstantValue(comparison.Right).HasValue)
            {
                value = comparison.Right;
                bound = comparison.Left;
                comparisonOperator = Flip(comparisonOperator);
            }

            value = Unparenthesize(value);
            bound = Unparenthesize(bound);
            return true;
        }

        private static SyntaxKind Flip(SyntaxKind comparisonOperator) =>
            comparisonOperator switch
            {
                SyntaxKind.LessThanExpression => SyntaxKind.GreaterThanExpression,
                SyntaxKind.LessThanOrEqualExpression => SyntaxKind.GreaterThanOrEqualExpression,
                SyntaxKind.GreaterThanExpression => SyntaxKind.LessThanExpression,
                SyntaxKind.GreaterThanOrEqualExpression => SyntaxKind.LessThanOrEqualExpression,
                _ => comparisonOperator,
            };

        private static bool IsBelowCheck(SyntaxKind comparisonOperator) =>
            comparisonOperator is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression;

        private static bool IsAboveCheck(SyntaxKind comparisonOperator) =>
            comparisonOperator is SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression;

        /// <summary>Shifts a bound by one, folding the arithmetic when the bound is a constant.</summary>
        private static ExpressionSyntax? Offset(ExpressionSyntax bound, int delta, SemanticModel semanticModel)
        {
            if (semanticModel.GetConstantValue(bound).Value is int constant)
                return LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(constant + delta));

            if (!IsSideEffectFree(bound))
                return null;

            return BinaryExpression(
                delta > 0 ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
                Parenthesize(bound),
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(Math.Abs(delta))));
        }

        // ---------------------------------------------------------------------------------------------
        // Argument names.
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// The name <c>Guard</c> is given for the argument: the <c>paramName</c> the original validation
        /// passed if there is one, otherwise <c>nameof(value)</c>, otherwise the value's source text.
        /// </summary>
        private static ExpressionSyntax BuildName(ExpressionSyntax? explicitName, ExpressionSyntax value)
        {
            if (explicitName is not null)
                return Strip(explicitName);

            var target = Strip(Unwrap(value));
            if (IsNameOfFriendly(target))
                return InvocationExpression(NameOfIdentifier()).AddArgumentListArguments(Argument(target));

            return LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(target.ToString()));
        }

        /// <summary>
        /// <c>nameof</c> is a contextual keyword: an identifier built the ordinary way parses as a call to
        /// an undefined method, so the token has to carry <see cref="SyntaxKind.NameOfKeyword"/> explicitly.
        /// </summary>
        private static IdentifierNameSyntax NameOfIdentifier() =>
            IdentifierName(Identifier(TriviaList(), SyntaxKind.NameOfKeyword, "nameof", "nameof", TriviaList()));

        private static bool IsNameOfFriendly(ExpressionSyntax expression) =>
            expression switch
            {
                IdentifierNameSyntax => true,
                MemberAccessExpressionSyntax memberAccess when memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression) =>
                    memberAccess.Name is IdentifierNameSyntax && IsNameOfQualifier(memberAccess.Expression),
                _ => false,
            };

        private static bool IsNameOfQualifier(ExpressionSyntax expression) =>
            expression is ThisExpressionSyntax || IsNameOfFriendly(expression);

        /// <summary>The <c>paramName</c> argument of an <c>Argument*Exception</c> constructor, if it has one.</summary>
        private static ExpressionSyntax? TryGetParamNameArgument(
            BaseObjectCreationExpressionSyntax creation, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (semanticModel.GetSymbolInfo(creation, cancellationToken).Symbol is not IMethodSymbol constructor)
                return null;

            return TryGetArgument(creation.ArgumentList, constructor, "paramName");
        }

        /// <summary>The argument bound to <paramref name="parameterName"/>, positionally or by name.</summary>
        private static ExpressionSyntax? TryGetArgument(ArgumentListSyntax? argumentList, IMethodSymbol method, string parameterName)
        {
            if (argumentList is null)
                return null;

            var arguments = argumentList.Arguments;
            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                if (argument.NameColon is { } nameColon)
                {
                    if (nameColon.Name.Identifier.ValueText == parameterName)
                        return argument.Expression;
                    continue;
                }

                if (i < method.Parameters.Length && method.Parameters[i].Name == parameterName)
                    return argument.Expression;
            }

            return null;
        }

        // ---------------------------------------------------------------------------------------------
        // Types and expressions.
        // ---------------------------------------------------------------------------------------------

        private static bool IsArgumentException(ITypeSymbol? type)
        {
            for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                if (current.Name == ArgumentExceptionName && current.ContainingNamespace?.ToDisplayString() == SystemNamespace)
                    return true;
            }

            return false;
        }

        /// <summary>Whether the value can be passed to <c>Guard.ArgumentNotNull(object, string)</c>.</summary>
        private static bool CanPassAsObject(ITypeSymbol? type) =>
            type is not null && type.TypeKind is not (TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer) && !type.IsRefLikeType;

        /// <summary>
        /// Whether <c>Guard.ArgumentNonNegative</c>/<c>ArgumentPositive</c> can take the value directly.
        /// The overloads are <c>int</c>/<c>float</c>/<c>double</c>; wider integral types either do not
        /// convert (<c>decimal</c>) or convert ambiguously (<c>long</c>, <c>uint</c>).
        /// </summary>
        private static bool HasNumericGuardOverload(ITypeSymbol? type) =>
            HasIntGuardOverload(type) || type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double;

        /// <summary>Whether the value converts to the <c>int</c> parameters of <c>Guard.ArgumentIsInRange</c>.</summary>
        private static bool HasIntGuardOverload(ITypeSymbol? type) =>
            type?.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16
                or SpecialType.System_UInt16 or SpecialType.System_Int32;

        private static bool IsIntegral(ITypeSymbol? type) =>
            type?.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16
                or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64;

        private static bool IsNumeric(ITypeSymbol? type) =>
            IsIntegral(type) || type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;

        /// <summary>Types whose <c>==</c> compares values rather than references, so the predicate keeps the original meaning.</summary>
        private static bool HasValueEquality(ITypeSymbol? type) =>
            IsNumeric(type)
            || type?.TypeKind == TypeKind.Enum
            || type?.SpecialType is SpecialType.System_String or SpecialType.System_Char or SpecialType.System_Boolean;

        private static bool IsNumericConstant(ExpressionSyntax expression, SemanticModel semanticModel, int expected)
        {
            var constant = semanticModel.GetConstantValue(expression);
            return constant.Value switch
            {
                sbyte value => value == expected,
                byte value => value == expected,
                short value => value == expected,
                ushort value => value == expected,
                int value => value == expected,
                uint value => value == expected,
                long value => value == expected,
                ulong value => value == (ulong)expected,
                float value => value == expected,
                double value => value == expected,
                decimal value => value == expected,
                _ => false,
            };
        }

        /// <summary>
        /// Whether the expression can be moved or duplicated without changing what the code does. Anything
        /// that can run user code or mutate state disqualifies it.
        /// </summary>
        private static bool IsSideEffectFree(ExpressionSyntax expression) =>
            !expression.DescendantNodesAndSelf().Any(node =>
                node is InvocationExpressionSyntax
                    or ObjectCreationExpressionSyntax
                    or AwaitExpressionSyntax
                    or AssignmentExpressionSyntax
                    or PostfixUnaryExpressionSyntax
                    or AnonymousFunctionExpressionSyntax
                || node.IsKind(SyntaxKind.PreIncrementExpression)
                || node.IsKind(SyntaxKind.PreDecrementExpression));

        private static bool IsNullLike(ExpressionSyntax expression) =>
            Unparenthesize(expression) is var inner
            && (inner.IsKind(SyntaxKind.NullLiteralExpression)
                || inner.IsKind(SyntaxKind.DefaultLiteralExpression)
                || inner is DefaultExpressionSyntax);

        private static ExpressionSyntax Unparenthesize(ExpressionSyntax expression) =>
            expression is ParenthesizedExpressionSyntax parenthesized ? Unparenthesize(parenthesized.Expression) : expression;

        /// <summary>Drops a trailing <c>!</c> so that <c>x!</c> still yields <c>nameof(x)</c>.</summary>
        private static ExpressionSyntax Unwrap(ExpressionSyntax expression) =>
            expression is PostfixUnaryExpressionSyntax postfix && postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression)
                ? Unwrap(postfix.Operand)
                : expression;

        private static ExpressionSyntax Parenthesize(ExpressionSyntax expression) =>
            expression is ParenthesizedExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax
                or InvocationExpressionSyntax or ElementAccessExpressionSyntax or LiteralExpressionSyntax
                ? expression.WithoutTrivia()
                : ParenthesizedExpression(expression.WithoutTrivia());

        /// <summary>The same operator token with a different kind, keeping the spacing around it.</summary>
        private static SyntaxToken Retrivia(SyntaxToken operatorToken, SyntaxKind kind) =>
            Token(operatorToken.LeadingTrivia, kind, operatorToken.TrailingTrivia);

        private static ExpressionSyntax Zero() => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0));

        private static T Strip<T>(T node) where T : SyntaxNode => (T)node.WithoutTrivia();
    }
}
