// Test that views part of a large array with tvm_inspect and finds instances reachable from statics with tvm_inspect_instances (called by inspect_test.c).
public class Box
{
    public int Hp = 7;
}

public class Node
{
    public Node? Next;
    public Box? Item;
}

public struct Spot
{
    public Box? B;
}

public static class Game
{
    public static Box[] Boxes = Make();
    // Finding instances: Solo is also reachable from Pair but appears only once, Ring is a cycle, At is inside a struct, Chain stops at the depth limit
    public static Box Solo = new Box();
    public static System.Collections.Generic.List<Box> Pair = new System.Collections.Generic.List<Box> { Solo, new Box() };
    public static Node Ring = MakeRing();
    public static Spot At = new Spot { B = new Box() };
    public static Node Chain = MakeChain(5);

    static Box[] Make()
    {
        var a = new Box[1 << 20];
        a[(1 << 20) - 1] = new Box();
        return a;
    }

    static Node MakeRing()
    {
        var a = new Node();
        var b = new Node();
        a.Next = b;
        b.Next = a;
        return a;
    }

    static Node MakeChain(int n)
    {
        var head = new Node();
        var at = head;
        for (int i = 1; i < n; i++)
        {
            at.Next = new Node();
            at = at.Next;
        }
        return head;
    }
}
