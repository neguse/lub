namespace TcsSelf;

// host への message (`--watch` の CLI が動いている host に知らせること)。標準出力へ、`@@tcs_<kind> <UTF-8 の byte 数>` の行と本文の組で順に書く
public static class HostMessage
{
    public static void Write(string kind, string text)
    {
        TcsHost.WriteOut("@@tcs_" + kind + " " + Strings.ByteLength(text) + "\n");
        TcsHost.WriteOut(text);
        TcsHost.FlushOut();
    }
}
