// voice の key は gfx の key と同じ長さまで区別され、長すぎる key は切り詰めずに鳴らさない (false を返す)。
// 音を出さないよう volume 0 で鳴らす。
using System;
using System.Collections.Generic;
using Lub;

public static class TestAudioVoiceKey
{
    static int frame;
    static int snd;
    static int baseline;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    static string Repeat(char c, int n)
    {
        var s = "";
        for (int i = 0; i < n; i++)
            s += c;
        return s;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        var pcm = new List<float>();
        for (int i = 0; i < 4800; i++)
            pcm.Add(0);
        snd = Audio.Snd("voice_key_snd", pcm, 1, 48000, 1) ?? 0;
        if (frame == 1)
        {
            baseline = Audio.Info().Voices;
            return;
        }
        // 先頭の 100 文字が同じで、最後の 1 文字だけ違う 2 つの key。
        var prefix = "game_a/" + Repeat('x', 93);
        var opts = new VoiceOpts { Loop = true, Volume = 0 };
        Check.That(Audio.Voice(prefix + "1", snd, opts), "the first voice must start");
        Check.That(Audio.Voice(prefix + "2", snd, opts), "the second voice must start");
        if (frame < 5)
            return;
        Check.That(Audio.Info().Voices == baseline + 2, "long keys that differ at the end must be two voices, got " + (Audio.Info().Voices - baseline));
        Check.That(!Audio.Voice(Repeat('k', 300), snd, opts), "a key longer than the limit must not play");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
