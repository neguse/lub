using System;
using System.Collections.Generic;

// closure は変数を捕捉する (il-spec §7): 書き換えが共有される、foreach の変数は反復ごと、
// 入れ子の lambda、引数と self の捕捉、static method の method group
public class Counter
{
    public int Count;
    public string Name = "c";

    public Func<int> Incrementer(int step)
    {
        return () => { Count = Count + step; return Count; };
    }
}

public class Closures
{
    static int Twice(int x) { return x * 2; }

    static int Apply(Func<int, int> f, int x) { return f(x); }

    public static void Main()
    {
        int total = 0;
        Action<int> add = n => { total = total + n; };
        add(3);
        add(4);
        Console.WriteLine(total);

        var fs = new List<Func<int>>();
        foreach (var k in new List<int> { 10, 20, 30 })
            fs.Add(() => k);
        foreach (var f in fs)
            Console.WriteLine(f());

        Func<int, Func<int, int>> adder = a => b => a + b + total;
        Console.WriteLine(adder(1)(2));
        total = 100;
        Console.WriteLine(adder(1)(2));

        var c = new Counter();
        var inc = c.Incrementer(5);
        inc();
        Console.WriteLine(inc());
        Console.WriteLine(c.Count);
        Console.WriteLine(c.Name);

        Console.WriteLine(Apply(Twice, 21));
        Console.WriteLine(Apply(x => x - 1, 21));
    }
}
