// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Atlas.hx と対)。
// Haxe 版は pixels を lua.Table.fromArray で変換してキャッシュするが、
// TinyC# の List<int> は最初から Lua array table なので変換もキャッシュも不要。
using System.Collections.Generic;

/// <summary>
/// SpriteBatch 用のテクスチャアトラス。PNG (fromPng) か生ピクセル
/// (fromPixels) から作り、ensure() が true を返したフレームから
/// 描画に使える (web ではロード完了まで false)。
/// </summary>
public class Atlas
{
    public TextureRef? texture = null;
    public int w = 0;
    public int h = 0;
    public string key;

    private string? path = null;
    private List<int>? pixels = null;
    private int format = 0;
    private int? version = null;
    private bool dirty = true;
    private TextureOpts? opts = null;

    public Atlas(string key)
    {
        this.key = key;
    }

    public static Atlas fromPng(string key, string path, TextureOpts? opts = null)
    {
        var a = new Atlas(key);
        a.path = path;
        a.opts = opts;
        return a;
    }

    /// <summary>
    /// version は内容から導ける同一性の値があるときだけ渡す (不変内容なら
    /// 定数)。省略すると「変更宣言 + ref.version 再主張」を Atlas が内部で
    /// 管理する。
    /// </summary>
    public static Atlas fromPixels(string key, int w, int h, List<int> pixels,
        int? version = null, TextureOpts? opts = null)
    {
        var a = new Atlas(key);
        a.w = w;
        a.h = h;
        a.pixels = pixels;
        a.version = version;
        a.format = Gfx.RGBA8;
        a.opts = opts;
        return a;
    }

    /// <summary>
    /// 動的 atlas 用: ピクセル配列を差し替えて変更を宣言する。次の
    /// ensure() で再アップロードされる (lubx.Text の glyph 追加が使う)。
    /// </summary>
    public void updatePixels(List<int> pixels)
    {
        this.pixels = pixels;
        this.dirty = true;
    }

    private TextureOpts textureOpts()
    {
        if (opts != null)
        {
            return opts;
        }
        return new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.CLAMP };
    }

    public bool ensure()
    {
        if (pixels != null)
        {
            if (version != null)
            {
                // caller 提供の同一性の値 (定数など)
                texture = Gfx.use_texture(key, w, h, format, pixels, version,
                    textureOpts());
            }
            else if (dirty || texture == null)
            {
                // 変更宣言: runtime が実効 version を発行して必ず upload
                texture = Gfx.use_texture(key, w, h, format, pixels, null,
                    textureOpts());
                dirty = false;
            }
            else
            {
                // 再主張: 前回の実効 version で upload を skip
                texture = Gfx.use_texture(key, w, h, format, pixels,
                    texture.version, textureOpts());
            }
            return true;
        }

        if (path == null || path == "")
        {
            return texture != null;
        }

        Png.load(path, out var bytes, out var pw, out var ph, out var pfmt,
            out _, out var pv, out _, out _);
        if (bytes == null)
        {
            return false;
        }
        w = pw;
        h = ph;
        texture = Gfx.use_texture(key, pw, ph, pfmt, bytes, pv, textureOpts());
        return true;
    }
}
