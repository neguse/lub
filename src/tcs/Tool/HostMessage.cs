namespace Tcs;

// Messages to the host (what the `--watch` CLI tells the host it runs in). Written in order to standard output, each as a `@@tcs_<kind> <UTF-8 byte count>` line followed by the body
public static class HostMessage
{
    public static void Write(string kind, string text)
    {
        TcsHost.WriteOut("@@tcs_" + kind + " " + Strings.ByteLength(text) + "\n");
        TcsHost.WriteOut(text);
        TcsHost.FlushOut();
    }
}
