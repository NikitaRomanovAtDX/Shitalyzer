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

        /// <summary>A member (method overload or type) is used that does not exist in .NET Framework 4.7.2.</summary>
        public const string NetFrameworkIncompatibleMethod = "SHIT0002";

        /// <summary>A value-type local is captured/mutated inside a lambda.</summary>
        public const string ValueTypeCapturedInLambda = "SHIT0003";

        // SHIT0004 (an iterator using 'yield') is retired: CS2J converts iterators to lazy state machines
        // (JLazyEnumerator) since dxvcs #50029, 2026-10-06. Do not reuse the id.

        /// <summary>A local function (method declared inside a method) has no equivalent in the Java output.</summary>
        public const string LocalFunctionNotSupported = "SHIT0005";

        /// <summary>A property coexists with a <c>Get</c>/<c>Set</c> method (in the same or a base class) that collides with its generated Java accessor.</summary>
        public const string PropertyAccessorMethodConflict = "SHIT0006";

        /// <summary>An argument is validated by hand instead of through the <c>Guard</c> helper.</summary>
        public const string ManualArgumentValidation = "SHIT0007";

        /// <summary>A LINQ call passes a comparer to an overload the CS2J Java runtime does not have.</summary>
        public const string LinqComparerNotSupported = "SHIT0008";

        /// <summary>A LINQ method, or an overload of one, is not implemented by the CS2J Java runtime.</summary>
        public const string LinqMethodNotSupported = "SHIT0009";

        /// <summary>The null-coalescing assignment <c>??=</c>, which the CS2J converter emits verbatim as invalid Java.</summary>
        public const string CoalesceAssignmentNotSupported = "SHIT0010";

        /// <summary>An NUnit constraint member (or <c>Assume</c>/<c>Iz</c>) that the Java NUnit shim the converted tests compile against does not have.</summary>
        public const string NUnitShimMemberNotSupported = "SHIT0011";
    }

    internal static class Categories
    {
        public const string Naming = "Shitalyzer.Naming";
        public const string Compatibility = "Shitalyzer.Compatibility";
        public const string Conversion = "Shitalyzer.Conversion";
        public const string Usage = "Shitalyzer.Usage";
    }
}
