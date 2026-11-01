using System;
using System.Collections.Generic;
using TinySystem.Linq;

// List と Dictionary (int と string の key)、List.Sort の比較、LINQ
public class Score
{
    public string Name;
    public int Points;

    public Score(string name, int points)
    {
        Name = name;
        Points = points;
    }
}

public class Collections
{
    public static void Main()
    {
        var xs = new List<int> { 5, 3, 9, 1, 7 };
        xs.Add(4);
        xs[0] = 6;
        Console.WriteLine(xs.Count);
        xs.Sort((a, b) => a - b);
        var line = "";
        foreach (var x in xs) line = line + x + ",";
        Console.WriteLine(line);
        Console.WriteLine(xs.Where(x => x % 2 == 1).Count);
        Console.WriteLine(xs.Select(x => x * x).Sum());

        var ages = new Dictionary<string, int>();
        ages["ann"] = 31;
        ages["bob"] = 25;
        ages["ann"] = 32;
        Console.WriteLine(ages["ann"]);
        Console.WriteLine(ages.Count);
        Console.WriteLine(ages.ContainsKey("bob"));
        Console.WriteLine(ages.ContainsKey("cid"));

        var byId = new Dictionary<int, string> { [7] = "seven", [3] = "three" };
        Console.WriteLine(byId[3] + byId[7]);

        var scores = new List<Score> { new Score("a", 30), new Score("b", 10), new Score("c", 20), new Score("d", 10) };
        var sorted = scores.OrderBy(s => s.Points);
        var names = "";
        foreach (var s in sorted) names = names + s.Name;
        Console.WriteLine(names);

        var fs = new float[] { 1.5f, -2.0f, 0.25f };
        float sum = 0.0f;
        foreach (var f in fs) sum = sum + f;
        Console.WriteLine(sum);
    }
}
