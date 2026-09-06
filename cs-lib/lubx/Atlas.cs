// 実装ライブラリ lubx の Atlas。
// pixels の List<int> は最初から Lua array table なので変換もキャッシュも不要。
using System.Collections.Generic;
using static Lub;

/// <summary>
/// SpriteBatch 用のテクスチャアトラス。PNG (fromPng) か生ピクセル
/// (fromPixels) から作り、ensure() が true を返したフレームから
/// 描画に使える (web ではロード完了まで false)。
/// </summary>
public class Atlas
{
    public TextureRef? Texture = null;
    public int W = 0;
    public int H = 0;
    public string Key;

    private string? path = null;
    private List<int>? pixels = null;
    private Gfx.PixelFormat format = Gfx.PixelFormat.Rgba8;
    private int? version = null;
    private bool dirty = true;
    private TextureOpts? opts = null;

    public Atlas(string key)
    {
        this.Key = key;
    }

    public static Atlas FromPng(string key, string path, TextureOpts? opts = null)
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
    public static Atlas FromPixels(string key, int w, int h, List<int> pixels,
        int? version = null, TextureOpts? opts = null)
    {
        var a = new Atlas(key);
        a.W = w;
        a.H = h;
        a.pixels = pixels;
        a.version = version;
        a.format = Gfx.PixelFormat.Rgba8;
        a.opts = opts;
        return a;
    }

    /// <summary>
    /// 動的 atlas 用: ピクセル配列を差し替えて変更を宣言する。次の
    /// ensure() で再アップロードされる (lubx.Text の glyph 追加が使う)。
    /// </summary>
    public void UpdatePixels(List<int> pixels)
    {
        this.pixels = pixels;
        this.dirty = true;
    }

    private TextureOpts TextureOpts()
    {
        if (opts != null)
        {
            return opts;
        }
        return new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp };
    }

    public bool Ensure()
    {
        if (pixels != null)
        {
            if (version != null)
            {
                // caller 提供の同一性の値 (定数など)
                Texture = Gfx.UseTexture(Key, W, H, format, pixels, version,
                    TextureOpts());
            }
            else if (dirty || Texture == null)
            {
                // 変更宣言: runtime が実効 version を発行して必ず upload
                Texture = Gfx.UseTexture(Key, W, H, format, pixels, null,
                    TextureOpts());
                dirty = false;
            }
            else
            {
                // 再主張: 前回の実効 version で upload を skip
                Texture = Gfx.UseTexture(Key, W, H, format, pixels,
                    Texture.Version, TextureOpts());
            }
            return true;
        }

        if (path == null || path == "")
        {
            return Texture != null;
        }

        Png.Load(path, out var bytes, out var pw, out var ph, out var pfmt,
            out _, out var pv, out _, out _);
        if (bytes == null)
        {
            return false;
        }
        W = pw;
        H = ph;
        Texture = Gfx.UseTextureBytes(Key, pw, ph, (Gfx.PixelFormat)pfmt, bytes,
            pv, TextureOpts());
        return true;
    }
}
