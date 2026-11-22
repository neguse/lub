// Atlas of the implementation library lubx.
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>
/// Texture atlas for SpriteBatch.
/// Created from a PNG (fromPng) or raw pixels (fromPixels), and usable for drawing from the frame in which ensure() returns true (on the web, false until loading completes).
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
    /// Pass version only when there is an identity value derivable from the content (a constant for immutable content).
    /// If omitted, Atlas manages "declaring changes + re-asserting ref.version" internally.
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

    /// <summary> Whether ensure() has not been called yet since UpdatePixels (the next ensure() uploads).
    /// </summary>
    public bool Changed => pixels != null && dirty;

    /// <summary> For dynamic atlases: replaces the pixel array and declares a change.
    /// Re-uploaded at the next ensure() (used by glyph additions in Lubx.Text).
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
            // Identity value provided by the caller, or the previous effective version if nothing changed
            int? claim = version;
            if (claim == null && !dirty && Texture != null)
                claim = Texture.Version;
            // The binding converts every element of pixels before looking at version, so do not pass pixels while the runtime holds the same version
            var live = claim != null ? Gfx.LookupTexture(Key) : null;
            if (live != null && live.Version == claim)
            {
                Texture = Gfx.UseTexture(Key, W, H, format, null, claim,
                    TextureOpts());
            }
            else
            {
                // An omitted version declares a change: the runtime issues one and always uploads
                Texture = Gfx.UseTexture(Key, W, H, format, pixels, version,
                    TextureOpts());
                dirty = false;
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
