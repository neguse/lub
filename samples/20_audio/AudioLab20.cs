// lub の samples/20_audio (Haxe 版 AudioLab20.hx) の TinyC# 版 entry。
// 実行: lub samples/20_audio/AudioLab20.csproj (transpile + watch + hot reload)
// サウンドラボ: ImGui で波形パラメータを調整しながら音を作る。
// - 波形はコード合成 (raw PCM → audio_pcm)。パラメータが変わったら作り直し、
//   古い snd は少し待ってから audio_free する (fade 中の voice を殺さないため)。
// - oneshot は play ボタン、継続音は "loop voice" を ON にすると毎フレーム
//   宣言される。pitch は負値 (逆再生) や 0 (停止) も試せる。
using System;
using System.Collections.Generic;

// 差し替えた snd の遅延 free 待ちエントリ (fade out が終わる猶予を置く)
public class RetiredSnd
{
    public int snd;
    public float remaining;
}

public static class AudioLab20
{
    const int RATE = 44100;

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
    static List<RetiredSnd> retired = new List<RetiredSnd>();

    static string[] waveNames = new string[]
    {
        "square", "saw", "triangle", "sine", "noise",
    };

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend });
    }

    static List<float> Synth()
    {
        int n = (int)Math.Floor(duration * RATE);
        var samples = new List<float>();
        float phase = 0.0f;
        // xorshift は Haxe (32bit Int) と同じ列: int32 wrap の << はそのまま、
        // Haxe の >>> は算術シフト >> + 有効 15bit の & マスクで再現する
        int seed = 0x2F6E2B1;
        float hold = 0.0f;
        for (int i = 0; i < n; i++)
        {
            float u = (float)i / n;
            float freq = freq0 + (freq1 - freq0) * u;
            phase = phase + freq / RATE;
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

    // パラメータが変わっていたら snd を作り直す。内容 dedupe があるので
    // 同じパラメータに戻せば同じ handle が返り、free 済みなら作り直される。
    static bool EnsureSnd()
    {
        string key = $"{wave}:{freq0}:{freq1}:{duration}:{decay}:{duty}";
        if (key == lastKey && snd != 0) return false;
        if (snd != 0)
        {
            retired.Add(new RetiredSnd { snd = snd, remaining = 0.5f });
        }
        snd = Audio.audio_pcm(Synth(), 1, RATE);
        lastKey = key;
        return true;
    }

    static void SweepRetired(float dt)
    {
        for (int i = retired.Count - 1; i >= 0; i--)
        {
            var r = retired[i];
            r.remaining = r.remaining - dt;
            if (r.remaining <= 0)
            {
                // 差し替え後も同じ内容で作り直されて現役に戻っている
                // (dedupe で同じ handle) 場合は free しない
                if (r.snd != snd)
                {
                    Audio.audio_free(r.snd);
                }
                retired.RemoveAt(i);
            }
        }
    }

    public static void onFrame(float dt)
    {
        if (Ui.ui_begin("sound lab"))
        {
            Ui.ui_text("waveform: " + waveNames[wave]);
            wave = Ui.ui_slider_int("wave", wave, 0, 4);
            freq0 = Ui.ui_slider_float("freq start (Hz)", freq0, 40, 2000);
            freq1 = Ui.ui_slider_float("freq end (Hz)", freq1, 40, 2000);
            duration = Ui.ui_slider_float("duration (s)", duration, 0.02f, 1.0f);
            decay = Ui.ui_slider_float("decay", decay, 0.0f, 12.0f);
            if (wave == 0)
            {
                duty = Ui.ui_slider_float("duty", duty, 0.05f, 0.95f);
            }
            Ui.ui_separator();

            // pitch は oneshot では発火時に固定 (負値なら末尾から逆再生)、
            // loop voice では鳴っている間もリアルタイムに追従する。
            // 0 で停止、負値で逆再生 (ターンテーブルのつもりで)。
            volume = Ui.ui_slider_float("volume", volume, 0.0f, 1.0f);
            pitch = Ui.ui_slider_float("pitch", pitch, -2.0f, 2.0f);
            pan = Ui.ui_slider_float("pan", pan, -1.0f, 1.0f);
            bool changed = EnsureSnd();
            bool hit = Ui.ui_button("play");
            Ui.ui_same_line();
            playOnChange = Ui.ui_checkbox("play on change", playOnChange);
            if (hit || (changed && playOnChange && !voiceOn))
            {
                Audio.audio_play(snd, new PlayOpts
                {
                    volume = volume,
                    pitch = pitch,
                    pan = pan,
                });
            }
            Ui.ui_separator();

            // 継続音: ON の間だけ毎フレーム宣言する。OFF で fade out。
            voiceOn = Ui.ui_checkbox("loop voice", voiceOn);
            Ui.ui_separator();

            master = Ui.ui_slider_float("master", master, 0.0f, 1.0f);
            Audio.audio_master_volume(master);
            var info = Audio.audio_info();
            Ui.ui_text("voices " + info.voices + " / snds " + info.snds
                + " / " + info.rate + " Hz");
        }
        Ui.ui_end();

        if (voiceOn)
        {
            Audio.audio_voice("lab", snd, new VoiceOpts
            {
                loop = true,
                volume = volume,
                pitch = pitch,
                pan = pan,
            });
        }
        SweepRetired(dt);

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.08f, 0.06f, 0.12f, 1.0f },
        });
        Ui.ui_render();
        Gfx.end_pass();
    }
}
