using System;
public class Foreign
{
    static Resource resource;
    public static void Init()
    {
        resource = Api.Current;
        Console.WriteLine(resource.Version);
        Console.WriteLine(Api.Read(new Options { Version = 2, Data = new float[] { 1, 2 } }, Api.Mode.Fast));
        Api.Poll(out string topic, out string payload);
        Console.WriteLine(topic);
        Console.WriteLine(payload);
    }
    public static void Frame(float dt)
    {
        Console.WriteLine(dt);
        Console.WriteLine(resource.Version);
        resource = null;
    }
}
