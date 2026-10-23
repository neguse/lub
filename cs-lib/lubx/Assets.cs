// 実装ライブラリ lubx の Assets。
// 戻り値は stub のハンドル型 (ShaderRef / BufferRef)。Io.load_* の
// multi-return は out 引数で受ける。

using Lub;

namespace Lubx;

/// <summary>アセット読み込みの定型を1行にする。ready まで null を返す宣言型
/// (毎フレーム呼んで null の間は描画をスキップする)。</summary>
public static class Assets
{
    /// <summary>vs/fs の Slang を読んで Gfx.UseShader。どちらか未 ready なら null。</summary>
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

    /// <summary>wav などの音声ファイルを読んで snd を宣言する。
    /// bytes か decode が未 ready なら null。
    /// ファイルの version の snd があれば decode せずにそれを返し、無ければ (初回、ファイルの更新、sweep 済み) decode して宣言する。
    /// 毎フレーム呼んで宣言し続けること。</summary>
    public static int? Wav(string key, string path)
    {
        Io.LoadBytes(path, out var bytes, out var version, out _, out _);
        if (bytes == null)
        {
            return null;
        }
        // data を渡さない宣言は channels / rate を見ない
        var snd = Audio.SndBytes(key, null, 0, 0, version);
        if (snd != null)
        {
            return snd;
        }
        Audio.Decode(bytes, out var pcm, out var channels, out var rate);
        if (pcm == null)
        {
            return null;
        }
        return Audio.SndBytes(key, pcm, channels, rate, version);
    }

    /// <summary>Gfx.UseTexture で render target を宣言する。
    /// filter 省略時 Linear、wrap 省略時 Clamp。
    /// version は w, h から決めるので、サイズが変わると作り直される。</summary>
    public static TextureRef? RenderTarget(string key, int w, int h,
        Gfx.PixelFormat fmt, Gfx.Filter? filter = null, Gfx.Wrap? wrap = null)
    {
        var opts = new TextureOpts();
        opts.Target = true;
        opts.Filter = filter ?? Gfx.Filter.Linear;
        opts.Wrap = wrap ?? Gfx.Wrap.Clamp;
        return Gfx.UseTexture(key, w, h, fmt, null, w * 65536 + h, opts);
    }

    /// <summary>load_floats + use_buffer。data 未 ready なら null。
    /// usage は Gfx.BufferType.Storage / Index。</summary>
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
