using System;

// string: concatenation and string conversion (il-spec §13), content equality, null handling, interpolation, ?? and ?:
public enum Mood { Calm = 0, Angry = 5 }

public class Strings
{
    static string Label(Mood m)
    {
        return m switch
        {
            Mood.Calm => "calm",
            Mood.Angry => "angry",
            _ => "?",
        };
    }

    public static void Main()
    {
        string a = "ab";
        string b = "a" + "b";
        Console.WriteLine(a == b);
        Console.WriteLine(a != "ac");
        string n = null;
        Console.WriteLine("[" + n + "]");
        Console.WriteLine(n ?? "fallback");
        int i = 42;
        float f = 0.1f;
        bool t = true;
        Console.WriteLine($"i={i} f={f} t={t} big={1e10f} neg={-0.0f}");
        Console.WriteLine(Label(Mood.Angry) + "/" + Label(Mood.Calm));
        Console.WriteLine(i > 40 ? "big" : "small");
        Console.WriteLine("tab\there \"quoted\" \\ é");
    }
}
