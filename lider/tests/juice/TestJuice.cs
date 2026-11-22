// Feedback of operations. Sounds are checked by name.
//   - Saving plays save, and when the update arrives, land plays, the frame shakes, the whole screen flashes, and the seconds appear in the title
//   - When updates arrive in a row within 2 seconds, it does not flash and the shake is smaller
//   - A compile error plays error, and when the fix arrives, fixed plays and the particles increase (confetti)
//   - Typing plays key, jumping plays move, and a marker appears on the window jumped to
//   - juice:cycle changes in the order no sound, reduced effects, everything. With no sound nothing plays, and with reduced effects it does not flash
//   - The peak (amplitude) of the played sounds is lower than the game's sounds (0.1 to 0.3), and from highest to lowest they are fixed, land, error, save, move
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestJuice
{
    static int frames;
    static int stage;
    static bool failed;
    // Upper limit of lider's sound peak (the game's sound peak is 0.1 to 0.3)
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
            Check(Lider.TitleOf(w.FocusWindow!).Label.EndsWith("s"), "the title has no seconds: " + Lider.TitleOf(w.FocusWindow!).Label);
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
