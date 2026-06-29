using System.Collections.Generic;
using System.Linq;

public class Item
{
    public string Name;
    public int Price;
    public int Count;

    public Item(string name, int price, int count)
    {
        Name = name;
        Price = price;
        Count = count;
    }

    public int TotalValue() => Price * Count;
}

public class Inventory
{
    public List<Item> Items = new List<Item>();
    private Dictionary<string, Item> _itemsByName = new Dictionary<string, Item>();

    public void Add(string name, int price, int count)
    {
        var item = new Item(name, price, count);
        Items.Add(item);
        _itemsByName.Add(name, item);
    }

    public int TotalValue()
    {
        return Items.Select(i => i.TotalValue()).Sum();
    }

    public List<Item> Expensive(int threshold)
    {
        return Items.Where(i => i.Price >= threshold).ToList();
    }

    public Item MostExpensive()
    {
        return Items.OrderBy(i => -i.Price).First();
    }

    public bool HasItem(string name)
    {
        return _itemsByName.ContainsKey(name);
    }

    public int CountOf(string name)
    {
        return _itemsByName[name].Count;
    }

    public string Summary()
    {
        var total = TotalValue();
        var count = Items.Count;
        var best = MostExpensive();
        var shieldCount = HasItem("Shield") ? CountOf("Shield") : 0;
        return $"Items={count} Total={total} Best={best.Name} Shield={shieldCount}";
    }
}

public class Game
{
    public static string Test()
    {
        var inv = new Inventory();
        inv.Add("Sword", 100, 1);
        inv.Add("Potion", 10, 5);
        inv.Add("Shield", 80, 1);
        inv.Add("Arrow", 2, 50);
        return inv.Summary();
    }
}
