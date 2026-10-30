; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SHIT0001 | Shitalyzer.Naming | Warning | Variable named 'package' breaks the Java converter.
SHIT0002 | Shitalyzer.Compatibility | Warning | Member (method overload or type) missing in .NET Framework 4.7.2.
SHIT0003 | Shitalyzer.Conversion | Info | Value-type local captured in a lambda cannot be converted to Java.
SHIT0005 | Shitalyzer.Conversion | Warning | Local function (method inside a method) cannot be converted to Java.
SHIT0006 | Shitalyzer.Conversion | Warning | Property collides with an explicit Get/Set method that the Java converter generates.
SHIT0007 | Shitalyzer.Usage | Warning | Argument validated by hand instead of through the Guard helper.
SHIT0008 | Shitalyzer.Conversion | Warning | LINQ comparer overload missing in the CS2J Java runtime; the comparer is dropped.
SHIT0009 | Shitalyzer.Conversion | Warning | LINQ method or overload not implemented by the CS2J Java runtime.
SHIT0010 | Shitalyzer.Conversion | Warning | Null-coalescing assignment '??=' is emitted verbatim by the Java converter.
SHIT0011 | Shitalyzer.Conversion | Warning | NUnit constraint member missing in the Java NUnit shim (devexpress-bcl-nunit).
