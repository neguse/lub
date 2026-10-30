// `using TinySystem.Linq;` で書いた LINQ が、readonly record struct の要素と Dictionary の Values で動くこと。
using System;
using System.Collections.Generic;
using Lub;
using TinySystem.Linq;

public readonly record struct LinqEnemy(string Name, int Hp);

public static class TestLinq
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        var enemies = new List<LinqEnemy>
        {
            new LinqEnemy("a", 0),
            new LinqEnemy("b", 3),
            new LinqEnemy("c", 7),
        };

        var alive = enemies.Where(e => e.Hp > 0).OrderByDescending(e => e.Hp).Select(e => e.Name);
        var names = string.Join(",", alive);
        Check.That(names == "c,b", "Where / OrderByDescending / Select: " + names);
        Check.That(enemies.Count == 3, "LINQ が元の List を変えない");
        Check.That(enemies.Sum(e => e.Hp) == 10, "Sum");
        Check.That(enemies.MaxBy(e => e.Hp).Name == "c", "MaxBy");
        Check.That(enemies.FirstOrDefault(e => e.Hp == 3).Name == "b", "FirstOrDefault");

        var hp = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };
        Check.That(hp.Values.Sum() == 3, "Dictionary.Values.Sum");

        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
