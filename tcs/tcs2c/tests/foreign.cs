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
        var shadow = new Shadow();
        Resource upcast = shadow;
        Console.WriteLine(shadow.Get(1) + ":" + upcast.Get(1) + ":" + upcast.Peek(1) + ":" + resource.Peek(1) + ":" + new Plain().Twice() + ":" + new Wide().Get("x"));
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
    public static void Frame(float dt)
    {
        Console.WriteLine(dt);
        Console.WriteLine(resource.Version);
        resource = null;
        Console.WriteLine(bodies[Api.Body(2)] + ":" + bodies.ContainsKey(Api.Body(3)) + ":" + bodies.Remove(Api.Body(1)) + ":" + bodies.Count);
    }
}
// stub の method を user subclass が再宣言したら IlInvoke は実行時型で解決する
public class Shadow : Resource
{
    public int Get(int index) { return 42; }
    public override int Peek(int i) { return 50 + i; }
}
public class Wide : Resource { public int Get(string key) { return key.Length; } }
public class Plain : Resource { public int Twice() { return Get(1) * 2; } }
