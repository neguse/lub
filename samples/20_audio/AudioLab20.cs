// Entry for lub's samples/20_audio.
// Run: lub samples/20_audio/AudioLab20.csproj (transpile + watch + hot reload) Sound lab: make sounds while tuning waveform parameters with ImGui.
// - Waveforms are synthesized in code (raw PCM → Audio.Snd).
//   It is declared every frame with key "lab", and
//   when the parameters change, version is advanced and it is recreated. The runtime
//   retires the old snd and reclaims it after the voices that are fading out finish.
// - oneshot uses the play button; the continuous sound is declared every frame
//   when "loop voice" is ON. pitch can also be tried with negative values (reverse playback) or 0 (stopped).
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public static class AudioLab20
{
    const int rate = 44100;

    // --- Synthesis parameters (state that ImGui edits directly) ---------------------------
    static int wave = 0; // 0=square 1=saw 2=triangle 3=sine 4=noise
    static float freq0 = 440.0f;
    static float freq1 = 440.0f;
    static float duration = 0.25f;
    static float decay = 5.0f;
    static float duty = 0.5f;

    // --- Playback parameters ------------------------------------------------------
    static float volume = 0.5f;
    static float pitch = 1.0f;
    static float pan = 0.0f;
    static bool playOnChange = true;
    static bool voiceOn = false;
    static float master = 1.0f;

    static int snd = 0;
    static string lastKey = "";
    static List<float>? samples = null;
    static int version = 0;

    static string[] waveNames = new string[]
    {
        "square", "saw", "triangle", "sine", "noise",
    };

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        App.Config(new ConfigOpts { Backend = backend });
    }

    static List<float> Synth()
    {
        int n = (int)Math.Floor(duration * rate);
        var samples = new List<float>();
        float phase = 0.0f;
        // xorshift32: << with int32 wrap is used as is; the logical shift >>> is reproduced with the arithmetic shift
        // >> plus an & mask of the valid 15 bits (the golden depends on this sequence)
        int seed = 0x2F6E2B1;
        float hold = 0.0f;
        for (int i = 0; i < n; i++)
        {
            float u = (float)i / n;
            float freq = freq0 + (freq1 - freq0) * u;
            phase = phase + freq / rate;
            if (phase >= 1.0f)
            {
                phase = phase - 1.0f;
                // noise is a sample & hold that advances the LFSR every period (pitched noise)
                seed = seed ^ (seed << 13);
                seed = seed ^ ((seed >> 17) & 0x7FFF);
                seed = seed ^ (seed << 5);
                hold = (seed & 0xFFFF) / 32768.0f - 1.0f;
            }
            float s = wave switch
            {
                0 => phase < duty ? 1.0f : -1.0f,
                1 => phase * 2.0f - 1.0f,
                2 => phase < 0.5f ? phase * 4.0f - 1.0f : 3.0f - phase * 4.0f,
                3 => (float)Math.Sin(phase * 2.0f * (float)Math.PI),
                _ => hold,
            };
            samples.Add(s * (float)Math.Exp(-decay * u));
        }
        return samples;
    }

    // If the parameters changed, regenerate the waveform and advance version.
    // snd is declared with the same key every frame (if version is unchanged, the runtime does not read the waveform).
    // Content is deduplicated, so returning to the same parameters returns the same handle.
    static bool EnsureSnd()
    {
        string key = $"{wave}:{freq0}:{freq1}:{duration}:{decay}:{duty}";
        bool changed = key != lastKey || samples == null;
        if (changed)
        {
            samples = Synth();
            version = version + 1;
            lastKey = key;
        }
        snd = Audio.Snd("lab", samples!, 1, rate, version) ?? 0;
        return changed;
    }

    public static void OnFrame(float dt)
    {
        if (Ui.BeginWindow("sound lab"))
        {
            Ui.Text("waveform: " + waveNames[wave]);
            wave = Ui.SliderInt("wave", wave, 0, 4);
            freq0 = Ui.SliderFloat("freq start (Hz)", freq0, 40, 2000);
            freq1 = Ui.SliderFloat("freq end (Hz)", freq1, 40, 2000);
            duration = Ui.SliderFloat("duration (s)", duration, 0.02f, 1.0f);
            decay = Ui.SliderFloat("decay", decay, 0.0f, 12.0f);
            if (wave == 0)
            {
                duty = Ui.SliderFloat("duty", duty, 0.05f, 0.95f);
            }
            Ui.Separator();

            // pitch is fixed at trigger time for oneshot (a negative value plays in reverse from the end), and for loop voice it follows in real time while playing.
            // 0 stops, a negative value plays in reverse (think of a turntable).
            volume = Ui.SliderFloat("volume", volume, 0.0f, 1.0f);
            pitch = Ui.SliderFloat("pitch", pitch, -2.0f, 2.0f);
            pan = Ui.SliderFloat("pan", pan, -1.0f, 1.0f);
            bool changed = EnsureSnd();
            bool hit = Ui.Button("play");
            Ui.SameLine();
            playOnChange = Ui.Checkbox("play on change", playOnChange);
            if (hit || (changed && playOnChange && !voiceOn))
            {
                Audio.Play(snd, new PlayOpts
                {
                    Volume = volume,
                    Pitch = pitch,
                    Pan = pan,
                });
            }
            Ui.Separator();

            // Continuous sound: declared every frame only while ON.
            // OFF fades it out.
            voiceOn = Ui.Checkbox("loop voice", voiceOn);
            Ui.Separator();

            master = Ui.SliderFloat("master", master, 0.0f, 1.0f);
            Audio.MasterVolume(master);
            var info = Audio.Info();
            Ui.Text("voices " + info.Voices + " / snds " + info.Snds
                + " / " + info.Rate + " Hz");
        }
        Ui.EndWindow();

        if (voiceOn)
        {
            Audio.Voice("lab", snd, new VoiceOpts
            {
                Loop = true,
                Volume = volume,
                Pitch = pitch,
                Pan = pan,
            });
        }

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.08f, 0.06f, 0.12f, 1.0f },
        });
        Ui.Render();
        Gfx.EndPass();
    }
}
