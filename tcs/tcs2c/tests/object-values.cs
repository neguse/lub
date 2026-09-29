using System;
using System.Collections.Generic;

public class ValueNode { public int Number; public object Link; }

public class ObjectValues
{
    static List<object> values;
    static Dictionary<string, object> bindings;
    const float Unit = 1;
    public static void Init()
    {
        var node = new ValueNode();
        node.Number = 42;
        node.Link = node;
        values = new List<object> { 7, 1.5f, true, node, new float[] { 2, 3 } };
        bindings = new Dictionary<string, object> { ["node"] = node, ["array"] = new float[] { 4, 5 } };
    }
    public static void Check()
    {
        Console.WriteLine((int)values[0]);
        Console.WriteLine((float)values[1]);
        Console.WriteLine((bool)values[2]);
        Console.WriteLine(((ValueNode)values[3]).Number);
        Console.WriteLine(((float[])values[4])[1]);
        Console.WriteLine(((float[])bindings["array"])[0]);
        Console.WriteLine(values[3] == bindings["node"]);
        object unit = Unit;
        Console.WriteLine((float)unit);
        Console.WriteLine(values[3] is ValueNode);
        values = null;
        bindings = null;
    }
    public static void Main() { Init(); Check(); }
}
