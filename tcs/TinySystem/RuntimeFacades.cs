namespace TinySystem;

// The dotnet-side facade delegates to types in the System namespace.
// Users can verify game logic with dotnet unit tests, and the tcs dotnet differential can verify facade parity.

/// <summary>Same shape as System.Random (instance + `Random.Shared`). The two tcs
/// backends (C / VM) use the same PRNG (xoshiro256**, il-spec §13), so with the same
/// seed, both `new Random(seed)` and `Shared` after `Random.Seed(seed)` produce
/// bit-identical sequences. The dotnet side delegates to System.Random, so its sequences differ
/// (random numbers are outside the dotnet differential).</summary>
public sealed class Random
{
    private readonly global::System.Random _impl;

    public Random() => _impl = new global::System.Random();
    public Random(int seed) => _impl = new global::System.Random(seed);

    /// <summary>Shared instance (equivalent to System.Random.Shared).</summary>
    public static Random Shared { get; private set; } = new();

    /// <summary>Fixes the sequence of the shared instance with a seed.</summary>
    public static void Seed(int seed) => Shared = new Random(seed);

    public int Next() => _impl.Next();
    public int Next(int max) => _impl.Next(max);
    public int Next(int min, int max) => _impl.Next(min, max);
    public float NextFloat() => _impl.NextSingle();
    public float NextSingle() => _impl.NextSingle();
    /// <summary>Integer in [min, max] (both ends inclusive).</summary>
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
    // Same name as System.Math.Ceiling (even when the TinySystem.props alias makes Math
    // TinySystem.Math, Math.Ceiling still works as is)
    public static float Ceiling(float x) => global::System.MathF.Ceiling(x);
    public static float Sqrt(float x) => global::System.MathF.Sqrt(x);
    // Transcendental functions use TcsMath (the same implementation as runtime/math) and return the same results as the tcs C and VM paths,
    // bit for bit
    public static float Sin(float x) => TcsMath.Sin(x);
    public static float Cos(float x) => TcsMath.Cos(x);
    public static float Atan2(float y, float x) => TcsMath.Atan2(y, x);
    // As in tcs C and VM, an exponent of 2 is a multiplication
    public static float Pow(float x, float y) => y == 2.0f ? x * x : TcsMath.Pow(x, y);
    public static float Round(float x) => global::System.MathF.Round(x);
    public static float Round(float x, int digits) =>
        global::System.MathF.Round(x, digits);
    public static int Sign(int x) => System.Math.Sign(x);
    public static int Sign(float x) => global::System.MathF.Sign(x);
    public static float Tan(float x) => TcsMath.Tan(x);
    public static float Log(float x) => TcsMath.Log(x);
    // Special values of MathF.Log(x, newBase), and dedicated functions for bases 2 / 10 (same as tcs2c)
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
    // tcs Add is an assignment (a duplicate key is overwritten).
    // For parity, delegates to indexer assignment rather than the throwing Dictionary.Add
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
