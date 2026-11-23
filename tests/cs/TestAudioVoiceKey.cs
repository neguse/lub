// Voice keys are distinguished up to the same length as gfx keys; a key that is too long is not truncated but is not played (returns false).
// Plays at volume 0 so that no sound is output.
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
        // Two keys whose first 100 characters are the same and that differ only in the last character.
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
