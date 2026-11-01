namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private static readonly string RuntimePrelude = CoreRuntime("tcs_prelude.h");

    // runtime/fmt の tcs_format_f32 を使うので、埋め込む runtime (CRuntime.Embedded) の後に置く
    private static readonly string RuntimeCore = CoreRuntime("tcs_core.c");
}
