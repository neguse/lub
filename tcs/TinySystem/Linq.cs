// project の nullable の設定に依らず同じ注釈で読む (.NET の project は TinySystem.props で取り込む)
#nullable enable
namespace TinySystem.Linq;

// using を namespace の内側に置き、user が global に定義した Func / List より System を先に引く
using System;
using System.Collections.Generic;

/// <summary>
/// <c>List&lt;T&gt;</c> の LINQ。System.Linq と同じ名前の演算を即時評価で持ち、列を返す演算は新しい
/// <c>List&lt;T&gt;</c> を返す。tcs はこのファイルを user のコードと一緒に Lua / C に変換し、.NET は
/// そのまま実行するので、3 つの経路が同じ実装で動く (q.md Q18)。namespace を TinySystem と分けるのは、
/// <c>using System;</c> と並べても Math / String / Random が曖昧にならないようにするため。
/// <para>共通の契約: callback は各要素にちょうど 1 回、先頭から順に呼ぶ。走査するのは呼び出した
/// 時点の Count までなので、callback が受け手を縮めると範囲外の添字を読む。空の列の
/// Min / Max (int / float) は fault する。</para>
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

    /// <summary>空なら default(T)。</summary>
    public static T FirstOrDefault<T>(this List<T> list) =>
        list.Count > 0 ? list[0] : default!;

    /// <summary>一致する要素が無ければ default(T)。</summary>
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

    /// <summary>途中の和が int の範囲を超えれば fault (checked)。</summary>
    public static int Sum(this List<int> list)
    {
        int sum = 0;
        int n = list.Count;
        for (int i = 0; i < n; i++) sum = checked(sum + list[i]);
        return sum;
    }

    /// <summary>途中の和が int の範囲を超えれば fault (checked)。</summary>
    public static int Sum<T>(this List<T> list, Func<T, int> selector)
    {
        int sum = 0;
        int n = list.Count;
        for (int i = 0; i < n; i++) sum = checked(sum + selector(list[i]));
        return sum;
    }

    /// <summary>float のまま先頭から足す (System.Linq は double で足してから float にする)。</summary>
    public static float Sum(this List<float> list)
    {
        float sum = 0f;
        int n = list.Count;
        for (int i = 0; i < n; i++) sum += list[i];
        return sum;
    }

    /// <summary>float のまま先頭から足す (System.Linq は double で足してから float にする)。</summary>
    public static float Sum<T>(this List<T> list, Func<T, float> selector)
    {
        float sum = 0f;
        int n = list.Count;
        for (int i = 0; i < n; i++) sum += selector(list[i]);
        return sum;
    }

    // Min / Max は System.Linq と同じ規則: 等しい値は先に出た方を残す。float の NaN は最小の値で、
    // Min は NaN を見た時点でそれを返し (残りの selector は呼ばない)、Max は NaN 以外が 1 つでも
    // あればその最大値。string は ordinal の順で、null を飛ばし、null しか無ければ null。
    // 空の列は int / float が fault (NoElements)、string が null

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

    // MinBy / MaxBy は key の最小 / 最大の要素 (key の等しい要素は先に出た方)。key は各要素に 1 回
    // だけ求め、比べ方は OrderBy と同じ。空の列は fault (System.Linq は参照型の要素なら null を返すが、
    // Lua は型を消すので値型と区別できない)

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

    // 添字 0..n-1 のうち key が better で最も先に勝つものの添字
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

    // OrderBy / OrderByDescending は安定 sort (key の等しい要素は元の順)。key は各要素に 1 回だけ
    // 求める。float の key は NaN が最小 (float.CompareTo)、string の key は ordinal で null が最小

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

    // Dictionary の Keys / Values も受け手にする (q.md Q18)。順序は未規定 (§18) で、List に写して
    // List の版に渡す (Lua / C の Keys / Values はもともと List の写し)

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

    // 空の列の Min / Max / MinBy / MaxBy は System.Linq と同じく InvalidOperationException 相当の fault。
    // サブセットに throw は無いので、値の無い Nullable の Value を読んで止める
    private static int NoElements()
    {
        int? none = null;
        return none!.Value;
    }

    // NaN だけが自分と等しくない (float.IsNaN はサブセットに無い)
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

    // 添字 0..n-1 を compare の順に並べ、その順の要素の List を返す
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
