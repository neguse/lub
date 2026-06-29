namespace TinySystem;

public static class Random
{
    public static int Next() => default;
    public static int Next(int max) => default;
    public static int Next(int min, int max) => default;
    public static float NextFloat() => default;
    public static int Range(int min, int max) => default;
}

public static class Math
{
    public const double PI = System.Math.PI;

    public static int Min(int a, int b) => default;
    public static double Min(double a, double b) => default;
    public static int Max(int a, int b) => default;
    public static double Max(double a, double b) => default;
    public static int Clamp(int value, int min, int max) => default;
    public static double Clamp(double value, double min, double max) => default;
    public static int Abs(int x) => default;
    public static double Abs(double x) => default;
    public static double Floor(double x) => default;
    public static double Ceil(double x) => default;
    public static double Sqrt(double x) => default;
    public static double Sin(double x) => default;
    public static double Cos(double x) => default;
    public static double Atan2(double y, double x) => default;
    public static double Pow(double x, double y) => default;
}

public static class String
{
    public static bool Contains(string str, string substr) => default;
    public static int IndexOf(string str, string value) => default;
    public static int IndexOf(string str, string value, int startIndex) => default;
    public static string Join(string sep, global::System.Collections.Generic.List<string> values) => "";
    public static string Join(string sep, params string[] values) => "";
    public static string Replace(string str, string old, string replacement) => "";
    public static bool StartsWith(string str, string prefix) => default;
    public static bool EndsWith(string str, string suffix) => default;
    public static string Trim(string str) => "";
    public static string Substring(string str, int start) => "";
    public static string Substring(string str, int start, int length) => "";
    public static global::System.Collections.Generic.List<string> Split(string str, string sep) => [];
}

public static class List
{
    public static void Add<T>(global::System.Collections.Generic.List<T> list, T item) { }
    public static bool Remove<T>(global::System.Collections.Generic.List<T> list, T item) => default;
    public static void RemoveAt<T>(global::System.Collections.Generic.List<T> list, int index) { }
    public static int Count<T>(global::System.Collections.Generic.List<T> list) => default;
    public static bool Contains<T>(global::System.Collections.Generic.List<T> list, T item) => default;
    public static int IndexOf<T>(global::System.Collections.Generic.List<T> list, T item) => default;
    public static void Sort<T>(global::System.Collections.Generic.List<T> list) { }
    public static void Sort<T>(
        global::System.Collections.Generic.List<T> list, global::System.Comparison<T> comparison) { }
    public static global::System.Collections.Generic.List<T> Where<T>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, bool> predicate) => [];
    public static global::System.Collections.Generic.List<TResult> Select<T, TResult>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, TResult> selector) => [];
    public static bool Any<T>(global::System.Collections.Generic.List<T> list) => default;
    public static bool Any<T>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, bool> predicate) => default;
    public static bool All<T>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, bool> predicate) => default;
    public static int Count<T>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, bool> predicate) => default;
    public static T First<T>(global::System.Collections.Generic.List<T> list) => default!;
    public static T First<T>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, bool> predicate) => default!;
    public static T? FirstOrDefault<T>(global::System.Collections.Generic.List<T> list) => default;
    public static T? FirstOrDefault<T>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, bool> predicate) => default;
    public static global::System.Collections.Generic.List<T> OrderBy<T, TKey>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, TKey> keySelector) => [];
    public static global::System.Collections.Generic.List<T> OrderByDescending<T, TKey>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, TKey> keySelector) => [];
    public static global::System.Collections.Generic.List<T> Take<T>(
        global::System.Collections.Generic.List<T> list, int count) => [];
    public static global::System.Collections.Generic.List<T> Skip<T>(
        global::System.Collections.Generic.List<T> list, int count) => [];
    public static T Last<T>(global::System.Collections.Generic.List<T> list) => default!;
    public static T Last<T>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, bool> predicate) => default!;
    public static T? LastOrDefault<T>(global::System.Collections.Generic.List<T> list) => default;
    public static T? LastOrDefault<T>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, bool> predicate) => default;
    public static T Min<T>(global::System.Collections.Generic.List<T> list) => default!;
    public static T Max<T>(global::System.Collections.Generic.List<T> list) => default!;
    public static int Sum(global::System.Collections.Generic.List<int> list) => default;
    public static double Sum(global::System.Collections.Generic.List<double> list) => default;
    public static global::System.Collections.Generic.List<T> ToList<T>(
        global::System.Collections.Generic.List<T> list) => [];
    public static global::System.Collections.Generic.Dictionary<TKey, T> ToDictionary<T, TKey>(
        global::System.Collections.Generic.List<T> list, global::System.Func<T, TKey> keySelector)
        where TKey : notnull => [];
    public static global::System.Collections.Generic.Dictionary<TKey, TValue> ToDictionary<T, TKey, TValue>(
        global::System.Collections.Generic.List<T> list,
        global::System.Func<T, TKey> keySelector,
        global::System.Func<T, TValue> valueSelector)
        where TKey : notnull => [];
}

public static class Dict
{
    public static void Add<TKey, TValue>(
        global::System.Collections.Generic.Dictionary<TKey, TValue> dict, TKey key, TValue value)
        where TKey : notnull { }
    public static bool Remove<TKey, TValue>(
        global::System.Collections.Generic.Dictionary<TKey, TValue> dict, TKey key)
        where TKey : notnull => default;
    public static bool ContainsKey<TKey, TValue>(
        global::System.Collections.Generic.Dictionary<TKey, TValue> dict, TKey key)
        where TKey : notnull => default;
    public static int Count<TKey, TValue>(
        global::System.Collections.Generic.Dictionary<TKey, TValue> dict)
        where TKey : notnull => default;
    public static global::System.Collections.Generic.List<TKey> Keys<TKey, TValue>(
        global::System.Collections.Generic.Dictionary<TKey, TValue> dict)
        where TKey : notnull => [];
    public static global::System.Collections.Generic.List<TValue> Values<TKey, TValue>(
        global::System.Collections.Generic.Dictionary<TKey, TValue> dict)
        where TKey : notnull => [];
}
