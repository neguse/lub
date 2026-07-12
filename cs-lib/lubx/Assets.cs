// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Assets.hx と対)。
// Haxe 版の Dynamic 戻り値は stub のハンドル型 (ShaderRef / BufferRef) に
// 型付けし直す。Io.load_* の multi-return は out 引数で受ける。

using System.Collections.Generic;

/// <summary>アセット読み込みの定型を1行にする。ready まで null を返す宣言型
/// (毎フレーム呼んで null の間は描画をスキップする)。</summary>
public static class Assets
{
    /// <summary>vs/fs の Slang を読んで use_shader。どちらか未 ready なら null。</summary>
    public static ShaderRef? shader(string key, string vsPath, string fsPath)
    {
        Io.load_text(vsPath, out var vs, out var vsVersion, out _, out _);
        Io.load_text(fsPath, out var fs, out var fsVersion, out _, out _);
        if (vs == null || fs == null)
        {
            return null;
        }
        return Gfx.use_shader(key, vs, fs, vsVersion * 31 + fsVersion);
    }

    /// <summary>load_floats + use_buffer。data 未 ready なら null。
    /// usage は Gfx.VERTEX / Gfx.INDEX。</summary>
    public static BufferRef? floats(string key, int usage, string path)
    {
        Io.load_floats(path, out var data, out var version, out _, out _);
        if (data == null)
        {
            return null;
        }
        return Gfx.use_buffer(key, usage, data, version);
    }
}
