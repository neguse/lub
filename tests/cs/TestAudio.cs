// audio core API の lifecycle テスト。実デバイスが無い環境では miniaudio の
// null backend にフォールバックし、mixer は同じように回る。音を出さないよう
// 全 voice を volume 0 で扱う (lifecycle は volume と独立)。
// snd は key で毎フレーム宣言する resource で、宣言が途切れると
// resource_sweep_after_frames 後に sweep される。
using System;
using System.Collections.Generic;
using Lub;

public static class TestAudio
{
    const int sweepFrames = 10;
    // audio は wall-clock で進むので、待ちの期限は frame 数でなく秒で持つ
    // (frame が速いと frame 数の期限は短い時間で尽きる)。
    const float phaseTimeoutSeconds = 20;
    // decode の入力にする wav (330 Hz、22050 Hz、mono、2205 frame) と、音ではない file。
    // iOS の app は自分の bundle に書けないので、wav は実行中に作らず data に置く。
    const string wavPath = "tests/cs/data/test_audio_decode.wav";
    const string bogusPath = "tests/cs/data/test_indexed_draw.vs.slang";

    static string phase = "init";
    static int phaseFrame;
    static float phaseSeconds;
    static int baselineVoices;
    static List<float> pcmLong = new List<float>(); // 2s loop 用
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

        // wav は file から読むので ready になるまで待つ (Bytes は frame 有効の view)
        Io.LoadBytes(wavPath, out var wav, out _, out _, out _);
        if (wav == null)
            return;
        phaseFrame++;
        phaseSeconds += dt;

        // 毎フレームの宣言。version が同じなら runtime は data を読まない。
        sndLong = Audio.Snd("long", pcmLong, 1, 48000, 1) ?? 0;
        sndShort = Audio.Snd("short", pcmShort, 1, 48000, 1) ?? 0;
        Audio.Decode(wav, out var decodedBytes, out var decodedChannels, out var decodedRate);
        sndDecoded = Audio.SndBytes("decoded", decodedBytes, decodedChannels, decodedRate, 1) ?? 0;

        if (phase == "init")
        {
            Check.That(sndLong != 0 && sndShort != 0, "snd creation failed");
            Check.That(sndLong != sndShort, "distinct content must get distinct snd");
            // 内容 dedupe: 別 key で同じ波形を宣言しても同じ handle (hot reload の要)
            var again = Audio.Snd("long_again", Sine(220, 48000, 96000), 1, 48000);
            Check.That(again == sndLong, "content dedupe failed: " + again + " vs " + sndLong);
            Check.That(Audio.Play(999999) == false, "play with bogus snd must fail");

            // decode 純関数: wav bytes -> pcm (frame 有効の view) -> snd
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
            // 短い oneshot はサンプル末尾で自動解放される
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
            // loop する宣言 voice は宣言が続く限り生き、pitch/volume を毎フレーム
            // 更新できる (負 pitch = 逆再生も宣言できることをここで通す)
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
            // "bgm" だけ宣言し続け、"scratch" は宣言をやめる → fade out して解放
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
            // 非 loop の宣言 voice が自然終了したら、同じ key を宣言し続けても
            // 再発火しない (スクラッチの retrigger は key を変えて行う)。
            // sndShort は 100 frames @48kHz ≈ 2ms だが、audio callback の周期は
            // 負荷次第で伸びるので「終わるまで」はフレーム数でなく voice 数で待つ。
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
            // 終了後も宣言し続ける。retrigger は Voice 呼び出しと同期して
            // game thread 側で起きるので、voices が 1 のままなら再発火していない
            Audio.Voice("bgm", sndLong, new VoiceOpts { Loop = true, Volume = 0 });
            Audio.Voice("blip", sndShort, new VoiceOpts { Volume = 0 });
            Check.That(Audio.Info().Voices == 1, "ended declared key must not retrigger: voices=" + Audio.Info().Voices);
            if (phaseFrame >= 60)
            {
                // "long_again" は init 以来宣言していないが、同じ snd を "long" が
                // 宣言し続けているので sweep されても snd は生きている
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
                // 1 frame だけ宣言して鳴らす。宣言が途切れた snd は sweep で退役し、
                // 鳴っている voice が終わってから PCM が回収される
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
