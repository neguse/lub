// tcs の host (SelfHost/Tool) が使う OS の関数の宣言。本体は host ごとに持つ:
// C の build は SelfHost/Native/host.c (tcs2c の --ref)、wasm は SelfHost/Web/tcs_web.c、.NET は Transpiler/DotnetHost.cs。
// このファイルは C の build で --ref として渡し、.NET の build には入れない
public static class TcsHost
{
    // コマンドラインの引数 (0 番は最初の引数で、実行ファイルの名前を含まない)
    public static int ArgCount() { return 0; }
    public static string Arg(int index) { return ""; }
    public static bool FileExists(string path) { return false; }
    // 2 つの path が同じ既存のファイルを指すか (link と、大文字小文字を区別しない file system の綴りの違いも)
    public static bool SameFile(string a, string b) { return false; }
    // ファイルの内容 (UTF-8)。読めなければ ""
    public static string ReadFile(string path) { return ""; }
    // 一時ファイルに書いてから置き換える。失敗したら理由を返す (成功は "")
    public static string WriteFileAtomic(string path, string text) { return ""; }
    // byte 列 (1 文字が 1 byte の 0〜255。.tcsb など) を一時ファイルに書いてから置き換える。失敗したら理由を返す
    public static string WriteBinaryAtomic(string path, string bytes) { return ""; }
    public static void WriteOut(string text) { }
    public static void WriteErr(string text) { }
    // 標準出力を流す (watch の host への message は書いた順に届ける)
    public static void FlushOut() { }
    // 変わったかを比べる印 (更新時刻と大きさ)。無いファイルは ""
    public static string FileStamp(string path) { return ""; }
    // tcs と一緒に配るファイル (`lib/TinySystem/Linq.cs`、`runtime/core/tcs_core.c` など)。無ければ ""
    public static string ToolFile(string name) { return ""; }
    public static void Sleep(int milliseconds) { }
    public static void SetExitCode(int code) { }
    // 仕事が終わった (host は Step を呼ぶのをやめる)
    public static void SetDone() { }
}
