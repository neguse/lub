using System;
using System.Collections.Generic;

// Even if a fault occurs in a closure inside the runtime (List.Sort), the trace continues to the outer frames
public class FaultCompare
{
    static int Cmp(int a, int b)
    {
        return (a - b) / (a - b);
    }

    public static void Main()
    {
        var xs = new List<int> { 3, 1, 2, 2 };
        Console.WriteLine(xs.Count);
        xs.Sort((a, b) => Cmp(a, b));
        Console.WriteLine(99);
    }
}
