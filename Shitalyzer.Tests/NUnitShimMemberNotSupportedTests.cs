using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Shitalyzer.Tests
{
    public class NUnitShimMemberNotSupportedTests
    {
        private static readonly ReferenceAssemblies WithNUnit = ReferenceAssemblies.Net.Net80.AddPackages(
            ImmutableArray.Create(new PackageIdentity("NUnit", "3.14.0")));

        [Test]
        public Task ChainsTheShimHasAreNotReported() => VerifyNoFix(@"
Assert.That(x, Is.EqualTo(1).Within(0.1).Percent);
Assert.That(x, Is.Not.Null.Or.EqualTo(2).Within(1));
Assert.That(x, Is.GreaterThan(0).And.LessThan(10));
Assert.That(x, Is.Not.GreaterThan(0));
Assert.That(x, Is.TypeOf<int>());
Assert.That(x, Is.InstanceOf(typeof(int)));
Assert.That(x, NUnit.Framework.Is.AtLeast(0));
Assert.That(list, Is.Not.Null.And.Not.Empty);
Assert.That(list, Is.All.Not.Null);
Assert.That(list, Is.Unique);
Assert.That(list, Has.Count.EqualTo(1));
Assert.That(list, Has.Count.Not.EqualTo(1));
Assert.That(list, Has.Count.GreaterThanOrEqualTo(1));
Assert.That(list, Has.Exactly(1).Items);
Assert.That(list, Has.No.Member(1));
Assert.That(list, Has.Member(1));
Assert.That(s, Has.Length.AtLeast(1));
Assert.That(s, Is.EqualTo(""a"").IgnoreCase);
Assert.That(s, Does.Contain(""a""));
Assert.That(s, Does.Not.StartWith(""a""));
Assert.That(a, Throws.TypeOf<ArgumentException>().With.Message.Contains(""x""));
Assert.That(a, Throws.Exception.With.Property(""ParamName"").EqualTo(""x""));
Assert.That(a, Throws.ArgumentNullException);
Assert.That(a, Throws.Nothing);
");

        [Test]
        public Task MembersTheShimLacksAreReported() => VerifyNoFix(@"
Assert.That(d, Is.{|SHIT0011:NaN|});
Assert.That(list, Is.{|SHIT0011:Ordered|});
Assert.That(x, Is.{|SHIT0011:InRange|}(0, 10));
Assert.That(list, Has.{|SHIT0011:Some|}.EqualTo(1));
Assert.That(list, Has.All.{|SHIT0011:GreaterThan|}(0));
Assert.That(x, Is.GreaterThan(0).{|SHIT0011:Within|}(1));
Assert.That(x, Is.EqualTo(1).{|SHIT0011:Ulps|});
Assert.That(s, Does.Contain(""a"").{|SHIT0011:IgnoreCase|});
Assert.That(s, Does.{|SHIT0011:Match|}(""a.*""));
Assert.That(a, Throws.Exception.With.Message.{|SHIT0011:StartsWith|}(""x""));
Assert.That(a, Throws.{|SHIT0011:InnerException|}.TypeOf<ArgumentException>());
");

        [Test]
        public Task OnlyTheFirstMissingMemberOfAChainIsReported() => VerifyNoFix(@"
Assert.That(list, Has.{|SHIT0011:Some|}.Not.Null.Or.Empty);
");

        [Test]
        public Task ClassesWithoutAShimAreReported() => VerifyNoFix(@"
Assume.{|SHIT0011:That|}(x, Is.EqualTo(1));
Assert.That(x, Iz.{|SHIT0011:EqualTo|}(1));
");

        [Test]
        public Task MessageNamesTheChainAndWhatTheShimHas()
        {
            var test = CreateTest("Assert.That(a, Throws.Exception.With.Message.StartsWith(\"x\"));", fixedBody: null);
            test.ExpectedDiagnostics.Add(new DiagnosticResult("SHIT0011", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
                .WithSpan(6, 46, 6, 56)
                .WithArguments("Throws.Exception.With.Message.StartsWith", "use a constraint the shim has: Contains, EqualTo"));
            return test.RunAsync();
        }

        [Test]
        public Task PositiveAndNegativeBecomeComparisonsWithZero() => VerifyFix(@"
Assert.That(x, Is.{|SHIT0011:Positive|});
Assert.That(d, Is.{|SHIT0011:Negative|}, ""message"");
Assert.That(x, Is.Not.{|SHIT0011:Positive|});
Assert.That(x, Is.Not.{|SHIT0011:Negative|});
Assert.That(x, Is.{|SHIT0011:Positive|}.Or.EqualTo(0));
", @"
Assert.That(x, Is.GreaterThan(0));
Assert.That(d, Is.LessThan(0), ""message"");
Assert.That(x, Is.Not.GreaterThan(0));
Assert.That(x, Is.Not.LessThan(0));
Assert.That(x, Is.GreaterThan(0).Or.EqualTo(0));
");

        [Test]
        public Task CountAtLeastAndAtMostBecomeComparisons() => VerifyFix(@"
Assert.That(list, Has.Count.{|SHIT0011:AtLeast|}(2));
Assert.That(list, Has.Count.{|SHIT0011:AtMost|}(2).Or.EqualTo(5));
", @"
Assert.That(list, Has.Count.GreaterThanOrEqualTo(2));
Assert.That(list, Has.Count.LessThanOrEqualTo(2).Or.EqualTo(5));
");

        [Test]
        public Task PositiveWithoutAShimComparisonIsNotFixed() => VerifyNoFix(@"
Assert.That(list, Has.All.{|SHIT0011:Positive|});
");

        [Test]
        public Task NonNUnitClassesNamedLikeNUnitOnesAreIgnored()
        {
            var test = new CSharpCodeFixTest<NUnitShimMemberNotSupportedAnalyzer, NUnitShimMemberNotSupportedCodeFixProvider, DefaultVerifier>
            {
                TestCode = @"
namespace Mine
{
    static class Is { public static int Positive => 1; }
    static class Assume { public static void That(bool condition) { } }
    class T
    {
        int M() { Assume.That(true); return Is.Positive; }
    }
}",
                ReferenceAssemblies = WithNUnit,
            };
            return test.RunAsync();
        }

        [Test]
        public Task CodeWithoutNUnitIsIgnored()
        {
            var test = new CSharpCodeFixTest<NUnitShimMemberNotSupportedAnalyzer, NUnitShimMemberNotSupportedCodeFixProvider, DefaultVerifier>
            {
                TestCode = @"
static class Is { public static int Positive => 1; }
class T { int M() => Is.Positive; }",
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            };
            return test.RunAsync();
        }

        private static Task VerifyNoFix(string body) => CreateTest(body, fixedBody: null).RunAsync();

        private static Task VerifyFix(string body, string fixedBody) => CreateTest(body, fixedBody).RunAsync();

        private static CSharpCodeFixTest<NUnitShimMemberNotSupportedAnalyzer, NUnitShimMemberNotSupportedCodeFixProvider, DefaultVerifier> CreateTest(string body, string? fixedBody)
        {
            var test = new CSharpCodeFixTest<NUnitShimMemberNotSupportedAnalyzer, NUnitShimMemberNotSupportedCodeFixProvider, DefaultVerifier>
            {
                TestCode = Wrap(body),
                FixedCode = Wrap(fixedBody ?? body),
                ReferenceAssemblies = WithNUnit,
            };
            if (fixedBody is null)
            {
                test.NumberOfIncrementalIterations = 0;
                test.NumberOfFixAllIterations = 0;
                test.NumberOfFixAllInDocumentIterations = 0;
                test.NumberOfFixAllInProjectIterations = 0;
            }
            return test;
        }

        private static string Wrap(string body) => @"using System;
using System.Collections.Generic;
using NUnit.Framework;
class T {
    void M(int x, double d, List<int> list, string s, TestDelegate a) {
" + body.Trim('\r', '\n') + @"
    }
}";
    }
}
