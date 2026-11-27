namespace Tcs;

// Messages to the host (what the `--watch` CLI tells the host it runs in). Written in order to standard output, each as a `@@tcs_<kind> <UTF-8 byte count>` line followed by the body
public static class HostMessage
{
    public static void Write(string kind, string text)
    {
        CliProcess.Out("@@tcs_" + kind + " " + Strings.ByteLength(text) + "\n");
        CliProcess.Out(text);
        CliProcess.Flush();
    }
}
