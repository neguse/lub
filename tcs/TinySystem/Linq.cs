// Read with the same annotations regardless of the project's nullable setting (.NET projects pull it in through TinySystem.props)
#nullable enable
namespace TinySystem.Linq;

// The using is placed inside the namespace so System is looked up before Func / List that users define globally
using System;
using System.Collections.Generic;

/// <summary>
/// LINQ for <c>List&lt;T&gt;</c>. It has eagerly evaluated operations with the same names as System.Linq, and operations that return a sequence return a new
/// <c>List&lt;T&gt;</c>. tcs compiles this file to C / VM IL together with user code, and .NET
/// runs it as is, so the three paths run the same implementation (q.md Q18). The namespace is separate from TinySystem
/// so that Math / String / Random do not become ambiguous next to <c>using System;</c>.
/// <para>Common contract: callbacks are called exactly once per element, in order from the first. Iteration goes up to the Count at the time
/// of the call, so if a callback shrinks the receiver, out-of-range indexes are read. Min / Max (int / float) of an empty
/// sequence fault.</para>
/// </summary>
public static class Enumerable
{
    public static List<T> Where<T>(this List<T> list, Func<T, bool> predicate)
    {
        var result = new List<T>();
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            var item = list[i];
            if (predicate(item)) result.Add(item);
        }
        return result;
    }

    public static List<TResult> Select<T, TResult>(this List<T> list, Func<T, TResult> selector)
    {
        var result = new List<TResult>();
        int n = list.Count;
        for (int i = 0; i < n; i++) result.Add(selector(list[i]));
        return result;
    }

    public static bool Any<T>(this List<T> list) => list.Count > 0;

    public static bool Any<T>(this List<T> list, Func<T, bool> predicate)
    {
        int n = list.Count;
        for (int i = 0; i < n; i++)
            if (predicate(list[i])) return true;
        return false;
    }

    public static bool All<T>(this List<T> list, Func<T, bool> predicate)
    {
        int n = list.Count;
        for (int i = 0; i < n; i++)
            if (!predicate(list[i])) return false;
        return true;
    }

    public static int Count<T>(this List<T> list, Func<T, bool> predicate)
    {
        int count = 0;
        int n = list.Count;
        for (int i = 0; i < n; i++)
            if (predicate(list[i])) count++;
        return count;
    }

    /// <summary>default(T) if empty.</summary>
    public static T FirstOrDefault<T>(this List<T> list) =>
        list.Count > 0 ? list[0] : default!;

    /// <summary>default(T) if no element matches.</summary>
    public static T FirstOrDefault<T>(this List<T> list, Func<T, bool> predicate)
    {
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            var item = list[i];
            if (predicate(item)) return item;
        }
        return default!;
    }

    /// <summary>Faults if an intermediate sum goes out of int range (checked).</summary>
    public static int Sum(this List<int> list)
    {
        int sum = 0;
        int n = list.Count;
        for (int i = 0; i < n; i++) sum = checked(sum + list[i]);
        return sum;
    }

    /// <summary>Faults if an intermediate sum goes out of int range (checked).</summary>
    public static int Sum<T>(this List<T> list, Func<T, int> selector)
    {
        int sum = 0;
        int n = list.Count;
        for (int i = 0; i < n; i++) sum = checked(sum + selector(list[i]));
        return sum;
    }

    /// <summary>Adds from the first element as float (System.Linq adds as double and then converts to float).</summary>
    public static float Sum(this List<float> list)
    {
        float sum = 0f;
        int n = list.Count;
        for (int i = 0; i < n; i++) sum += list[i];
        return sum;
    }

    /// <summary>Adds from the first element as float (System.Linq adds as double and then converts to float).</summary>
    public static float Sum<T>(this List<T> list, Func<T, float> selector)
    {
        float sum = 0f;
        int n = list.Count;
        for (int i = 0; i < n; i++) sum += selector(list[i]);
        return sum;
    }

    // Min / Max follow the same rules as System.Linq: among equal values the earlier one is kept. float NaN is the smallest value;
    // Min returns NaN as soon as it sees it (the remaining selectors are not called), and Max is the maximum of the non-NaN values if there is
    // at least one. string uses ordinal order, skips null, and is null if there is only null.
    // An empty sequence faults for int / float (NoElements) and gives null for string

    public static int Min(this List<int> list) => Min(list, Identity);

    public static int Min<T>(this List<T> list, Func<T, int> selector)
    {
        int n = list.Count;
        if (n == 0) return NoElements();
        int min = selector(list[0]);
        for (int i = 1; i < n; i++)
        {
            int value = selector(list[i]);
            if (value < min) min = value;
        }
        return min;
    }

    public static float Min(this List<float> list) => Min(list, Identity);

    public static float Min<T>(this List<T> list, Func<T, float> selector)
    {
        int n = list.Count;
        if (n == 0) return NoElements();
        float min = selector(list[0]);
        if (IsNaN(min)) return min;
        for (int i = 1; i < n; i++)
        {
            float value = selector(list[i]);
            if (value < min) min = value;
            else if (IsNaN(value)) return value;
        }
        return min;
    }

    public static string? Min(this List<string> list) => Min(list, Identity);

    public static string? Min<T>(this List<T> list, Func<T, string?> selector)
    {
        string? min = null;
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            string? value = selector(list[i]);
            if (value != null && (min == null || string.CompareOrdinal(value, min) < 0))
                min = value;
        }
        return min;
    }

    public static int Max(this List<int> list) => Max(list, Identity);

    public static int Max<T>(this List<T> list, Func<T, int> selector)
    {
        int n = list.Count;
        if (n == 0) return NoElements();
        int max = selector(list[0]);
        for (int i = 1; i < n; i++)
        {
            int value = selector(list[i]);
            if (value > max) max = value;
        }
        return max;
    }

    public static float Max(this List<float> list) => Max(list, Identity);

    public static float Max<T>(this List<T> list, Func<T, float> selector)
    {
        int n = list.Count;
        if (n == 0) return NoElements();
        float max = selector(list[0]);
        for (int i = 1; i < n; i++)
        {
            float value = selector(list[i]);
            if (value > max || IsNaN(max)) max = value;
        }
        return max;
    }

    public static string? Max(this List<string> list) => Max(list, Identity);

    public static string? Max<T>(this List<T> list, Func<T, string?> selector)
    {
        string? max = null;
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            string? value = selector(list[i]);
            if (value != null && (max == null || string.CompareOrdinal(value, max) > 0))
                max = value;
        }
        return max;
    }

    // MinBy / MaxBy return the element with the minimum / maximum key (among equal keys, the earlier one). The key is computed only once
    // per element, and comparison is the same as OrderBy. An empty sequence faults regardless of the element type (System.Linq returns null for reference-type elements)

    public static T MinBy<T>(this List<T> list, Func<T, int> key) =>
        list[Best(list.Count, i => key(list[i]), (a, b) => a.CompareTo(b) < 0)];

    public static T MinBy<T>(this List<T> list, Func<T, float> key) =>
        list[Best(list.Count, i => key(list[i]), (a, b) => a.CompareTo(b) < 0)];

    public static T MinBy<T>(this List<T> list, Func<T, string?> key) =>
        list[Best(list.Count, i => key(list[i]), (a, b) => Ordinal(a, b) < 0)];

    public static T MaxBy<T>(this List<T> list, Func<T, int> key) =>
        list[Best(list.Count, i => key(list[i]), (a, b) => a.CompareTo(b) > 0)];

    public static T MaxBy<T>(this List<T> list, Func<T, float> key) =>
        list[Best(list.Count, i => key(list[i]), (a, b) => a.CompareTo(b) > 0)];

    public static T MaxBy<T>(this List<T> list, Func<T, string?> key) =>
        list[Best(list.Count, i => key(list[i]), (a, b) => Ordinal(a, b) > 0)];

    // The index, among indexes 0..n-1, of the earliest one whose key wins by better
    private static int Best<TKey>(int n, Func<int, TKey> key, Func<TKey, TKey, bool> better)
    {
        if (n == 0) return NoElements();
        int best = 0;
        TKey bestKey = key(0);
        for (int i = 1; i < n; i++)
        {
            TKey k = key(i);
            if (better(k, bestKey))
            {
                best = i;
                bestKey = k;
            }
        }
        return best;
    }

    // OrderBy / OrderByDescending are stable sorts (elements with equal keys keep their original order). The key is computed only once
    // per element. For float keys NaN is the smallest (float.CompareTo); string keys use ordinal order with null as the smallest

    public static List<T> OrderBy<T>(this List<T> list, Func<T, int> key)
    {
        var keys = Keys(list, key);
        return Reorder(list, keys.Count, (a, b) => Tie(keys[a].CompareTo(keys[b]), a, b));
    }

    public static List<T> OrderBy<T>(this List<T> list, Func<T, float> key)
    {
        var keys = Keys(list, key);
        return Reorder(list, keys.Count, (a, b) => Tie(keys[a].CompareTo(keys[b]), a, b));
    }

    public static List<T> OrderBy<T>(this List<T> list, Func<T, string?> key)
    {
        var keys = Keys(list, key);
        return Reorder(list, keys.Count, (a, b) => Tie(Ordinal(keys[a], keys[b]), a, b));
    }

    public static List<T> OrderByDescending<T>(this List<T> list, Func<T, int> key)
    {
        var keys = Keys(list, key);
        return Reorder(list, keys.Count, (a, b) => Tie(keys[b].CompareTo(keys[a]), a, b));
    }

    public static List<T> OrderByDescending<T>(this List<T> list, Func<T, float> key)
    {
        var keys = Keys(list, key);
        return Reorder(list, keys.Count, (a, b) => Tie(keys[b].CompareTo(keys[a]), a, b));
    }

    public static List<T> OrderByDescending<T>(this List<T> list, Func<T, string?> key)
    {
        var keys = Keys(list, key);
        return Reorder(list, keys.Count, (a, b) => Tie(Ordinal(keys[b], keys[a]), a, b));
    }

    // Dictionary Keys / Values are also accepted as receivers (q.md Q18). The order is insertion order (§18; TinySystem/Collections.cs); they are copied to a List and
    // passed to the List version (Keys / Values in C / VM are List copies too)

    public static List<TKey> Where<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, bool> predicate)
        where TKey : notnull => new List<TKey>(keys).Where(predicate);

    public static List<TResult> Select<TKey, TValue, TResult>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, TResult> selector)
        where TKey : notnull => new List<TKey>(keys).Select(selector);

    public static bool Any<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys)
        where TKey : notnull => new List<TKey>(keys).Any();

    public static bool Any<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, bool> predicate)
        where TKey : notnull => new List<TKey>(keys).Any(predicate);

    public static bool All<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, bool> predicate)
        where TKey : notnull => new List<TKey>(keys).All(predicate);

    public static int Count<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, bool> predicate)
        where TKey : notnull => new List<TKey>(keys).Count(predicate);

    public static TKey FirstOrDefault<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys)
        where TKey : notnull => new List<TKey>(keys).FirstOrDefault();

    public static TKey FirstOrDefault<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, bool> predicate)
        where TKey : notnull => new List<TKey>(keys).FirstOrDefault(predicate);

    public static int Sum<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, int> selector)
        where TKey : notnull => new List<TKey>(keys).Sum(selector);

    public static float Sum<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, float> selector)
        where TKey : notnull => new List<TKey>(keys).Sum(selector);

    public static int Min<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, int> selector)
        where TKey : notnull => new List<TKey>(keys).Min(selector);

    public static int Max<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, int> selector)
        where TKey : notnull => new List<TKey>(keys).Max(selector);

    public static float Min<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, float> selector)
        where TKey : notnull => new List<TKey>(keys).Min(selector);

    public static float Max<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, float> selector)
        where TKey : notnull => new List<TKey>(keys).Max(selector);

    public static string? Min<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, string?> selector)
        where TKey : notnull => new List<TKey>(keys).Min(selector);

    public static string? Max<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, string?> selector)
        where TKey : notnull => new List<TKey>(keys).Max(selector);

    public static TKey MinBy<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, int> key)
        where TKey : notnull => new List<TKey>(keys).MinBy(key);

    public static TKey MaxBy<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, int> key)
        where TKey : notnull => new List<TKey>(keys).MaxBy(key);

    public static TKey MinBy<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, float> key)
        where TKey : notnull => new List<TKey>(keys).MinBy(key);

    public static TKey MaxBy<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, float> key)
        where TKey : notnull => new List<TKey>(keys).MaxBy(key);

    public static TKey MinBy<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, string?> key)
        where TKey : notnull => new List<TKey>(keys).MinBy(key);

    public static TKey MaxBy<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, string?> key)
        where TKey : notnull => new List<TKey>(keys).MaxBy(key);

    public static List<TKey> OrderBy<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, int> key)
        where TKey : notnull => new List<TKey>(keys).OrderBy(key);

    public static List<TKey> OrderByDescending<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, int> key)
        where TKey : notnull => new List<TKey>(keys).OrderByDescending(key);

    public static List<TKey> OrderBy<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, float> key)
        where TKey : notnull => new List<TKey>(keys).OrderBy(key);

    public static List<TKey> OrderByDescending<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, float> key)
        where TKey : notnull => new List<TKey>(keys).OrderByDescending(key);

    public static List<TKey> OrderBy<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, string?> key)
        where TKey : notnull => new List<TKey>(keys).OrderBy(key);

    public static List<TKey> OrderByDescending<TKey, TValue>(this Dictionary<TKey, TValue>.KeyCollection keys, Func<TKey, string?> key)
        where TKey : notnull => new List<TKey>(keys).OrderByDescending(key);

    public static List<TValue> Where<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, bool> predicate)
        where TKey : notnull => new List<TValue>(values).Where(predicate);

    public static List<TResult> Select<TKey, TValue, TResult>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, TResult> selector)
        where TKey : notnull => new List<TValue>(values).Select(selector);

    public static bool Any<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values)
        where TKey : notnull => new List<TValue>(values).Any();

    public static bool Any<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, bool> predicate)
        where TKey : notnull => new List<TValue>(values).Any(predicate);

    public static bool All<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, bool> predicate)
        where TKey : notnull => new List<TValue>(values).All(predicate);

    public static int Count<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, bool> predicate)
        where TKey : notnull => new List<TValue>(values).Count(predicate);

    public static TValue FirstOrDefault<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values)
        where TKey : notnull => new List<TValue>(values).FirstOrDefault();

    public static TValue FirstOrDefault<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, bool> predicate)
        where TKey : notnull => new List<TValue>(values).FirstOrDefault(predicate);

    public static int Sum<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, int> selector)
        where TKey : notnull => new List<TValue>(values).Sum(selector);

    public static float Sum<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, float> selector)
        where TKey : notnull => new List<TValue>(values).Sum(selector);

    public static int Min<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, int> selector)
        where TKey : notnull => new List<TValue>(values).Min(selector);

    public static int Max<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, int> selector)
        where TKey : notnull => new List<TValue>(values).Max(selector);

    public static float Min<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, float> selector)
        where TKey : notnull => new List<TValue>(values).Min(selector);

    public static float Max<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, float> selector)
        where TKey : notnull => new List<TValue>(values).Max(selector);

    public static string? Min<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, string?> selector)
        where TKey : notnull => new List<TValue>(values).Min(selector);

    public static string? Max<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, string?> selector)
        where TKey : notnull => new List<TValue>(values).Max(selector);

    public static TValue MinBy<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, int> key)
        where TKey : notnull => new List<TValue>(values).MinBy(key);

    public static TValue MaxBy<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, int> key)
        where TKey : notnull => new List<TValue>(values).MaxBy(key);

    public static TValue MinBy<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, float> key)
        where TKey : notnull => new List<TValue>(values).MinBy(key);

    public static TValue MaxBy<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, float> key)
        where TKey : notnull => new List<TValue>(values).MaxBy(key);

    public static TValue MinBy<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, string?> key)
        where TKey : notnull => new List<TValue>(values).MinBy(key);

    public static TValue MaxBy<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, string?> key)
        where TKey : notnull => new List<TValue>(values).MaxBy(key);

    public static List<TValue> OrderBy<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, int> key)
        where TKey : notnull => new List<TValue>(values).OrderBy(key);

    public static List<TValue> OrderByDescending<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, int> key)
        where TKey : notnull => new List<TValue>(values).OrderByDescending(key);

    public static List<TValue> OrderBy<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, float> key)
        where TKey : notnull => new List<TValue>(values).OrderBy(key);

    public static List<TValue> OrderByDescending<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, float> key)
        where TKey : notnull => new List<TValue>(values).OrderByDescending(key);

    public static List<TValue> OrderBy<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, string?> key)
        where TKey : notnull => new List<TValue>(values).OrderBy(key);

    public static List<TValue> OrderByDescending<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection values, Func<TValue, string?> key)
        where TKey : notnull => new List<TValue>(values).OrderByDescending(key);

    public static int Min<TKey>(this Dictionary<TKey, int>.ValueCollection values)
        where TKey : notnull => new List<int>(values).Min();

    public static int Max<TKey>(this Dictionary<TKey, int>.ValueCollection values)
        where TKey : notnull => new List<int>(values).Max();

    public static int Sum<TKey>(this Dictionary<TKey, int>.ValueCollection values)
        where TKey : notnull => new List<int>(values).Sum();

    public static float Min<TKey>(this Dictionary<TKey, float>.ValueCollection values)
        where TKey : notnull => new List<float>(values).Min();

    public static float Max<TKey>(this Dictionary<TKey, float>.ValueCollection values)
        where TKey : notnull => new List<float>(values).Max();

    public static float Sum<TKey>(this Dictionary<TKey, float>.ValueCollection values)
        where TKey : notnull => new List<float>(values).Sum();

    public static string? Min<TKey>(this Dictionary<TKey, string>.ValueCollection values)
        where TKey : notnull => new List<string>(values).Min();

    public static string? Max<TKey>(this Dictionary<TKey, string>.ValueCollection values)
        where TKey : notnull => new List<string>(values).Max();

    public static int Min<TValue>(this Dictionary<int, TValue>.KeyCollection keys) =>
        new List<int>(keys).Min();

    public static int Max<TValue>(this Dictionary<int, TValue>.KeyCollection keys) =>
        new List<int>(keys).Max();

    public static float Min<TValue>(this Dictionary<float, TValue>.KeyCollection keys) =>
        new List<float>(keys).Min();

    public static float Max<TValue>(this Dictionary<float, TValue>.KeyCollection keys) =>
        new List<float>(keys).Max();

    public static string? Min<TValue>(this Dictionary<string, TValue>.KeyCollection keys) =>
        new List<string>(keys).Min();

    public static string? Max<TValue>(this Dictionary<string, TValue>.KeyCollection keys) =>
        new List<string>(keys).Max();

    // Min / Max / MinBy / MaxBy of an empty sequence fault, equivalent to InvalidOperationException as in System.Linq.
    // The subset has no throw, so it stops by reading Value of a Nullable with no value
    private static int NoElements()
    {
        int? none = null;
        return none!.Value;
    }

    // Only NaN is not equal to itself (float.IsNaN is not in the subset)
#pragma warning disable CS1718
    private static bool IsNaN(float x) => x != x;
#pragma warning restore CS1718

    private static int Identity(int x) => x;

    private static float Identity(float x) => x;

    private static string? Identity(string? x) => x;

    private static List<TKey> Keys<T, TKey>(List<T> list, Func<T, TKey> key)
    {
        var keys = new List<TKey>();
        int n = list.Count;
        for (int i = 0; i < n; i++) keys.Add(key(list[i]));
        return keys;
    }

    // Sorts indexes 0..n-1 in compare order and returns a List of the elements in that order
    private static List<T> Reorder<T>(List<T> list, int n, Func<int, int, int> compare)
    {
        var order = new List<int>();
        for (int i = 0; i < n; i++) order.Add(i);
        order.Sort((a, b) => compare(a, b));
        var result = new List<T>();
        for (int i = 0; i < n; i++) result.Add(list[order[i]]);
        return result;
    }

    private static int Tie(int compared, int a, int b) =>
        compared != 0 ? compared : a.CompareTo(b);

    private static int Ordinal(string? a, string? b)
    {
        if (string.CompareOrdinal(a, b) < 0) return -1;
        return string.CompareOrdinal(a, b) > 0 ? 1 : 0;
    }
}
