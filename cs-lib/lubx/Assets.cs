// 実装ライブラリ lubx の Assets。
// 戻り値は stub のハンドル型 (ShaderRef / BufferRef)。Io.load_* の
// multi-return は out 引数で受ける。

using System.Collections.Generic;
using static Lub;

/// <summary>アセット読み込みの定型を1行にする。ready まで null を返す宣言型
/// (毎フレーム呼んで null の間は描画をスキップする)。</summary>
public static class Assets
{
    /// <summary>vs/fs の Slang を読んで use_shader。どちらか未 ready なら null。</summary>
    public static ShaderRef? Shader(string key, string vsPath, string fsPath)
    {
        Io.LoadText(vsPath, out var vs, out var vsVersion, out _, out _);
        Io.LoadText(fsPath, out var fs, out var fsVersion, out _, out _);
        if (vs == null || fs == null)
        {
            return null;
        }
        return Gfx.UseShader(key, vs, fs, vsVersion * 31 + fsVersion);
    }

    /// <summary>load_floats + use_buffer。data 未 ready なら null。
    /// usage は Gfx.BufferType.Vertex / Index。</summary>
    public static BufferRef? Floats(string key, Gfx.BufferType usage, string path)
    {
        Io.LoadFloats(path, out var data, out var version, out _, out _);
        if (data == null)
        {
            return null;
        }
        return Gfx.UseBuffer(key, usage, data, version);
    }
}
