// 操作の手応え。音は名前で確かめる。
//   - 保存で save、反映が届くと land が鳴り、枠が揺れ、全面が明滅し、見出しに秒が出る
//   - 2 秒以内に続けて届くと、明滅せず、揺れも小さい
//   - compile error で error が鳴り、直って届くと fixed が鳴り、粒が増える (紙吹雪)
//   - 打つと key、飛ぶと move が鳴り、飛んだ先の窓に印が出る
//   - F4 で 音なし、演出も抑える、全部の順に変わる。音なしでは鳴らず、抑えると明滅しない
//   - 鳴らした音の山 (振幅) はゲームの音 (0.1〜0.3) より小さく、大きい順に fixed、land、error、save、move
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestJuice
{
    static int frames;
    static int stage;
    static bool failed;
    // lider の音の山の上限 (ゲームの音の山は 0.1〜0.3)
    const float MaxPeak = 0.1f;

    public static void OnInit()
    {
        TestInit();
        Lider.OnInit();
    }

    public static void OnEvent(EventData e)
    {
        Lider.OnEvent(e);
    }

    public static void OnQuit()
    {
        Lider.OnQuit();
    }

    public static void OnFrame(float dt)
    {
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void TestFrame(float dt)
    {
        frames++;
        if (failed || frames < 5 || frames % 3 != 0)
            return;
        var w = Lider.Work!;
        if (stage == 0)
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
        else if (stage == 1)
        {
            Lider.LastLandAt = -100;
            w.Focus!.Type("// a\n");
            w.Focus.Save();
        }
        else if (stage == 2)
        {
            Check(Cues.Last == Cues.Save, "saving did not sound: " + Cues.Last);
            w.Reloaded();
        }
        else if (stage == 3)
        {
            var m = Lider.MotionOf(w.FocusWindow!)!;
            Check(Cues.Last == Cues.Land, "landing did not sound: " + Cues.Last);
            Check(Lider.Flash > 0, "landing did not flash");
            Check(m.Shake > 0.5f, "landing did not shake the frame: " + m.Shake);
            Check(m.LandLabel > 0 && m.LandSeconds >= 0, "the seconds are not shown");
            Check(Lider.Panel(w.FocusWindow!).Title.EndsWith("s"), "the title has no seconds: " + Lider.Panel(w.FocusWindow!).Title);
            Lider.Flash = 0;
            m.Shake = 0;
            w.Focus!.Type("// b\n");
            w.Focus.Save();
            w.Reloaded();
        }
        else if (stage == 4)
        {
            var m = Lider.MotionOf(w.FocusWindow!)!;
            Check(Lider.Flash == 0, "a repeated landing flashed");
            Check(m.Shake > 0 && m.Shake < 0.5f, "a repeated landing shook too much: " + m.Shake);
            w.BuildFailed("Target.cs(1,1): error CS0000: broken");
            w.Errors[0].Path = w.Focus!.Path;
        }
        else if (stage == 5)
        {
            Check(Cues.Last == Cues.Error, "the error did not sound: " + Cues.Last);
            Lider.LastLandAt = -100;
            w.Focus!.Type("// c\n");
            w.Focus.Save();
            w.Reloaded();
        }
        else if (stage == 6)
        {
            Check(Cues.Last == Cues.Fixed, "fixing did not celebrate: " + Cues.Last);
            Check(Lider.ParticleCount > 60, "no confetti: " + Lider.ParticleCount);
            w.Back();
            w.Remember(w.Here());
            w.Back();
        }
        else if (stage == 7)
        {
            Check(Cues.Last == Cues.Move, "jumping did not sound: " + Cues.Last);
            Check(Lider.MotionOf(w.FocusWindow!)!.Arrive > 0, "the arrival is not marked");
            Lider.SetJuice(1);
            Cues.Last = "";
            Lider.LastLandAt = -100;
            w.Focus!.Type("// d\n");
            w.Focus.Save();
            w.Reloaded();
        }
        else if (stage == 8)
        {
            Check(Cues.Last == "", "the muted landing sounded: " + Cues.Last);
            Check(Lider.Flash > 0, "effects stopped with the sound");
            Lider.Flash = 0;
            Lider.SetJuice(0);
            Lider.LastLandAt = -100;
            w.Focus!.Type("// e\n");
            w.Focus.Save();
            w.Reloaded();
        }
        else
        {
            Check(Lider.Flash == 0, "the reduced effects still flash");
            var order = new List<string> { Cues.Fixed, Cues.Land, Cues.Error, Cues.Save, Cues.Move };
            for (var i = 0; i < order.Count; i++)
            {
                var peak = Cues.Peaks.TryGetValue(order[i], out var p) ? p : -1;
                Check(peak > 0 && peak <= MaxPeak, order[i] + " peaked at " + peak + ", want 0 to " + MaxPeak);
                if (i > 0 && Cues.Peaks.ContainsKey(order[i - 1]))
                    Check(peak < Cues.Peaks[order[i - 1]], order[i] + " (" + peak + ") is not quieter than " + order[i - 1] + " (" + Cues.Peaks[order[i - 1]] + ")");
            }
            if (failed)
                return;
            Console.WriteLine("PASS");
            App.Quit(0);
        }
        stage++;
    }

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
