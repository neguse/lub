// C の build (tcs2c) で、セルフホストの compiler の driver が使う host の関数の宣言。
// 本体は host.c が実装する (tcs2c の --ref)。
public static class TcscHost
{
    // コマンドラインの引数 (0 番は最初の引数で、実行ファイルの名前を含まない)
    public static int ArgCount() { return 0; }
    public static string Arg(int index) { return ""; }
    // ファイルを読む。読めなければ診断を出して終了する
    public static string ReadFile(string path) { return ""; }
    public static void WriteFile(string path, string text) { }
    public static void WriteOut(string text) { }
    public static void WriteErr(string text) { }
    public static void SetExitCode(int code) { }
    // compile が終わった (host は Step を呼ぶのをやめる)
    public static void SetDone() { }
}
