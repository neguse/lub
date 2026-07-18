namespace TinySystem;

// dotnet 側 facade は System 相当へ委譲する (CLAUDE.md「dotnet側では System
// 名前空間の型に委譲」)。ユーザーがゲームロジックを dotnet 単体テストで
// 検証でき、tcs の dotnet differential が facade parity を検証できる。
// Lua 側実装は runtime/tinysystem.lua が正本。

public static class Random
{
    private static readonly global::System.Random Shared = new();

    public static int Next() => Shared.Next();
    public static int Next(int max) => Shared.Next(max);
    public static int Next(int min, int max) => Shared.Next(min, max);
    public static float NextFloat() => Shared.NextSingle();
    public static int Range(int min, int max) => Shared.Next(min, max);
}

public static class Math
{
    public const float PI = global::System.MathF.PI;

    public static int Min(int a, int b) => System.Math.Min(a, b);
    public static float Min(float a, float b) => global::System.MathF.Min(a, b);
    public static int Max(int a, int b) => System.Math.Max(a, b);
    public static float Max(float a, float b) => global::System.MathF.Max(a, b);
    public static int Clamp(int value, int min, int max) =>
        System.Math.Clamp(value, min, max);
    public static float Clamp(float value, float min, float max) =>
        global::System.Math.Clamp(value, min, max);
    public static int Abs(int x) => System.Math.Abs(x);
    public static float Abs(float x) => global::System.MathF.Abs(x);
    public static float Floor(float x) => global::System.MathF.Floor(x);
    public static float Ceil(float x) => global::System.MathF.Ceiling(x);
    public static float Sqrt(float x) => global::System.MathF.Sqrt(x);
    public static float Sin(float x) => global::System.MathF.Sin(x);
    public static float Cos(float x) => global::System.MathF.Cos(x);
    public static float Atan2(float y, float x) => global::System.MathF.Atan2(y, x);
    public static float Pow(float x, float y) => global::System.MathF.Pow(x, y);
    public static float Round(float x) => global::System.MathF.Round(x);
    public static float Round(float x, int digits) =>
        global::System.MathF.Round(x, digits);
    public static int Sign(int x) => System.Math.Sign(x);
    public static int Sign(float x) => global::System.MathF.Sign(x);
    public static float Tan(float x) => global::System.MathF.Tan(x);
    public static float Log(float x) => global::System.MathF.Log(x);
    public static float Log(float x, float newBase) =>
        global::System.MathF.Log(x, newBase);
    public static float Exp(float x) => global::System.MathF.Exp(x);
}

public static class String
{
    public static bool Contains(string str, string substr) =>
        str.Contains(substr, global::System.StringComparison.Ordinal);
    public static bool IsNullOrEmpty(string? str) =>
        global::System.String.IsNullOrEmpty(str);
    public static int IndexOf(string str, string value) =>
        str.IndexOf(value, global::System.StringComparison.Ordinal);
    public static int IndexOf(string str, string value, int startIndex) =>
        str.IndexOf(value, startIndex, global::System.StringComparison.Ordinal);
    public static string Join(string sep,
        global::System.Collections.Generic.List<string> values) =>
        global::System.String.Join(sep, values);
    public static string Join(string sep, params string[] values) =>
        global::System.String.Join(sep, values);
    public static string Replace(string str, string old, string replacement) =>
        str.Replace(old, replacement, global::System.StringComparison.Ordinal);
    public static bool StartsWith(string str, string prefix) =>
        str.StartsWith(prefix, global::System.StringComparison.Ordinal);
    public static bool EndsWith(string str, string suffix) =>
        str.EndsWith(suffix, global::System.StringComparison.Ordinal);
    public static string Trim(string str) => str.Trim();
    public static string Substring(string str, int start) =>
        str.Substring(start);
    public static string Substring(string str, int start, int length) =>
        str.Substring(start, length);
    public static global::System.Collections.Generic.List<string> Split(
        string str, string sep) =>
        [.. str.Split(sep)];
}

public static class List
{
    public static void Add<T>(
        global::System.Collections.Generic.List<T> list, T item) =>
        list.Add(item);
    public static bool Remove<T>(
        global::System.Collections.Generic.List<T> list, T item) =>
        list.Remove(item);
    public static void RemoveAt<T>(
        global::System.Collections.Generic.List<T> list, int index) =>
        list.RemoveAt(index);
    public static int Count<T>(
        global::System.Collections.Generic.List<T> list) => list.Count;
    public static bool Contains<T>(
        global::System.Collections.Generic.List<T> list, T item) =>
        list.Contains(item);
    public static int IndexOf<T>(
        global::System.Collections.Generic.List<T> list, T item) =>
        list.IndexOf(item);
    public static void Sort<T>(
        global::System.Collections.Generic.List<T> list) => list.Sort();
    public static void Sort<T>(
        global::System.Collections.Generic.List<T> list,
        global::System.Comparison<T> comparison) => list.Sort(comparison);
    public static global::System.Collections.Generic.List<T> Where<T>(
        global::System.Collections.Generic.List<T> list,
        global::System.Func<T, bool> predicate) =>
        [.. global::System.Linq.Enumerable.Where(list, predicate)];
    public static global::System.Collections.Generic.List<TResult>
        Select<T, TResult>(
            global::System.Collections.Generic.List<T> list,
            global::System.Func<T, TResult> selector) =>
        [.. global::System.Linq.Enumerable.Select(list, selector)];
    public static bool Any<T>(
        global::System.Collections.Generic.List<T> list) => list.Count > 0;
    public static bool Any<T>(
        global::System.Collections.Generic.List<T> list,
        global::System.Func<T, bool> predicate) =>
        global::System.Linq.Enumerable.Any(list, predicate);
    public static bool All<T>(
        global::System.Collections.Generic.List<T> list,
        global::System.Func<T, bool> predicate) =>
        global::System.Linq.Enumerable.All(list, predicate);
    public static int Count<T>(
        global::System.Collections.Generic.List<T> list,
        global::System.Func<T, bool> predicate) =>
        global::System.Linq.Enumerable.Count(list, predicate);
    public static T First<T>(
        global::System.Collections.Generic.List<T> list) => list[0];
    public static T First<T>(
        global::System.Collections.Generic.List<T> list,
        global::System.Func<T, bool> predicate) =>
        global::System.Linq.Enumerable.First(list, predicate);
    public static T? FirstOrDefault<T>(
        global::System.Collections.Generic.List<T> list) =>
        global::System.Linq.Enumerable.FirstOrDefault(list);
    public static T? FirstOrDefault<T>(
        global::System.Collections.Generic.List<T> list,
        global::System.Func<T, bool> predicate) =>
        global::System.Linq.Enumerable.FirstOrDefault(list, predicate);
    public static global::System.Collections.Generic.List<T> OrderBy<T, TKey>(
        global::System.Collections.Generic.List<T> list,
        global::System.Func<T, TKey> keySelector) =>
        [.. global::System.Linq.Enumerable.OrderBy(list, keySelector)];
    public static global::System.Collections.Generic.List<T>
        OrderByDescending<T, TKey>(
            global::System.Collections.Generic.List<T> list,
            global::System.Func<T, TKey> keySelector) =>
        [.. global::System.Linq.Enumerable.OrderByDescending(list, keySelector)];
    public static global::System.Collections.Generic.List<T> Take<T>(
        global::System.Collections.Generic.List<T> list, int count) =>
        [.. global::System.Linq.Enumerable.Take(list, count)];
    public static global::System.Collections.Generic.List<T> Skip<T>(
        global::System.Collections.Generic.List<T> list, int count) =>
        [.. global::System.Linq.Enumerable.Skip(list, count)];
    public static T Last<T>(
        global::System.Collections.Generic.List<T> list) => list[^1];
    public static T Last<T>(
        global::System.Collections.Generic.List<T> list,
        global::System.Func<T, bool> predicate) =>
        global::System.Linq.Enumerable.Last(list, predicate);
    public static T? LastOrDefault<T>(
        global::System.Collections.Generic.List<T> list) =>
        global::System.Linq.Enumerable.LastOrDefault(list);
    public static T? LastOrDefault<T>(
        global::System.Collections.Generic.List<T> list,
        global::System.Func<T, bool> predicate) =>
        global::System.Linq.Enumerable.LastOrDefault(list, predicate);
    public static T Min<T>(
        global::System.Collections.Generic.List<T> list) =>
        global::System.Linq.Enumerable.Min(list)!;
    public static T Max<T>(
        global::System.Collections.Generic.List<T> list) =>
        global::System.Linq.Enumerable.Max(list)!;
    public static int Sum(
        global::System.Collections.Generic.List<int> list) =>
        global::System.Linq.Enumerable.Sum(list);
    public static float Sum(
        global::System.Collections.Generic.List<float> list) =>
        global::System.Linq.Enumerable.Sum(list);
    public static global::System.Collections.Generic.List<T> ToList<T>(
        global::System.Collections.Generic.List<T> list) => [.. list];
    public static global::System.Collections.Generic.Dictionary<TKey, T>
        ToDictionary<T, TKey>(
            global::System.Collections.Generic.List<T> list,
            global::System.Func<T, TKey> keySelector)
        where TKey : notnull =>
        global::System.Linq.Enumerable.ToDictionary(list, keySelector,
            item => item);
    public static global::System.Collections.Generic.Dictionary<TKey, TValue>
        ToDictionary<T, TKey, TValue>(
            global::System.Collections.Generic.List<T> list,
            global::System.Func<T, TKey> keySelector,
            global::System.Func<T, TValue> valueSelector)
        where TKey : notnull =>
        global::System.Linq.Enumerable.ToDictionary(list, keySelector,
            valueSelector);
}

public static class Dict
{
    // Lua runtime の Add は table 代入 (duplicate key は上書き、T153 判断)。
    // parity のため Dictionary.Add の throw ではなく indexer 代入へ委譲する
    public static void Add<TKey, TValue>(
        global::System.Collections.Generic.Dictionary<TKey, TValue> dict,
        TKey key, TValue value)
        where TKey : notnull => dict[key] = value;
    public static bool Remove<TKey, TValue>(
        global::System.Collections.Generic.Dictionary<TKey, TValue> dict,
        TKey key)
        where TKey : notnull => dict.Remove(key);
    public static bool ContainsKey<TKey, TValue>(
        global::System.Collections.Generic.Dictionary<TKey, TValue> dict,
        TKey key)
        where TKey : notnull => dict.ContainsKey(key);
    public static int Count<TKey, TValue>(
        global::System.Collections.Generic.Dictionary<TKey, TValue> dict)
        where TKey : notnull => dict.Count;
    public static global::System.Collections.Generic.List<TKey>
        Keys<TKey, TValue>(
            global::System.Collections.Generic.Dictionary<TKey, TValue> dict)
        where TKey : notnull => [.. dict.Keys];
    public static global::System.Collections.Generic.List<TValue>
        Values<TKey, TValue>(
            global::System.Collections.Generic.Dictionary<TKey, TValue> dict)
        where TKey : notnull => [.. dict.Values];
}
