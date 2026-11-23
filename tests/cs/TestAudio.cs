// Lifecycle test of the audio core API.
// In environments without a real device it falls back to the miniaudio null backend, and the mixer runs the same way.
// All voices use volume 0 so that no sound is output (the lifecycle is independent of volume).
// snd is a resource declared every frame by key; when its declarations stop, it is swept after ConfigOpts.ResourceSweepAfterFrames.
using System;
using System.Collections.Generic;
using Lub;

public static class TestAudio
{
    const int sweepFrames = 10;
    // Audio advances by wall-clock time, so wait deadlines are kept in seconds, not frames (with fast frames, a frame-count deadline runs out in a short time).
    const float phaseTimeoutSeconds = 20;
    // The wav used as decode input (330 Hz, 22050 Hz, mono, 2205 frames) and a file that is not audio.
    // iOS apps cannot write to their own bundle, so the wav is placed in data instead of being created at runtime.
    const string wavPath = "tests/cs/data/test_audio_decode.wav";
    const string bogusPath = "tests/cs/data/test_indexed_draw.vs.slang";

    static string phase = "init";
    static int phaseFrame;
    static float phaseSeconds;
    static int baselineVoices;
    static List<float> pcmLong = new List<float>(); // For the 2s loop
    static List<float> pcmShort = new List<float>(); // ~2ms oneshot
    static int sndLong;
    static int sndShort;
    static int sndDecoded;
    static int sndSwept;

    static List<float> Sine(float freq, int rate, int frames)
    {
        var t = new List<float>();
        for (int i = 0; i < frames; i++)
            t.Add((float)Math.Sin(2 * Math.PI * freq * i / rate) * 0.5f);
        return t;
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = 320,
            Height = 180,
            ResourceSweepAfterFrames = sweepFrames,
        });
        pcmLong = Sine(220, 48000, 96000);
        pcmShort = Sine(440, 48000, 100);
    }

    static void Fail(string message)
    {
        Check.Fail(phase + ": " + message);
    }

    static void SetPhase(string next)
    {
        phase = next;
        phaseFrame = 0;
        phaseSeconds = 0;
    }

    static bool TimedOut()
    {
        return phaseSeconds > phaseTimeoutSeconds;
    }

    public static void OnFrame(float dt)
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.02f, 0.03f, 0.04f, 1.0f } });
        Gfx.EndPass();

        // The wav is read from a file, so wait until it is ready (Bytes is a view valid for the frame)
        Io.LoadBytes(wavPath, out var wav, out _, out _, out _);
        if (wav == null)
            return;
        phaseFrame++;
        phaseSeconds += dt;

        // Declaration every frame.
        // If the version is the same, the runtime does not read the data.
        sndLong = Audio.Snd("long", pcmLong, 1, 48000, 1) ?? 0;
        sndShort = Audio.Snd("short", pcmShort, 1, 48000, 1) ?? 0;
        Audio.Decode(wav, out var decodedBytes, out var decodedChannels, out var decodedRate);
        sndDecoded = Audio.SndBytes("decoded", decodedBytes, decodedChannels, decodedRate, 1) ?? 0;

        if (phase == "init")
        {
            Check.That(sndLong != 0 && sndShort != 0, "snd creation failed");
            Check.That(sndLong != sndShort, "distinct content must get distinct snd");
            // Content dedupe: declaring the same waveform under a different key gives the same handle (the key to hot reload)
            var again = Audio.Snd("long_again", Sine(220, 48000, 96000), 1, 48000);
            Check.That(again == sndLong, "content dedupe failed: " + again + " vs " + sndLong);
            Check.That(Audio.Play(999999) == false, "play with bogus snd must fail");

            // Pure decode function: wav bytes -> pcm (view valid for the frame) -> snd
            Check.That(decodedBytes != null, "decode failed");
            Check.That(decodedChannels == 1 && decodedRate == 22050, "decode meta mismatch: ch=" + decodedChannels + " rate=" + decodedRate);
            Check.That(sndDecoded != 0, "snd from decoded bytes failed");
            Io.LoadBytes(bogusPath, out var bogus, out _, out _, out _);
            if (bogus == null)
            {
                phaseFrame--;
                return;
            }
            Audio.Decode(bogus, out var bogusBytes, out _, out _);
            Check.That(bogusBytes == null, "bogus decode must return nil");
            Check.That(Audio.Info().Snds == 3, "expected 3 snds, got " + Audio.Info().Snds);

            SetPhase("oneshot");
            return;
        }

        if (phase == "oneshot")
        {
            // A short oneshot is released automatically at the end of its samples
            if (phaseFrame == 1)
            {
                baselineVoices = Audio.Info().Voices;
                Check.That(Audio.Play(sndShort, new PlayOpts { Volume = 0 }), "oneshot play failed");
                Check.That(Audio.Info().Voices == baselineVoices + 1, "oneshot voice not spawned");
                return;
            }
            if (Audio.Info().Voices <= baselineVoices)
            {
                SetPhase("declared");
                return;
            }
            if (TimedOut())
                Fail("oneshot voice did not end");
            return;
        }

        if (phase == "declared")
        {
            // A looping declared voice lives as long as it keeps being declared, and its pitch/volume can be updated every frame (this also checks that a negative pitch = reverse playback can be declared)
            Audio.Voice("bgm", sndLong, new VoiceOpts { Loop = true, Volume = 0, Pitch = 1 + 0.001f * phaseFrame });
            Audio.Voice("scratch", sndLong, new VoiceOpts { Loop = true, Volume = 0, Pitch = -0.5f });
            if (phaseFrame == 1)
            {
                Check.That(Audio.Info().Voices >= 2, "declared voices not spawned");
                return;
            }
            if (phaseFrame >= 20)
            {
                Check.That(Audio.Info().Voices >= 2, "declared voices died while declared");
                SetPhase("undeclare");
            }
            return;
        }

        if (phase == "undeclare")
        {
            // Keep declaring only "bgm" and stop declaring "scratch" → it fades out and is released
            Audio.Voice("bgm", sndLong, new VoiceOpts { Loop = true, Volume = 0 });
            if (Audio.Info().Voices == 1)
            {
                SetPhase("tombstone");
                return;
            }
            if (TimedOut())
                Fail("undeclared voice did not fade out: voices=" + Audio.Info().Voices);
            return;
        }

        if (phase == "tombstone")
        {
            // When a non-loop declared voice finishes naturally, it does not retrigger even if the same key keeps being declared (scratch retriggers are done by changing the key).
            // sndShort is 100 frames @48kHz ≈ 2ms, but the audio callback period grows with load, so "until it ends" is waited for by voice count, not frame count.
            Audio.Voice("bgm", sndLong, new VoiceOpts { Loop = true, Volume = 0 });
            Audio.Voice("blip", sndShort, new VoiceOpts { Volume = 0 });
            if (Audio.Info().Voices == 1)
            {
                SetPhase("tombstone_hold");
                return;
            }
            if (TimedOut())
                Fail("blip voice did not end: voices=" + Audio.Info().Voices);
            return;
        }

        if (phase == "tombstone_hold")
        {
            // Keep declaring after it ends.
            // Retriggers happen on the game thread side, synchronously with the Voice call, so if voices stays 1, it has not retriggered
            Audio.Voice("bgm", sndLong, new VoiceOpts { Loop = true, Volume = 0 });
            Audio.Voice("blip", sndShort, new VoiceOpts { Volume = 0 });
            Check.That(Audio.Info().Voices == 1, "ended declared key must not retrigger: voices=" + Audio.Info().Voices);
            if (phaseFrame >= 60)
            {
                // "long_again" has not been declared since init, but "long" keeps declaring the same snd, so the snd stays alive even after a sweep
                Check.That(Audio.Info().Snds == 3, "shared snd must survive: snds=" + Audio.Info().Snds);
                SetPhase("sweep");
            }
            return;
        }

        if (phase == "sweep")
        {
            Audio.Voice("bgm", sndLong, new VoiceOpts { Loop = true, Volume = 0 });
            if (phaseFrame == 1)
            {
                // Declare for just 1 frame and play.
                // A snd whose declarations stopped is retired by the sweep, and its PCM is reclaimed after the playing voice ends
                sndSwept = Audio.Snd("swept", Sine(550, 48000, 4800), 1, 48000) ?? 0;
                Check.That(sndSwept != 0, "snd for sweep test failed");
                Check.That(Audio.Play(sndSwept, new PlayOpts { Volume = 0 }), "play before sweep failed");
                Check.That(Audio.Info().Snds == 4, "expected 4 snds, got " + Audio.Info().Snds);
                return;
            }
            if (phaseFrame <= sweepFrames)
            {
                Check.That(Audio.Play(sndSwept, new PlayOpts { Volume = 0 }), "play within the sweep window must succeed");
                return;
            }
            var info = Audio.Info();
            if (info.Voices == 1 && info.Snds == 3)
            {
                Check.That(Audio.Play(sndSwept) == false, "play after sweep must fail");
                Check.Pass();
                return;
            }
            if (TimedOut())
                Fail("swept snd not reclaimed: voices=" + info.Voices + " snds=" + info.Snds);
            return;
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
