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
        Console.WriteLine(Api.Read(new Options { Version = 2, Data = new float[] { 1, 2 } }, Api.Mode.Fast));
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
