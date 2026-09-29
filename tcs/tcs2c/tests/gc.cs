using System;
using System.Collections.Generic;

public class GcNode
{
    public GcNode Next;
    public string Text;
    public int Value;
}

public class GcDerived : GcNode
{
    public GcNode Child;
}

public struct GcPair
{
    public GcNode Item;
}

public class GcProbe
{
    static GcNode root;
    static GcNode[] nodes;
    static List<float> values;
    static GcDerived derived;
    static Func<int> closure;
    static Dictionary<string, GcNode> dictionary;
    static GcPair pair;
    static GcPair[] pairs;
    static List<GcNode> references;
    static GcNode chain;

    public static void Setup()
    {
        root = new GcNode();
        root.Value = 42;
        root.Next = root;
        root.Text = "alive";
        nodes = new GcNode[2];
        nodes[0] = root;
        nodes[1] = new GcNode();
        nodes[1].Value = 7;
        values = new List<float>();
        for (int i = 0; i < 129; i++) values.Add(i);
        derived = new GcDerived();
        derived.Next = root;
        derived.Child = new GcNode();
        derived.Child.Value = 19;
        var captured = new GcNode();
        captured.Value = 23;
        Func<int> callback = () => captured.Value;
        closure = callback;
        dictionary = new Dictionary<string, GcNode>();
        var dictValue = new GcNode();
        dictValue.Value = 29;
        dictionary["kept" + 1] = dictValue;
        var pairValue = new GcPair();
        pairValue.Item = new GcNode();
        pairValue.Item.Value = 31;
        pair = pairValue;
        pairs = new GcPair[1];
        pairs[0] = pairValue;
        references = new List<GcNode>();
        for (int i = 0; i < 129; i++) references.Add(root);
        for (int i = 0; i < 10000; i++)
        {
            var next = new GcNode();
            next.Next = chain;
            chain = next;
        }
    }

    public static void Churn()
    {
        for (int i = 0; i < 1000; i++)
        {
            var a = new GcNode();
            var b = new GcNode();
            a.Next = b;
            b.Next = a;
            a.Text = "dead" + i;
        }
    }

    public static void Check()
    {
        Console.WriteLine(root.Next.Value);
        Console.WriteLine(root.Text);
        Console.WriteLine(nodes[1].Value);
        Console.WriteLine(values[128]);
        Console.WriteLine(derived.Next.Value);
        Console.WriteLine(derived.Child.Value);
        var invoke = closure;
        Console.WriteLine(invoke());
        Console.WriteLine(dictionary["kept" + 1].Value);
        var pairValue = pair;
        Console.WriteLine(pairValue.Item.Value);
        Console.WriteLine(pairs[0].Item.Value);
        Console.WriteLine(references[128].Value);
        int count = 0;
        var cursor = chain;
        while (cursor != null) { count++; cursor = cursor.Next; }
        Console.WriteLine(count);
    }

    public static void Clear()
    {
        root = null;
        nodes = null;
        values = null;
        derived = null;
        closure = null;
        dictionary = null;
        pair = new GcPair();
        pairs = null;
        references = null;
        chain = null;
    }
}
