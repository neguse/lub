// 画像と音の file を出す板。画像はそのまま貼り、音は波形を描いて、出したときに 1 度鳴らす。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public class AssetView
{
    const int RowH = 26;
    const int Pad = 8;
    const int PxW = 640;
    const int PxH = 400;
    const float W = PanelView.BaseW * 0.6f;
    const float H = W * PxH / PxW;
    const float R = Space.R - 0.2f;
    const int Bars = PxW - Pad * 2;
    const string TextureKey = "lider_asset_image";
    const string SoundKey = "lider_asset_sound";

    readonly SpriteBatch batch = new SpriteBatch("lider_asset", PxW, PxH);
    readonly DeclareGap gap = new DeclareGap();
    TextureRef? board;
    TextureRef? image;
    int imageW;
    int imageH;
    string path = "";
    string drawn = "";
    float[]? peaks;
    // 大きさか長さ、読めなければその旨
    public string Info { get; private set; } = "";
    public bool Played { get; private set; }

    public static bool IsAsset(string name)
    {
        return IsImage(name) || IsSound(name);
    }

    static bool IsImage(string name)
    {
        return name.EndsWith(".png") || name.EndsWith(".jpg") || name.EndsWith(".jpeg");
    }

    static bool IsSound(string name)
    {
        return name.EndsWith(".wav") || name.EndsWith(".ogg") || name.EndsWith(".mp3");
    }

    // 出す file を読み、板を描き直す (画面への pass の外で毎 frame 呼ぶ。読んだ物の宣言が途切れないように)
    public void Prepare(string shown, string title, MonoText text)
    {
        if (shown != path)
        {
            path = shown;
            image = null;
            peaks = null;
            Info = "";
            Played = false;
        }
        if (path == "")
            return;
        if (IsImage(path))
            LoadImage();
        else
            LoadSound();
        batch.Ensure();
        text.Glyphs.Ensure();
        board = Assets.RenderTarget("lider_asset", PxW * text.Density, PxH * text.Density, Gfx.PixelFormat.Rgba8);
        if (gap.Lapsed())
            drawn = "";
        var key = path + "\n" + Info + "\n" + text.Version + "\n" + text.Density + "\n" + (peaks != null);
        if (board == null || key == drawn)
            return;
        drawn = key;
        Gfx.BeginPass(new PassOpts { Target = board, ClearColor = new float[] { 0, 0, 0, 0 } });
        batch.Begin();
        // SpriteBatch は atlas を初めて使った順に描くので、文字より先に塗る
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        text.Draw(batch, title + "  " + Info, Pad * 2, Pad, Theme.Ink, 0, PxW - Pad);
        if (peaks != null)
        {
            var mid = RowH + (PxH - RowH) * 0.5f;
            var scale = (PxH - RowH - Pad * 2) * 0.5f;
            for (var i = 0; i < peaks.Length; i++)
            {
                var h = Math.Max(1, peaks[i] * scale);
                batch.Rect(Pad + i, mid - h, 1, h * 2, Theme.Accent);
            }
        }
        batch.Flush();
        Gfx.EndPass();
    }

    void LoadImage()
    {
        Png.Load(path, out var bytes, out var w, out var h, out var format, out _, out var version, out var status, out _);
        if (status == Io.Status.Error)
        {
            Info = "読めません";
            return;
        }
        if (bytes == null && image == null)
            return;
        imageW = w;
        imageH = h;
        Info = w + " x " + h;
        image = Gfx.UseTextureBytes(TextureKey, w, h, (Gfx.PixelFormat)format, bytes, version,
            new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
    }

    void LoadSound()
    {
        Io.LoadBytes(path, out var bytes, out var version, out var status, out _);
        if (status == Io.Status.Error)
            Info = "読めません";
        if (bytes == null)
            return;
        var snd = Audio.SndBytes(SoundKey, null, 0, 0, version);
        if (snd == null || peaks == null)
        {
            Bytes? pcm = null;
            var channels = 0;
            var rate = 0;
            if (path.EndsWith(".ogg"))
                Vorbis.Decode(bytes, out pcm, out channels, out rate);
            else
                Audio.Decode(bytes, out pcm, out channels, out rate);
            if (pcm == null)
            {
                Info = "読めません";
                return;
            }
            peaks = Peaks(pcm);
            var centi = (int)Math.Round(pcm.Length * 25.0f / channels / rate);
            Info = centi / 100 + "." + (centi % 100 < 10 ? "0" : "") + centi % 100 + " 秒";
            snd = Audio.SndBytes(SoundKey, pcm, channels, rate, version);
        }
        if (snd is int sound && !Played)
        {
            Played = true;
            Audio.Play(sound);
        }
    }

    // f32 の列の、区間ごとの大きさ (0 から 1)
    static float[] Peaks(Bytes pcm)
    {
        const int Probes = 8;
        var result = new float[Bars];
        var samples = pcm.Length / 4;
        for (var bar = 0; bar < Bars; bar++)
            for (var probe = 0; probe < Probes; probe++)
            {
                var at = (int)((bar + (float)probe / Probes) / Bars * samples) * 4;
                if (at + 3 < pcm.Length)
                    result[bar] = Math.Max(result[bar], Magnitude(pcm, at));
            }
        return result;
    }

    // little endian の f32 の絶対値
    static float Magnitude(Bytes b, int at)
    {
        var exponent = ((b.Get(at + 3) & 0x7F) << 1) | (b.Get(at + 2) >> 7);
        if (exponent == 0)
            return 0;
        var mantissa = 1 + (b.Get(at + 2) & 0x7F) / 128.0f;
        return Math.Min(1, mantissa * (float)Math.Pow(2, exponent - 127));
    }

    // 上の端の真ん中を円筒の (angle, top) にして描く
    public void Draw(Mat4 vp, float angle, float top, float scale)
    {
        if (path == "" || board == null)
            return;
        var k = R / Space.R;
        var h = H * scale;
        Theme.Plate(vp, Theme.Surface, angle, (top - h * 0.5f) * k, R + 0.003f, W * scale * k, h * k);
        Quad3d.Curved(vp, board, Color.Rgb(1, 1, 1), angle, (top - h * 0.5f) * k, R, W * scale * k, h * k, 0, 1);
        if (image == null || imageW == 0 || imageH == 0)
            return;
        // 字の下の範囲に、縦横の比を保って収める
        var areaW = W * scale * (PxW - Pad * 2) / PxW;
        var areaH = h * (PxH - RowH - Pad * 2) / PxH;
        var fit = Math.Min(areaW / imageW, areaH / imageH);
        var center = top - h * (RowH + (PxH - RowH) * 0.5f) / PxH;
        Quad3d.Curved(vp, image, Color.Rgb(1, 1, 1), angle, center * k, R - 0.01f, imageW * fit * k, imageH * fit * k, 0, 1);
    }
}
