using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Shitalyzer.Tests
{
    public class ManualArgumentValidationTests
    {
        private const string GuardFix = "SHIT0007:Guard";
        private const string ValueCheckerFix = "SHIT0007:ValueChecker";

        // The shapes of DevExpress.Utils.Guard and DevExpress.Office.Utils.ValueChecker the fixes rely on.
        private const string GuardStub = @"
namespace DevExpress.Utils
{
    public static class Guard
    {
        public static void ArgumentNotNull(object value, string name) { }
        public static void ArgumentNonNegative(int value, string name) { }
        public static void ArgumentPositive(int value, string name) { }
        public static void ArgumentNonNegative(double value, string name) { }
        public static void ArgumentPositive(double value, string name) { }
        public static void ArgumentIsNotNullOrEmpty(string value, string name) { }
        public static void ArgumentIsInRange<T>(System.Collections.Generic.IList<T> list, int index, string name) { }
        public static void ArgumentIsInRange(int minValue, int maxValue, int value, string name) { }
        public static void ArgumentMatch<TValue>(TValue value, string name, System.Func<TValue, bool> predicate) { }
    }
}";

        private const string ValueCheckerStub = @"
namespace DevExpress.Office.Utils
{
    public static class ValueChecker
    {
        public static void CheckValue(int value, int minValue, int maxValue) { }
        public static void CheckValue(int value, int minValue, int maxValue, string name) { }
        public static void CheckValue(long value, long minValue, long maxValue, string name) { }
        public static void CheckValue(double value, double minValue, double maxValue, string name) { }
        public static void CheckLength(string value, int maxLength, string name) { }
        public static void CheckMinValue(long? value, long min, string propertyName) { }
        public static void CheckRange(long? value, long min, long max, string propertyName) { }
    }
}";

        // ---------------------------------------------------------------------------------------------
        // Severity.
        // ---------------------------------------------------------------------------------------------

        [Test]
        public Task MatchingExceptionTypeIsAWarning() => VerifyDiagnostics(
            withValueChecker: false,
            @"if (x == null) throw new ArgumentNullException(nameof(x));
if (s == null || s.Length == 0) throw new ArgumentException(null, nameof(s));",
            new Expected(0, DiagnosticSeverity.Warning, "Guard.ArgumentNotNull"),
            new Expected(1, DiagnosticSeverity.Warning, "Guard.ArgumentIsNotNullOrEmpty"));

        [Test]
        public Task DifferentExceptionTypeIsHidden() => VerifyDiagnostics(
            withValueChecker: false,
            @"if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));
if (x == null) throw new ArgumentException(null, nameof(x));
if (s == null || s.Length == 0) throw new ArgumentNullException(nameof(s));",
            new Expected(0, DiagnosticSeverity.Hidden, "Guard.ArgumentNonNegative"),
            new Expected(1, DiagnosticSeverity.Hidden, "Guard.ArgumentNotNull"),
            new Expected(2, DiagnosticSeverity.Hidden, "Guard.ArgumentIsNotNullOrEmpty"));

        [Test]
        public Task CustomMessageIsHidden() => VerifyDiagnostics(
            withValueChecker: true,
            @"if (x == null) throw new ArgumentNullException(nameof(x), ""Pass a value."");
if (i < 0) throw new ArgumentOutOfRangeException(nameof(i), ""Must not be negative."");",
            new Expected(0, DiagnosticSeverity.Hidden, "Guard.ArgumentNotNull"),
            new Expected(1, DiagnosticSeverity.Hidden, "ValueChecker.CheckMinValue"));

        [Test]
        public Task ValueCheckerKeepsArgumentOutOfRangeAWarning() => VerifyDiagnostics(
            withValueChecker: true,
            @"if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));
if (i < 0 || i > 100) throw new ArgumentOutOfRangeException(nameof(i));
if (d < 0.0 || d > 1.0) throw new ArgumentOutOfRangeException(nameof(d));
if (n < 1) throw new ArgumentOutOfRangeException(nameof(n));",
            new Expected(0, DiagnosticSeverity.Warning, "ValueChecker.CheckMinValue"),
            new Expected(1, DiagnosticSeverity.Warning, "ValueChecker.CheckValue"),
            new Expected(2, DiagnosticSeverity.Warning, "ValueChecker.CheckValue"),
            new Expected(3, DiagnosticSeverity.Warning, "ValueChecker.CheckMinValue"));

        [Test]
        public Task ArgumentExceptionStillPrefersGuard() => VerifyDiagnostics(
            withValueChecker: true,
            @"if (i < 0) throw new ArgumentException(null, nameof(i));",
            new Expected(0, DiagnosticSeverity.Warning, "Guard.ArgumentNonNegative"));

        [Test]
        public Task ThrowHelpersStayWarnings() => VerifyDiagnostics(
            withValueChecker: false,
            @"ArgumentOutOfRangeException.ThrowIfNegative(i);
ArgumentNullException.ThrowIfNull(x);",
            new Expected(0, DiagnosticSeverity.Warning, "Guard.ArgumentNonNegative", Span: "ThrowIfNegative"),
            new Expected(1, DiagnosticSeverity.Warning, "Guard.ArgumentNotNull", Span: "ThrowIfNull"));

        [Test]
        public Task ThrowHelperPrefersValueCheckerWhenItKeepsTheType() => VerifyDiagnostics(
            withValueChecker: true,
            @"ArgumentOutOfRangeException.ThrowIfNegative(i);",
            new Expected(0, DiagnosticSeverity.Warning, "ValueChecker.CheckMinValue", Span: "ThrowIfNegative"));

        [Test]
        public Task ComparisonOfTwoParametersIsNotReported() => VerifyDiagnostics(
            withValueChecker: true,
            @"if (i < n) throw new ArgumentOutOfRangeException(nameof(i));");

        // ---------------------------------------------------------------------------------------------
        // ValueChecker fixes.
        // ---------------------------------------------------------------------------------------------

        [Test]
        public Task CheckMinValueFix() => VerifyFix(
            ValueCheckerFix,
            @"if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));
if (i <= 0) throw new ArgumentOutOfRangeException(nameof(i));
if (l < 5) throw new ArgumentOutOfRangeException(nameof(l));
if (nullable < 0) throw new ArgumentOutOfRangeException(nameof(nullable));",
            @"DevExpress.Office.Utils.ValueChecker.CheckMinValue(i, 0, nameof(i));
DevExpress.Office.Utils.ValueChecker.CheckMinValue(i, 1, nameof(i));
DevExpress.Office.Utils.ValueChecker.CheckMinValue(l, 5, nameof(l));
DevExpress.Office.Utils.ValueChecker.CheckMinValue(nullable, 0, nameof(nullable));");

        [Test]
        public Task CheckValueFix() => VerifyFix(
            ValueCheckerFix,
            @"if (i < 0 || i > 100) throw new ArgumentOutOfRangeException(nameof(i));
if (i < 0 || i >= list.Count) throw new ArgumentOutOfRangeException(nameof(i));
if (i > 100 || 0 > i) throw new ArgumentOutOfRangeException(""index"");
if (l < 0 || l > 10L) throw new ArgumentOutOfRangeException(nameof(l));
if (d < 0.0 || d > 1.0) throw new ArgumentOutOfRangeException(nameof(d));",
            @"DevExpress.Office.Utils.ValueChecker.CheckValue(i, 0, 100, nameof(i));
DevExpress.Office.Utils.ValueChecker.CheckValue(i, 0, list.Count - 1, nameof(i));
DevExpress.Office.Utils.ValueChecker.CheckValue(i, 0, 100, ""index"");
DevExpress.Office.Utils.ValueChecker.CheckValue(l, 0, 10L, nameof(l));
DevExpress.Office.Utils.ValueChecker.CheckValue(d, 0.0, 1.0, nameof(d));");

        [Test]
        public Task CheckLengthFix() => VerifyFix(
            ValueCheckerFix,
            @"if (!string.IsNullOrEmpty(s) && s.Length > 10) throw new ArgumentException(null, nameof(s));
if (s != null && s.Length >= 10) throw new ArgumentException(null, nameof(s));",
            @"DevExpress.Office.Utils.ValueChecker.CheckLength(s, 10, nameof(s));
DevExpress.Office.Utils.ValueChecker.CheckLength(s, 9, nameof(s));");

        [Test]
        public Task ThrowHelperValueCheckerFix() => VerifyFix(
            ValueCheckerFix,
            @"ArgumentOutOfRangeException.ThrowIfNegative(i);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(l);
ArgumentOutOfRangeException.ThrowIfLessThan(i, 3);",
            @"DevExpress.Office.Utils.ValueChecker.CheckMinValue(i, 0, nameof(i));
DevExpress.Office.Utils.ValueChecker.CheckMinValue(l, 1, nameof(l));
DevExpress.Office.Utils.ValueChecker.CheckMinValue(i, 3, nameof(i));");

        [Test]
        public Task GuardFixIsStillOfferedNextToValueChecker() => VerifyFix(
            GuardFix,
            @"if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));",
            @"DevExpress.Utils.Guard.ArgumentNonNegative(i, nameof(i));");

        [Test]
        public Task ValueCheckerAloneIsEnough()
        {
            var test = new CSharpCodeFixTest<ManualArgumentValidationAnalyzer, ManualArgumentValidationCodeFixProvider, DefaultVerifier>
            {
                TestState = { Sources = { Wrap("if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));"), ValueCheckerStub } },
                FixedState = { Sources = { Wrap("DevExpress.Office.Utils.ValueChecker.CheckMinValue(i, 0, nameof(i));"), ValueCheckerStub } },
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                CodeActionEquivalenceKey = ValueCheckerFix,
            };
            test.TestState.ExpectedDiagnostics.Add(Resolve(
                "if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));",
                new Expected(0, DiagnosticSeverity.Warning, "ValueChecker.CheckMinValue")));
            return test.RunAsync();
        }

        [Test]
        public Task ShapesValueCheckerCannotTakeOfferNoValueCheckerFix()
        {
            var body = @"if (d <= 0.0 || d >= 1.0) throw new ArgumentOutOfRangeException(nameof(d));
if (s != null && s.Length >= 0) throw new ArgumentException(null, nameof(s));";
            var test = CreateTest(body, body, withValueChecker: true, ValueCheckerFix);
            test.TestState.ExpectedDiagnostics.Add(Resolve(body, new Expected(0, DiagnosticSeverity.Hidden, "Guard.ArgumentMatch")));
            test.TestState.ExpectedDiagnostics.Add(Resolve(body, new Expected(1, DiagnosticSeverity.Warning, "Guard.ArgumentMatch")));
            test.NumberOfIncrementalIterations = 0;
            test.NumberOfFixAllIterations = 0;
            test.NumberOfFixAllInDocumentIterations = 0;
            test.NumberOfFixAllInProjectIterations = 0;
            return test.RunAsync();
        }

        // ---------------------------------------------------------------------------------------------
        // Infrastructure.
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// An expected diagnostic on body line <see cref="Line"/>: on the <c>if (…)</c> header, or on
        /// <see cref="Span"/> when given. A <see langword="null"/> <see cref="HelperMember"/> skips the message check.
        /// </summary>
        private sealed record Expected(int Line, DiagnosticSeverity Severity, string? HelperMember, string? Span = null);

        private static DiagnosticResult Resolve(string body, Expected expected)
        {
            var line = Lines(body)[expected.Line];
            int start, end;
            if (expected.Span is not null)
            {
                start = line.IndexOf(expected.Span, System.StringComparison.Ordinal);
                end = start + expected.Span.Length;
            }
            else
            {
                // 'if (' through the parenthesis that closes the condition.
                start = line.IndexOf("if (", System.StringComparison.Ordinal);
                var depth = 0;
                end = start + 3;
                do
                {
                    depth += line[end] == '(' ? 1 : line[end] == ')' ? -1 : 0;
                    end++;
                }
                while (depth > 0);
            }

            var result = new DiagnosticResult("SHIT0007", expected.Severity)
                .WithSpan(BodyFirstLine + expected.Line, Indentation.Length + start + 1, BodyFirstLine + expected.Line, Indentation.Length + end + 1);
            return expected.HelperMember is null
                ? result
                : result.WithMessage($"Use '{expected.HelperMember}' instead of validating the argument by hand");
        }

        private static Task VerifyDiagnostics(bool withValueChecker, string body, params Expected[] expected)
        {
            // No FixedState: only the diagnostics are verified.
            var test = new CSharpCodeFixTest<ManualArgumentValidationAnalyzer, ManualArgumentValidationCodeFixProvider, DefaultVerifier>
            {
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            };
            test.TestState.Sources.Add(Wrap(body));
            test.TestState.Sources.Add(GuardStub);
            if (withValueChecker)
                test.TestState.Sources.Add(ValueCheckerStub);
            test.TestState.ExpectedDiagnostics.AddRange(expected.Select(e => Resolve(body, e)));
            return test.RunAsync();
        }

        /// <summary>Applies the fix to a body whose every line is a reported warning; the severities are covered above.</summary>
        private static Task VerifyFix(string equivalenceKey, string body, string fixedBody)
        {
            var test = CreateTest(body, fixedBody, withValueChecker: true, equivalenceKey);
            var lines = Lines(body);
            test.TestState.ExpectedDiagnostics.AddRange(Enumerable.Range(0, lines.Length).Select(line => Resolve(
                body,
                new Expected(line, DiagnosticSeverity.Warning, HelperMember: null, Span: lines[line].StartsWith("if", System.StringComparison.Ordinal)
                    ? null
                    : lines[line].Split('.', '(')[1]))));
            return test.RunAsync();
        }

        private static CSharpCodeFixTest<ManualArgumentValidationAnalyzer, ManualArgumentValidationCodeFixProvider, DefaultVerifier> CreateTest(
            string body, string fixedBody, bool withValueChecker, string? equivalenceKey)
        {
            var test = new CSharpCodeFixTest<ManualArgumentValidationAnalyzer, ManualArgumentValidationCodeFixProvider, DefaultVerifier>
            {
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                CodeActionEquivalenceKey = equivalenceKey,
            };
            test.TestState.Sources.Add(Wrap(body));
            test.TestState.Sources.Add(GuardStub);
            test.FixedState.Sources.Add(Wrap(fixedBody));
            test.FixedState.Sources.Add(GuardStub);
            if (withValueChecker)
            {
                test.TestState.Sources.Add(ValueCheckerStub);
                test.FixedState.Sources.Add(ValueCheckerStub);
            }

            return test;
        }

        /// <summary>1-based line of the first body line inside <see cref="Wrap"/>.</summary>
        private const int BodyFirstLine = 8;

        private static string Wrap(string body) => @"using System;
using System.Collections.Generic;

class T
{
    void M(object x, string s, int i, int n, long l, double d, int? nullable, IList<int> list, int max)
    {
" + Indent(body) + @"
    }
}";

        private static string[] Lines(string body) => body.Trim('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        private const string Indentation = "        ";

        private static string Indent(string body) => string.Join("\n", Lines(body).Select(line => Indentation + line));
    }
}
