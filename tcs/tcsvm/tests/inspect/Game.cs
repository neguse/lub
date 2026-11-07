// 大きな配列の一部を tvm_inspect で見て、static から辿れる個体を tvm_inspect_instances で探すテスト (inspect_test.c が呼ぶ)。
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
    // 個体を探す: Solo は Pair からも辿れるが 1 度だけ、Ring は輪、At は struct の中、Chain は深さの上限で止まる
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
