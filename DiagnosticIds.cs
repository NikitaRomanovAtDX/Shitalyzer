namespace Shitalyzer
{
    /// <summary>
    /// SHIT = "Source Has Incompatible Traits".
    /// Diagnostics that flag C# constructs our C#-to-Java converter cannot handle.
    /// </summary>
    internal static class DiagnosticIds
    {
        /// <summary>A variable is named <c>package</c>, a reserved word in the Java output.</summary>
        public const string PackageVariableName = "SHIT0001";

        /// <summary>A string method overload is used that does not exist in .NET Framework 4.7.2.</summary>
        public const string NetFrameworkIncompatibleMethod = "SHIT0002";

        /// <summary>A value-type local is captured/mutated inside a lambda.</summary>
        public const string ValueTypeCapturedInLambda = "SHIT0003";
    }

    internal static class Categories
    {
        public const string Naming = "Shitalyzer.Naming";
        public const string Compatibility = "Shitalyzer.Compatibility";
        public const string Conversion = "Shitalyzer.Conversion";
    }
}
