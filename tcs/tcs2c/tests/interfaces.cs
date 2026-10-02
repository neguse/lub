using System;

public interface Target { int Read(); }
public interface Movable { void Move(); }
public class Body { public int Value; public int Read() { return Value; } }
public class MovingBody : Body, Target, Movable
{
    public void Move() { Value += 7; }
}
public class Interfaces
{
    static Target target;
    static Movable moving;
    public static void Init()
    {
        var body = new MovingBody();
        target = body;
        moving = body;
    }
    public static void Check()
    {
        moving.Move();
        Console.WriteLine(target.Read());
        object saved = target;
        Console.WriteLine(((Target)saved).Read());
        Console.WriteLine(((MovingBody)target).Value);
        target = null;
        moving = null;
    }
    public static void Main() { Init(); Check(); }
}
