namespace TinyCs;

// IDs of TinyC# compliance diagnostics. TCS1001 to 1003 are reported by the tcs compiler (Compiler), and the analyzer reports them by running the same compiler
public static class TinyCsDiagnosticIds
{
    public const string UnsupportedSyntax = "TCS1001";

    public const string UnsupportedApi = "TCS1002";

    public const string UnsupportedCollectionNull = "TCS1003";

    // Diagnostic reported only by the analyzer, for running on .NET: for System.Math
    // transcendental functions the .NET implementation runs, and the results differ from the tcs path (runtime/math)
    public const string DotnetMath = "TCS1004";
}
