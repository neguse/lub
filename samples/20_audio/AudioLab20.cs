// lub の samples/20_audio の entry。
// 実行: lub samples/20_audio/AudioLab20.csproj (transpile + watch + hot reload)
// サウンドラボ: ImGui で波形パラメータを調整しながら音を作る。
// - 波形はコード合成 (raw PCM → Audio.Snd)。key "lab" で毎フレーム宣言し、
//   パラメータが変わったら version を進めて作り直す。古い snd は runtime が
//   退役させ、fade 中の voice が終わってから回収する。
// - oneshot は play ボタン、継続音は "loop voice" を ON にすると毎フレーム
//   宣言される。pitch は負値 (逆再生) や 0 (停止) も試せる。
using System;
using System.Collections.Generic;
using static Lub;

public static class AudioLab20
{
    const int rate = 44100;

    // --- 合成パラメータ (ImGui が直接いじる状態) ---------------------------
    static int wave = 0; // 0=square 1=saw 2=triangle 3=sine 4=noise
    static float freq0 = 440.0f;
    static float freq1 = 440.0f;
    static float duration = 0.25f;
    static float decay = 5.0f;
    static float duty = 0.5f;

    // --- 再生パラメータ ------------------------------------------------------
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
        Lub.Config(new ConfigOpts { Backend = backend });
    }

    static List<float> Synth()
    {
        int n = (int)Math.Floor(duration * rate);
        var samples = new List<float>();
        float phase = 0.0f;
        // xorshift32: int32 wrap の << はそのまま、論理シフト >>> は算術シフト
        // >> + 有効 15bit の & マスクで再現する (golden はこの列に依存する)
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
                // noise は周期ごとに LFSR を進める sample & hold (pitched noise)
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

    // パラメータが変わっていたら波形を作り直して version を進める。snd は毎
    // フレーム同じ key で宣言する (version が同じなら runtime は波形を読まない)。
    // 内容 dedupe があるので同じパラメータに戻せば同じ handle が返る。
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
        snd = Audio.Snd("lab", samples!, 1, rate, version);
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

            // pitch は oneshot では発火時に固定 (負値なら末尾から逆再生)、
            // loop voice では鳴っている間もリアルタイムに追従する。
            // 0 で停止、負値で逆再生 (ターンテーブルのつもりで)。
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

            // 継続音: ON の間だけ毎フレーム宣言する。OFF で fade out。
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
