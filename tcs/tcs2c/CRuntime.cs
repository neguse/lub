namespace TinyCs.Tcs2c;

// 生成 C の前に埋め込む C runtime (runtime/c。説明は runtime/c/README.md)。
// ファイルの中身をそのまま、prelude.h、math、fmt、utf8、core.c、gc.c、collections.c、strings.c、lib.c の順に置く。
internal sealed partial class CEmitter
{
    private static readonly Lazy<string> RuntimePrelude = new(() => RuntimeC("prelude.h"));
    private static readonly Lazy<string> RuntimeCore = new(() => RuntimeC("core.c"));
    private static readonly Lazy<string> RuntimeGc = new(() => RuntimeC("gc.c"));
    private static readonly Lazy<string> RuntimeCollections = new(() => RuntimeC("collections.c"));
    private static readonly Lazy<string> RuntimeStrings = new(() => RuntimeC("strings.c"));
    private static readonly Lazy<string> RuntimeLib = new(() => RuntimeC("lib.c"));

    private static string RuntimeC(string name)
    {
        using var stream = typeof(CEmitter).Assembly.GetManifestResourceStream("c/" + name)
            ?? throw new InvalidOperationException($"missing embedded runtime/c/{name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
