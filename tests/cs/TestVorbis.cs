// Lubx.Vorbis.Decode と Lubx.Assets.Ogg。
// test_vorbis_decode.ogg は 22050Hz の stereo で 0.2 秒 (4410 frame)、左が振幅 0.5 の 440Hz の正弦波、右が無音。
// 戻した PCM の channel 数、rate、frame 数と、左右の RMS を見る (正弦波の RMS は 0.5 / sqrt(2))。
// ogg でない bytes は pcm が null。
using System;
using Lub;
using Lubx;

public static class TestVorbis
{
    const string oggPath = "tests/cs/data/test_vorbis_decode.ogg";
    const string notOggPath = "tests/cs/data/test_audio_decode.wav";

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    // little endian の f32 を bit から組み立てる
    static float F32(Bytes bytes, int index)
    {
        int bits = bytes.Get(index) | bytes.Get(index + 1) << 8 | bytes.Get(index + 2) << 16 | bytes.Get(index + 3) << 24;
        int exponent = (bits >> 23) & 255;
        int mantissa = bits & 0x7fffff;
        if (exponent == 0 && mantissa == 0)
            return 0;
        float value = (1 + mantissa / 8388608f) * (float)Math.Pow(2, exponent - 127);
        return bits < 0 ? -value : value;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        Io.LoadBytes(oggPath, out var ogg, out _, out _, out _);
        Io.LoadBytes(notOggPath, out var notOgg, out _, out _, out _);
        if (ogg == null || notOgg == null)
            return;

        Vorbis.Decode(ogg, out var pcm, out var channels, out var rate);
        Check.That(pcm != null, "the ogg did not decode");
        Check.That(channels == 2 && rate == 22050, "decode meta mismatch: ch=" + channels + " rate=" + rate);
        int frames = pcm!.Length / 4 / channels;
        Check.That(frames == 4410, "expected 4410 frames, got " + frames);
        float left = 0, right = 0;
        for (int i = 0; i < frames; i++)
        {
            float l = F32(pcm, (i * 2) * 4), r = F32(pcm, (i * 2 + 1) * 4);
            left += l * l;
            right += r * r;
        }
        float leftRms = (float)Math.Sqrt(left / frames), rightRms = (float)Math.Sqrt(right / frames);
        Check.That(Math.Abs(leftRms - 0.3536f) < 0.02f, "left RMS must be about 0.354, got " + leftRms);
        Check.That(rightRms < 0.01f, "right must be silent, got RMS " + rightRms);

        Vorbis.Decode(notOgg, out var notPcm, out _, out _);
        Check.That(notPcm == null, "a wav must not decode as ogg");

        Check.That(Assets.Ogg("vorbis-test", oggPath) != null, "Assets.Ogg did not declare a snd");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
