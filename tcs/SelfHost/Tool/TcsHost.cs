// tcs の host (SelfHost/Tool) が使う OS の関数の宣言。本体は host ごとに持つ:
// C の build は SelfHost/Native/host.c (tcs2c の --ref)、Lua は SelfHost/Host/tcsc.lua、.NET は Transpiler/DotnetHost.cs。
// このファイルは C と Lua の build で --ref として渡し、.NET の build には入れない
public static class TcsHost
{
    // コマンドラインの引数 (0 番は最初の引数で、実行ファイルの名前を含まない)
    public static int ArgCount() { return 0; }
    public static string Arg(int index) { return ""; }
    public static bool FileExists(string path) { return false; }
    // ファイルの内容 (UTF-8)。読めなければ ""
    public static string ReadFile(string path) { return ""; }
    public static string ReadStdin() { return ""; }
    // 一時ファイルに書いてから置き換える。失敗したら理由を返す (成功は "")
    public static string WriteFileAtomic(string path, string text) { return ""; }
    public static void WriteOut(string text) { }
    public static void WriteErr(string text) { }
    // 標準出力を流す (watch の host への message は書いた順に届ける)
    public static void FlushOut() { }
    // 環境変数 (無ければ "")
    public static string Env(string name) { return ""; }
    // 絶対 path (比べる形。大文字小文字を区別しない file system では小文字にする)
    public static string PathKey(string path) { return ""; }
    // ファイルの同一性 (同じファイルを指す別の path を見分ける)。無いファイルは ""、調べられない host は "-"
    public static string FileIdentity(string path) { return ""; }
    // 変わったかを比べる印 (更新時刻と大きさ)。無いファイルは ""
    public static string FileStamp(string path) { return ""; }
    // tcs と一緒に配るファイル (`runtime/tinysystem.lua`、`runtime/module_registry.lua`、`lib/TinySystem/Linq.cs` など)。無ければ ""
    public static string ToolFile(string name) { return ""; }
    // 今の時刻 (`HH:mm:ss`)
    public static string Clock() { return ""; }
    public static void Sleep(int milliseconds) { }
    public static void SetExitCode(int code) { }
    // 仕事が終わった (host は Step を呼ぶのをやめる)
    public static void SetDone() { }
}
