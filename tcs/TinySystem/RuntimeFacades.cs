namespace TinySystem;

// dotnet 側 facade は System 相当へ委譲する (CLAUDE.md「dotnet側では System
// 名前空間の型に委譲」)。ユーザーがゲームロジックを dotnet 単体テストで
// 検証でき、tcs の dotnet differential が facade parity を検証できる。
// Lua 側実装は runtime/tinysystem.lua が正本。

/// <summary>System.Random と同じ形 (instance + `Random.Shared`)。tcs の
/// 2 backend (Lua / C) は同じ PRNG (Lua 5.5 の xoshiro256**) なので、同じ
/// seed なら `new Random(seed)` も `Random.Seed(seed)` 後の `Shared` も列が
/// bit 一致する。dotnet 側は System.Random に委譲するので列は異なる
/// (乱数は dotnet differential の対象外)。</summary>
public sealed class Random
{
    private readonly global::System.Random _impl;

    public Random() => _impl = new global::System.Random();
    public Random(int seed) => _impl = new global::System.Random(seed);

    /// <summary>共有 instance (System.Random.Shared 相当)。</summary>
    public static Random Shared { get; private set; } = new();

    /// <summary>共有 instance の列を seed で固定する (Lua 側は
    /// math.randomseed)。</summary>
    public static void Seed(int seed) => Shared = new Random(seed);

    public int Next() => _impl.Next();
    public int Next(int max) => _impl.Next(max);
    public int Next(int min, int max) => _impl.Next(min, max);
    public float NextFloat() => _impl.NextSingle();
    public float NextSingle() => _impl.NextSingle();
    /// <summary>[min, max] の整数 (両端含む。Lua の math.random(m, n) と同じ)。</summary>
    public int Range(int min, int max) => (int)_impl.NextInt64(min, (long)max + 1);
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
    // System.Math.Ceiling と同じ名前 (TinySystem.props の alias で Math を
    // TinySystem.Math にしても、Math.Ceiling がそのまま通る)
    public static float Ceiling(float x) => global::System.MathF.Ceiling(x);
    public static float Sqrt(float x) => global::System.MathF.Sqrt(x);
    // 超越関数は TcsMath (runtime/math と同じ実装) で、tcs2c / Lua の経路と
    // bit 単位で同じ結果を返す
    public static float Sin(float x) => TcsMath.Sin(x);
    public static float Cos(float x) => TcsMath.Cos(x);
    public static float Atan2(float y, float x) => TcsMath.Atan2(y, x);
    // Lua の ^ と tcs2c と同じく、指数 2 は乗算
    public static float Pow(float x, float y) => y == 2.0f ? x * x : TcsMath.Pow(x, y);
    public static float Round(float x) => global::System.MathF.Round(x);
    public static float Round(float x, int digits) =>
        global::System.MathF.Round(x, digits);
    public static int Sign(int x) => System.Math.Sign(x);
    public static int Sign(float x) => global::System.MathF.Sign(x);
    public static float Tan(float x) => TcsMath.Tan(x);
    public static float Log(float x) => TcsMath.Log(x);
    // MathF.Log(x, newBase) の特殊値と、底 2 / 10 の専用の関数 (tcs2c と同じ)
    public static float Log(float x, float newBase)
    {
        if (newBase == 1.0f || (x != 1.0f && (newBase == 0.0f || newBase == float.PositiveInfinity)))
            return float.NaN;
        if (newBase == 2.0f) return TcsMath.Log2(x);
        if (newBase == 10.0f) return TcsMath.Log10(x);
        return TcsMath.Log(x) / TcsMath.Log(newBase);
    }
    public static float Exp(float x) => TcsMath.Exp(x);
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
}

public static class Dict
{
    // Lua runtime の Add は table 代入 (duplicate key は上書き)。
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
