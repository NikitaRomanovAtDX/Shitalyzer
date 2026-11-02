using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Shitalyzer.Tests
{
    public class NetFrameworkInsecureXmlTests
    {
        private const string XmlTextReaderFix = "SHIT0012:XmlTextReader";
        private const string SafeXmlFix = "SHIT0012:SafeXml";

        // The shape of DevExpress.Utils.SafeXml the SafeXml fix relies on.
        private const string SafeXmlStub = @"
namespace DevExpress.Utils
{
    public static class SafeXml
    {
        public static System.Xml.XmlDocument CreateDocument(string xmlString) => null;
        public static System.Xml.XmlDocument CreateDocument(System.IO.Stream stream) => null;
        public static TDocument CreateDocument<TDocument>(System.Func<TDocument> createDocument, string xml) where TDocument : System.Xml.XmlDocument => null;
        public static TDocument CreateDocument<TDocument>(System.Func<TDocument> createDocument, System.IO.Stream stream) where TDocument : System.Xml.XmlDocument => null;
    }
}";

        [Test]
        public Task CallsCa3075ReportsOnlyOnNetFrameworkAreReported() => VerifyDiagnostics(@"
doc.{|SHIT0012:LoadXml|}(xml);
doc.{|SHIT0012:Load|}(stream);
doc.{|SHIT0012:Load|}(text);
doc.{|SHIT0012:Load|}(path);
doc?.{|SHIT0012:LoadXml|}(xml);
{|SHIT0012:doc.InnerXml = xml|};
new System.Data.DataTable().{|SHIT0012:ReadXml|}(stream);
new System.Data.DataTable().{|SHIT0012:ReadXmlSchema|}(path);
{|SHIT0012:doc.XmlResolver = new XmlUrlResolver()|};
var other = new XmlDocument { {|SHIT0012:XmlResolver = new XmlUrlResolver()|} };
doc.Load(new {|SHIT0012:XmlTextReader|}(stream));
var reader = new {|SHIT0012:XmlTextReader|}(stream);
var parsing = new XmlTextReader(stream) { {|SHIT0012:DtdProcessing = DtdProcessing.Parse|} };
var later = new XmlTextReader(stream);
{|SHIT0012:later.DtdProcessing = DtdProcessing.Parse|};
");

        [Test]
        public Task SafeCallsAreNotReported() => VerifyDiagnostics(@"
doc.Load(XmlReader.Create(stream));
doc.Load(new XmlTextReader(stream) { DtdProcessing = DtdProcessing.Prohibit });
var ignoring = new XmlTextReader(text) { DtdProcessing = DtdProcessing.Ignore };
var later = new XmlTextReader(stream);
later.DtdProcessing = DtdProcessing.Prohibit;
doc.XmlResolver = null;
var other = new XmlDocument { XmlResolver = null };
doc.DocumentElement.InnerXml = xml;
new System.Data.DataTable().ReadXml(XmlReader.Create(stream));
new System.Data.DataSet().ReadXml(stream);
new MyReader(stream).Read();
");

        [Test]
        public Task NetFrameworkCompilationIsLeftToCa3075()
        {
            var test = new CSharpCodeFixTest<NetFrameworkInsecureXmlAnalyzer, NetFrameworkInsecureXmlCodeFixProvider, DefaultVerifier>
            {
                TestCode = Wrap(@"
doc.LoadXml(xml);
doc.InnerXml = xml;
new XmlTextReader(stream).Read();
"),
                ReferenceAssemblies = ReferenceAssemblies.NetFramework.Net472.Default,
            };
            return test.RunAsync();
        }

        [Test]
        public Task Ca3075PragmaSuppressesTheRule() => VerifyDiagnostics(@"
#pragma warning disable CA3075 // the input is our own resource
doc.LoadXml(xml);
#pragma warning restore CA3075
doc.{|SHIT0012:LoadXml|}(xml);
#pragma warning disable CA1822, CA3075
doc.Load(stream);
#pragma warning restore CA1822, CA3075
doc.{|SHIT0012:Load|}(stream);
#pragma warning disable CA3075 // DtdProcessing is set by a helper
doc.Load(new XmlTextReader(stream));
var tracked = new XmlTextReader(stream);
#pragma warning restore CA3075
doc.Load(new {|SHIT0012:XmlTextReader|}(stream));
");

        [Test]
        public Task Ca3075SuppressMessageSuppressesTheRule()
        {
            var test = new CSharpCodeFixTest<NetFrameworkInsecureXmlAnalyzer, NetFrameworkInsecureXmlCodeFixProvider, DefaultVerifier>
            {
                TestCode = @"using System.Diagnostics.CodeAnalysis;
using System.Xml;
class T
{
    [SuppressMessage(""Security"", ""CA3075:Insecure DTD processing"", Justification = ""Trusted input"")]
    void Suppressed(XmlDocument doc, string xml) => doc.LoadXml(xml);

    void Reported(XmlDocument doc, string xml) => doc.{|SHIT0012:LoadXml|}(xml);
}",
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            };
            return test.RunAsync();
        }

        [Test]
        public Task MessageNamesTheOverload()
        {
            var test = new CSharpCodeFixTest<NetFrameworkInsecureXmlAnalyzer, NetFrameworkInsecureXmlCodeFixProvider, DefaultVerifier>
            {
                TestCode = Wrap("doc.Load(stream);"),
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            };
            test.ExpectedDiagnostics.Add(new DiagnosticResult("SHIT0012", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning)
                .WithSpan(8, 13, 8, 17)
                .WithArguments("'XmlDocument.Load(Stream)' parses DTDs; load the XML through an XmlReader with DTD processing prohibited"));
            return test.RunAsync();
        }

        [Test]
        public Task LoadsAreRoutedThroughAnXmlTextReader() => VerifyFix(XmlTextReaderFix, @"
doc.{|SHIT0012:LoadXml|}(xml);
doc.{|SHIT0012:Load|}(stream);
doc.{|SHIT0012:Load|}(text);
doc?.{|SHIT0012:LoadXml|}(xml);
{|SHIT0012:doc.InnerXml = xml|};
", @"
doc.Load(new XmlTextReader(new StringReader(xml)) { DtdProcessing = DtdProcessing.Prohibit });
doc.Load(new XmlTextReader(stream) { DtdProcessing = DtdProcessing.Prohibit });
doc.Load(new XmlTextReader(text) { DtdProcessing = DtdProcessing.Prohibit });
doc?.Load(new XmlTextReader(new StringReader(xml)) { DtdProcessing = DtdProcessing.Prohibit });
doc.Load(new XmlTextReader(new StringReader(xml)) { DtdProcessing = DtdProcessing.Prohibit });
");

        [Test]
        public Task FileLoadDisposesItsReader() => VerifyFix(XmlTextReaderFix, @"
int reader = 0;
doc.{|SHIT0012:Load|}(path);
", @"
int reader = 0;
using (var reader1 = new XmlTextReader(path) { DtdProcessing = DtdProcessing.Prohibit })
    doc.Load(reader1);
");

        [Test]
        public Task FixAddsTheUsingsItNeeds()
        {
            var test = new CSharpCodeFixTest<NetFrameworkInsecureXmlAnalyzer, NetFrameworkInsecureXmlCodeFixProvider, DefaultVerifier>
            {
                TestCode = @"class T
{
    void M(System.Xml.XmlDocument doc, string xml)
    {
        doc.{|SHIT0012:LoadXml|}(xml);
    }
}",
                FixedCode = @"using System.IO;
using System.Xml;

class T
{
    void M(System.Xml.XmlDocument doc, string xml)
    {
        doc.Load(new XmlTextReader(new StringReader(xml)) { DtdProcessing = DtdProcessing.Prohibit });
    }
}",
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                CodeActionEquivalenceKey = XmlTextReaderFix,
            };
            return test.RunAsync();
        }

        [Test]
        public Task DocumentCreatedRightBeforeLoadXmlBecomesSafeXmlCall() => VerifySafeXmlFix(@"
var plain = new XmlDocument();
plain.{|SHIT0012:LoadXml|}(xml);
XmlDocument implicitNew = new();
implicitNew.{|SHIT0012:LoadXml|}(xml);
var whitespace = new XmlDocument { PreserveWhitespace = true };
whitespace.{|SHIT0012:LoadXml|}(xml);
XmlDocument inner = new() { PreserveWhitespace = true };
{|SHIT0012:inner.InnerXml = xml|};
", @"
var plain = SafeXml.CreateDocument(xml);
XmlDocument implicitNew = SafeXml.CreateDocument(xml);
var whitespace = SafeXml.CreateDocument(() => new XmlDocument { PreserveWhitespace = true }, xml);
XmlDocument inner = SafeXml.CreateDocument(() => new XmlDocument() { PreserveWhitespace = true }, xml);
");

        [Test]
        public Task FieldAssignedRightBeforeLoadXmlBecomesSafeXmlCall()
        {
            var test = new CSharpCodeFixTest<NetFrameworkInsecureXmlAnalyzer, NetFrameworkInsecureXmlCodeFixProvider, DefaultVerifier>
            {
                TestState = { Sources = { @"using System.Xml;
class T
{
    readonly XmlDocument doc;
    T(string xml)
    {
        doc = new XmlDocument { PreserveWhitespace = true };
        doc.{|SHIT0012:LoadXml|}(xml);
    }
}", SafeXmlStub } },
                FixedState = { Sources = { @"using System.Xml;
using DevExpress.Utils;
class T
{
    readonly XmlDocument doc;
    T(string xml)
    {
        doc = SafeXml.CreateDocument(() => new XmlDocument { PreserveWhitespace = true }, xml);
    }
}", SafeXmlStub } },
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                CodeActionEquivalenceKey = SafeXmlFix,
            };
            return test.RunAsync();
        }

        [Test]
        public Task SafeXmlFixNeedsTheCreationRightBeforeTheLoad() => VerifySafeXmlFix(@"
var separated = new XmlDocument();
separated.PreserveWhitespace = true;
separated.{|SHIT0012:LoadXml|}(xml);
var selfReferencing = new XmlDocument();
selfReferencing.{|SHIT0012:LoadXml|}(selfReferencing.OuterXml);
doc.{|SHIT0012:LoadXml|}(xml);
", fixedBody: null);

        [Test]
        public Task UnfixableReportsOfferNoFix() => VerifyFix(XmlTextReaderFix, @"
new System.Data.DataTable().{|SHIT0012:ReadXml|}(stream);
var reader = new {|SHIT0012:XmlTextReader|}(stream);
System.Action load = () => doc.{|SHIT0012:Load|}(path);
var inner = ({|SHIT0012:doc.InnerXml = xml|});
", fixedBody: null);

        private static Task VerifyDiagnostics(string body)
        {
            var test = new CSharpCodeFixTest<NetFrameworkInsecureXmlAnalyzer, NetFrameworkInsecureXmlCodeFixProvider, DefaultVerifier>
            {
                TestCode = Wrap(body),
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            };
            return test.RunAsync();
        }

        private static Task VerifyFix(string equivalenceKey, string body, string? fixedBody)
        {
            var test = new CSharpCodeFixTest<NetFrameworkInsecureXmlAnalyzer, NetFrameworkInsecureXmlCodeFixProvider, DefaultVerifier>
            {
                TestCode = Wrap(body),
                FixedCode = Wrap(fixedBody ?? body),
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                CodeActionEquivalenceKey = equivalenceKey,
            };
            if (fixedBody is null)
                ExpectNoFix(test);
            return test.RunAsync();
        }

        private static Task VerifySafeXmlFix(string body, string? fixedBody)
        {
            var test = new CSharpCodeFixTest<NetFrameworkInsecureXmlAnalyzer, NetFrameworkInsecureXmlCodeFixProvider, DefaultVerifier>
            {
                TestState = { Sources = { Wrap(body), SafeXmlStub } },
                FixedState = { Sources = { Wrap(fixedBody ?? body, withSafeXmlUsing: fixedBody is not null), SafeXmlStub } },
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                CodeActionEquivalenceKey = SafeXmlFix,
            };
            if (fixedBody is null)
                ExpectNoFix(test);
            return test.RunAsync();
        }

        private static void ExpectNoFix(CSharpCodeFixTest<NetFrameworkInsecureXmlAnalyzer, NetFrameworkInsecureXmlCodeFixProvider, DefaultVerifier> test)
        {
            test.NumberOfIncrementalIterations = 0;
            test.NumberOfFixAllIterations = 0;
            test.NumberOfFixAllInDocumentIterations = 0;
            test.NumberOfFixAllInProjectIterations = 0;
        }

        private static string Wrap(string body, bool withSafeXmlUsing = false) => @"using System.IO;
using System.Xml;
" + (withSafeXmlUsing ? "using DevExpress.Utils;\n" : "") + @"
class T
{
    void M(XmlDocument doc, string xml, Stream stream, TextReader text, string path)
    {
" + Indent(body) + @"
    }
}

class MyReader : XmlTextReader
{
    public MyReader(Stream stream) : base(stream) { }
}";

        private static string Indent(string body) =>
            string.Join("\n", body.Trim('\r', '\n').Split('\n').Select(line => "        " + line.TrimEnd('\r')));
    }
}
