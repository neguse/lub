using System;

// class の field (struct の field を含む)、class の配列、struct の配列、static の struct
public struct Vec2
{
    public float X;
    public float Y;
}

public class Body
{
    public Vec2 Pos;
    public Vec2 Vel;
    public int Id;
    public bool Alive;
    public Body Next;
}

public class Objects
{
    static Vec2 gravity;
    static Body head;

    static Body Make(int id)
    {
        var b = new Body();
        b.Id = id;
        b.Alive = id % 2 == 0;
        b.Pos.X = id * 1.5f;
        b.Vel.Y = -id;
        return b;
    }

    static Vec2 Sum(Vec2 a, Vec2 b)
    {
        var r = new Vec2();
        r.X = a.X + b.X;
        r.Y = a.Y + b.Y;
        return r;
    }

    public static void Main()
    {
        gravity.Y = -9.5f;
        for (int i = 0; i < 4; i++)
        {
            var b = Make(i);
            b.Next = head;
            head = b;
        }
        var cur = head;
        while (cur != null)
        {
            cur.Vel.Y = cur.Vel.Y + gravity.Y;
            cur.Pos.Y = cur.Pos.Y + cur.Vel.Y;
            Console.WriteLine(cur.Id);
            Console.WriteLine(cur.Alive);
            Console.WriteLine(cur.Pos.Y);
            cur = cur.Next;
        }
        var arr = new Vec2[3];
        arr[1].X = 2.0f;
        arr[2] = Sum(arr[1], gravity);
        var copy = arr[2];
        copy.X = 100.0f;
        Console.WriteLine(arr[2].X);
        Console.WriteLine(arr[2].Y);
        Console.WriteLine(copy.X);
        Console.WriteLine(arr.Length);
        var bodies = new Body[2];
        Console.WriteLine(bodies[0] == null);
    }
}
