// Declarations of the tcs compiler (not compiled; passed with tcs's --ref).
// The implementation is native/tcs/vm_tcs.c, statically linked by the lider player (native/CMakeLists.txt, native and web).
// When running on lub's stock lub_vm, the neighboring Tcs.so (built by scripts/build-native.sh) is loaded as a native extension.
// The request format is in tcs/Tool/Sessions.cs.
// lider uses it as in src/Core/TcsSession.cs.
public static class Tcs
{
    /// <summary>Appends an argument to pass to the next Call.
    /// </summary>
    public static void Arg(string text) { }

    /// <summary>Processes one request made of the queued arguments, puts the response line in reply, and clears the arguments.
    /// Returns true if it was processed.
    /// </summary>
    public static bool Call(out string? reply)
    {
        reply = null;
        return false;
    }
}
