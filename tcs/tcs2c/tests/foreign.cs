using System;
using System.Collections.Generic;
public class Foreign
{
    static Resource resource;
    // host handle (外部 data class) を key にする: 同じ handle の別 wrapper で引ける
    static Dictionary<Handle, int> bodies = new Dictionary<Handle, int>();
    public static void Init()
    {
        resource = Api.Current;
        Console.WriteLine(resource.Version);
        Console.WriteLine(resource.Get(3) + resource.Get(4));
        resource.Touch();
        Console.WriteLine(resource.Get(1) + ":" + resource.Peek(1));
        var options = new Options { Version = 2, Data = new float[] { 1, 2 } };
        Console.WriteLine(options.Scale(5));
        Console.WriteLine(Api.Read(options, Api.Mode.Fast));
        Api.Poll(out string topic, out string payload);
        Console.WriteLine(topic);
        Console.WriteLine(payload);
        Api.Discard(out _, out _, out _);
        bodies[Api.Body(1)] = 10;
        bodies[Api.Body(2)] = 20;
        bodies[Api.Body(2)] = 21;
    }
    // struct は C の struct の値、out は変数の pointer、struct の戻り値は値で host に渡る
    public static void Structs()
    {
        var a = new Vec2 { X = 1, Y = 2 };
        var b = new Vec2 { X = 3, Y = 4 };
        Console.WriteLine(Phys.Dot(a, b));
        var box = new Box2 { Min = a, Max = b, Solid = true, Layer = Layer.Front };
        var c = Phys.Center(box);
        Console.WriteLine(c.X + " " + c.Y);
        Phys.Size(box, out var size, out var solid);
        Console.WriteLine(size.X + " " + size.Y + " " + solid);
        bool found = Phys.Pick(7, out Hit hit);
        bool missing = Phys.Pick(-1, out Hit none);
        Console.WriteLine(found + " " + hit.Id + " " + hit.T + " " + missing + " " + none.Id + " " + none.T);
        if (Phys.Probe(8, out float y) && !Phys.Probe(2, out _)) Console.WriteLine(y);
        Hit? near = Phys.Cast(a, 10);
        Hit? far = Phys.Cast(b, 1);
        Console.WriteLine(near.HasValue + " " + near.Value.Id + " " + near.Value.T + " " + far.HasValue);
        Console.WriteLine(Phys.Score(near, 1) + " " + Phys.Score(far, 1) + " " + Phys.Score(null, 2));
    }

    public static void Frame(float dt)
    {
        Console.WriteLine(dt);
        Console.WriteLine(resource.Version);
        resource = null;
        Console.WriteLine(bodies[Api.Body(2)] + ":" + bodies.ContainsKey(Api.Body(3)) + ":" + bodies.Remove(Api.Body(1)) + ":" + bodies.Count);
    }
}
