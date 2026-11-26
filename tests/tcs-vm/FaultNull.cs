using System;

// Calling an instance method on a null receiver faults (il-spec §12)
public class Node
{
    public int V;
    public int Get() { return 1; }
}

public class FaultNull
{
    public static void Main()
    {
        Node n = new Node();
        Console.WriteLine(n.Get());
        n = null;
        Console.WriteLine(n.Get());
    }
}
