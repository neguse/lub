namespace Tcs;

// Compliance errors that the tcs VM accepts and `tcs check` and `tcs c` stop on.
// The implementation of a `--ref` interface (RefInterfaceImplementation) is a constraint of how host names are mapped (snake_case), so
// it does not stop a mix of the VM, which calls by C# names, and C modules. Type tests and casts to `--ref` classes / interfaces
// are not stopped either, since the VM can decide them with the type table (docs/tcs/object-model.md §10.6)
public static class VmAccepted
{
    private static readonly string[] Kinds = "RefInterfaceImplementation( RefTypeTest( RefCast( RefInterfaceTypeTest(".Split(' ');

    // Whether the text of a diagnostic is one of them
    public static bool Is(string diagnostic)
    {
        foreach (var kind in Kinds)
        {
            if (diagnostic.Contains("unsupported syntax: " + kind)) return true;
        }
        return false;
    }
}
