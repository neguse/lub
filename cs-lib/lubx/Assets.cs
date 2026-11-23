// Assets of the implementation library lubx.
// Return values are stub handle types (ShaderRef / TextureRef).
// The multi-return of Io.load_* is received through out arguments.

using Lub;

namespace Lubx;

/// <summary>Turns the asset loading boilerplate into one line.
/// Declarative style that returns null until ready (call every frame and skip drawing while it is null).
/// </summary>
public static class Assets
{
    /// <summary>Reads the vs/fs Slang and calls Gfx.UseShader.
    /// null if either is not ready.
    /// </summary>
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

    /// <summary>Reads an audio file such as wav and declares a snd.
    /// null if bytes or decode is not ready.
    /// If there is a snd for the file's version, returns it without decoding; otherwise (first time, file updated, swept) decodes and declares it.
    /// Call it every frame to keep declaring it.
    /// </summary>
    public static int? Wav(string key, string path)
    {
        Io.LoadBytes(path, out var bytes, out var version, out _, out _);
        if (bytes == null)
        {
            return null;
        }
        // A declaration without data does not look at channels / rate
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

    /// <summary>Reads an Ogg Vorbis file and declares a snd (same shape as Wav, decoding with Lubx.Vorbis).
    /// null if bytes or decode is not ready.
    /// Call it every frame to keep declaring it.
    /// </summary>
    public static int? Ogg(string key, string path)
    {
        Io.LoadBytes(path, out var bytes, out var version, out _, out _);
        if (bytes == null)
        {
            return null;
        }
        // A declaration without data does not look at channels / rate
        var snd = Audio.SndBytes(key, null, 0, 0, version);
        if (snd != null)
        {
            return snd;
        }
        Vorbis.Decode(bytes, out var pcm, out var channels, out var rate);
        if (pcm == null)
        {
            return null;
        }
        return Audio.SndBytes(key, pcm, channels, rate, version);
    }

    /// <summary>Declares a render target with Gfx.UseTexture.
    /// filter defaults to Linear, wrap defaults to Clamp.
    /// version is derived from w, h, so the target is recreated when the size changes.
    /// </summary>
    public static TextureRef? RenderTarget(string key, int w, int h,
        Gfx.PixelFormat fmt, Gfx.Filter? filter = null, Gfx.Wrap? wrap = null)
    {
        var opts = new TextureOpts();
        opts.Target = true;
        opts.Filter = filter ?? Gfx.Filter.Linear;
        opts.Wrap = wrap ?? Gfx.Wrap.Clamp;
        return Gfx.UseTexture(key, w, h, fmt, null, w * 65536 + h, opts);
    }
}
