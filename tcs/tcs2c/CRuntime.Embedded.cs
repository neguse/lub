using System.Reflection;
using System.Text;

namespace TinyCs.Tcs2c;

// runtime/ の C (Lua の module と同じ実装) を生成 C に埋め込む。
// Math の超越関数 (runtime/math。.NET の TinySystem.Math とも同じ)、float の文字列化 (runtime/fmt)、文字列の操作 (runtime/utf8。.NET の TinySystem.Text とも同じ)。
// 関数は TCS_MATH_STATIC / TCS_FMT_STATIC / TCS_UTF8_STATIC で static にする。
internal sealed partial class CEmitter
{
    private static readonly string[] MathSources =
    [
        "tcs_math.h", "tcs_math_internal.h", "atan2f.c", "exppowf.c", "log10f.c",
        "log2f.c", "logf.c", "sincosf.c", "tanf.c",
    ];

    private static readonly string[] FmtSources = ["tcs_fmt.h", "f2s.c"];

    private static readonly Lazy<string> RuntimeMath =
        new(() => EmbedRuntime("math", "TCS_MATH_STATIC", MathSources));

    private static readonly Lazy<string> RuntimeFmt =
        new(() => EmbedRuntime("fmt", "TCS_FMT_STATIC", FmtSources));

    private static readonly Lazy<string> RuntimeUtf8 =
        new(() => EmbedRuntime("utf8", "TCS_UTF8_STATIC", ["tcs_utf8.h", "tcs_utf8.c"]));

    private static string EmbedRuntime(string dir, string staticDefine, string[] names)
    {
        var assembly = typeof(CEmitter).Assembly;
        var text = new StringBuilder($"/* ---- runtime/{dir} ---- */\n#define {staticDefine} 1\n");
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream($"{dir}/{name}")
                ?? throw new InvalidOperationException($"missing embedded runtime/{dir}/{name}");
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("#include \"", StringComparison.Ordinal)) continue;
                text.Append(line).Append('\n');
            }
        }
        return text.ToString();
    }
}
