using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// A large non-negative decimal integer (one digit at a time, from the lowest digit). Used for the exact decimal expansion of floats
public sealed class BigDec
{
    public List<int> D = new List<int>();

    public static BigDec Of(int v)
    {
        var b = new BigDec();
        if (v == 0) b.D.Add(0);
        while (v > 0)
        {
            b.D.Add(v % 10);
            v = v / 10;
        }
        return b;
    }

    public BigDec Copy()
    {
        var b = new BigDec();
        foreach (var x in D) b.D.Add(x);
        return b;
    }

    public void MulSmall(int k)
    {
        int carry = 0;
        for (int i = 0; i < D.Count; i++)
        {
            int v = D[i] * k + carry;
            D[i] = v % 10;
            carry = v / 10;
        }
        while (carry > 0)
        {
            D.Add(carry % 10);
            carry = carry / 10;
        }
        Trim();
    }

    // Multiplies by 10^n (appends n zero digits at the bottom)
    public void Shift(int n)
    {
        if (IsZero()) return;
        for (int i = 0; i < n; i++) D.Insert(0, 0);
    }

    public bool IsZero()
    {
        foreach (var x in D)
        {
            if (x != 0) return false;
        }
        return true;
    }

    private void Trim()
    {
        while (D.Count > 1 && D[D.Count - 1] == 0) D.RemoveAt(D.Count - 1);
    }

    public static int Compare(BigDec a, BigDec b)
    {
        int na = a.D.Count;
        int nb = b.D.Count;
        while (na > 1 && a.D[na - 1] == 0) na--;
        while (nb > 1 && b.D[nb - 1] == 0) nb--;
        if (na != nb) return na < nb ? -1 : 1;
        for (int i = na - 1; i >= 0; i--)
        {
            if (a.D[i] != b.D[i]) return a.D[i] < b.D[i] ? -1 : 1;
        }
        return 0;
    }

    // a - b (a >= b)
    public static BigDec Sub(BigDec a, BigDec b)
    {
        var r = new BigDec();
        int borrow = 0;
        for (int i = 0; i < a.D.Count; i++)
        {
            int v = a.D[i] - borrow - (i < b.D.Count ? b.D[i] : 0);
            if (v < 0)
            {
                v += 10;
                borrow = 1;
            }
            else borrow = 0;
            r.D.Add(v);
        }
        r.Trim();
        return r;
    }

    public static BigDec AbsDiff(BigDec a, BigDec b)
    {
        return Compare(a, b) >= 0 ? Sub(a, b) : Sub(b, a);
    }
}

// How IL literals (IlLit) are created, and their display for diagnostics (--dump-il, --trace).
// The text of a float value is determined with integer arithmetic only, so it is the same string whether tcs runs on .NET, C, or wasm
public static class IlLiterals
{
    public static IlLit Null() => new IlLit(IlLitKind.Null, 0, 0f, "", null);

    public static IlLit Bool(bool value) => new IlLit(IlLitKind.Bool, value ? 1 : 0, 0f, "", null);

    public static IlLit Int(int value) => new IlLit(IlLitKind.Int, value, 0f, "", null);

    public static IlLit Float(float value) => new IlLit(IlLitKind.Float, 0, value, "", null);

    public static IlLit Str(string value) => new IlLit(IlLitKind.String, 0, 0f, value, null);

    // Literal for a binder constant
    public static IlLit Of(ConstValue c)
    {
        switch (c.Kind)
        {
            case ConstKind.Bool: return Bool(c.B);
            case ConstKind.String: return Str(c.S);
            case ConstKind.Int: return Int(c.I);
            case ConstKind.Float: return Float(c.F);
        }
        return Null();
    }

    public static bool IsNull(IlExpr? e) => e is IlLit l && l.Kind == IlLitKind.Null;

    public static bool IsString(IlExpr? e) => e is IlLit l && l.Kind == IlLitKind.String;

    // Display for diagnostics (null, true / false, decimal numbers, quoted and escaped strings)
    public static string Display(IlLit lit)
    {
        switch (lit.Kind)
        {
            case IlLitKind.Bool: return lit.I != 0 ? "true" : "false";
            case IlLitKind.Int: return "" + lit.I;
            case IlLitKind.Float: return FormatDoubleOfFloat(lit.F);
            case IlLitKind.String: return QuoteString(lit.S);
        }
        return "null";
    }

    // Quotes the string and escapes backslashes, quotes, newlines, carriage returns, tabs, and control characters
    public static string QuoteString(string value)
    {
        var u = new Utf8Bytes(value);
        var parts = new List<string>();
        parts.Add("\"");
        int run = 0;
        for (int i = 0; i < u.Length; i++)
        {
            int c = u[i];
            string? esc = null;
            if (c == '\\') esc = "\\\\";
            else if (c == '"') esc = "\\\"";
            else if (c == '\n') esc = "\\n";
            else if (c == '\r') esc = "\\r";
            else if (c == '\t') esc = "\\t";
            // Control characters are a fixed two-digit \xXX
            else if (c < 32 || c == 127) esc = "\\x" + Hex2(c);
            if (esc != null)
            {
                if (i > run) parts.Add(u.Slice(run, i - run).ToString());
                parts.Add(esc);
                run = i + 1;
            }
        }
        if (run < u.Length) parts.Add(u.Slice(run, u.Length - run).ToString());
        parts.Add("\"");
        return string.Join("", parts);
    }

    private static string Hex2(int c)
    {
        var digits = "0123456789ABCDEF";
        var d = new Utf8Bytes(digits);
        return d.Slice(c / 16, 1).ToString() + d.Slice(c % 16, 1).ToString();
    }

    // The same word as .NET's float.ToString() if x is NaN or ±∞, null if finite
    private static string? NonFiniteText(float x)
    {
        // Only NaN is not equal to itself (TinyC# has no float.IsNaN)
#pragma warning disable CS1718
        if (x != x) return "NaN";
#pragma warning restore CS1718
        if (x > 3.4028235e38f) return "Infinity";
        if (x < -3.4028235e38f) return "-Infinity";
        return null;
    }

    // Converts a C# float constant to double and writes it as .NET's double.ToString("R") does (the shortest round-trippable form).
    // Appends ".0" if there is neither a decimal point nor an exponent. NaN and infinities are written with the words from NonFiniteText
    public static string FormatDoubleOfFloat(float x)
    {
        var nonFinite = NonFiniteText(x);
        if (nonFinite != null) return nonFinite;
        var text = RoundTripDoubleText(x);
        if (text.Contains(".") || text.Contains("E") || text.Contains("e")) return text;
        return text + ".0";
    }

    // The shortest decimal that round-trips x (a float32 value) as a double value. Same form as .NET's "R" for double
    public static string RoundTripDoubleText(float x)
    {
        if (x == 0)
        {
            // -0 is "-0"
            if (1 / x < 0) return "-0";
            return "0";
        }
        bool negative = x < 0;
        float a = negative ? -x : x;
        // a = m × 2^p (m is an integer of at most 24 bits). Doubling and halving are exact even in float
        int p = 0;
        while (a < 8388608f)
        {
            a = a * 2;
            p--;
        }
        while (a >= 16777216f)
        {
            a = a / 2;
            p++;
        }
        int m = (int)a;
        // Strip the trailing zero bits
        while (m % 2 == 0)
        {
            m = m / 2;
            p++;
        }
        var digits = ShortestDigits(m, p, out int exp10);
        return (negative ? "-" : "") + FormatR(digits, exp10);
    }

    // The shortest significant digits of m × 2^p that round-trip as a double (the first digit is not 0), and the power of 10 of the most significant digit
    private static string ShortestDigits(int m, int p, out int exp10)
    {
        // The double exponent E (2^E <= v < 2^(E+1)) and half the gap to the neighboring double (2^(E-53))
        int bits = 0;
        int mm = m;
        while (mm > 0)
        {
            bits++;
            mm = mm / 2;
        }
        int e = p + bits - 1;
        bool powerOfTwo = m == 1;
        // Compare as integers scaled by 10^F. F is the maximum number of fractional digits of v and the half gap
        int fracV = p < 0 ? -p : 0;
        int fracHalf = e - 53 < 0 ? 53 - e : 0;
        int fracLow = e - 54 < 0 ? 54 - e : 0;
        int scale = fracV;
        if (fracHalf > scale) scale = fracHalf;
        if (fracLow > scale) scale = fracLow;
        var v = Scaled(m, p, scale);
        var halfUp = Scaled(1, e - 53, scale);
        // The double just below a power of 2 has half the gap
        var halfDown = powerOfTwo ? Scaled(1, e - 54, scale) : halfUp;
        // The decimal digits of v (from the top)
        var all = new List<int>();
        for (int i = v.D.Count - 1; i >= 0; i--) all.Add(v.D[i]);
        while (all.Count > 1 && all[0] == 0) all.RemoveAt(0);
        // The power of 10 of the most significant digit
        exp10 = all.Count - 1 - scale;
        for (int n = 1; n <= 17; n++)
        {
            var cand = RoundToDigits(all, n, out int carryExp);
            // The integer cand × 10^(exp10 + carryExp - n + 1) scaled by 10^scale
            int shift = exp10 + carryExp - n + 1 + scale;
            if (shift < 0) continue;
            var c = new BigDec();
            for (int i = cand.Count - 1; i >= 0; i--) c.D.Add(cand[i]);
            c.Shift(shift);
            var diff = BigDec.AbsDiff(c, v);
            var bound = BigDec.Compare(c, v) >= 0 ? halfUp : halfDown;
            if (BigDec.Compare(diff, bound) <= 0)
            {
                exp10 = exp10 + carryExp;
                var parts = new List<string>();
                int last = cand.Count;
                while (last > 1 && cand[last - 1] == 0) last--;
                for (int i = 0; i < last; i++) parts.Add("" + cand[i]);
                return string.Join("", parts);
            }
        }
        var rest = new List<string>();
        foreach (var d in all) rest.Add("" + d);
        return string.Join("", rest);
    }

    // m × 2^p × 10^scale (it is an integer)
    private static BigDec Scaled(int m, int p, int scale)
    {
        var b = BigDec.Of(m);
        if (p >= 0)
        {
            for (int i = 0; i < p; i++) b.MulSmall(2);
            b.Shift(scale);
            return b;
        }
        // 2^-q × 10^scale = 5^q × 10^(scale - q)
        int q = -p;
        for (int i = 0; i < q; i++) b.MulSmall(5);
        b.Shift(scale - q);
        return b;
    }

    // Rounds the digits all (from the top) to n digits (round to nearest, ties to even). If a carry adds a digit, carryExp = 1
    private static List<int> RoundToDigits(List<int> all, int n, out int carryExp)
    {
        carryExp = 0;
        var r = new List<int>();
        for (int i = 0; i < n; i++) r.Add(i < all.Count ? all[i] : 0);
        if (n >= all.Count) return r;
        int next = all[n];
        bool restZero = true;
        for (int i = n + 1; i < all.Count; i++)
        {
            if (all[i] != 0) restZero = false;
        }
        bool up = next > 5 || (next == 5 && (!restZero || r[n - 1] % 2 == 1));
        if (!up) return r;
        int k = n - 1;
        while (k >= 0)
        {
            r[k]++;
            if (r[k] < 10) break;
            r[k] = 0;
            k--;
        }
        if (k < 0)
        {
            r.Insert(0, 1);
            r.RemoveAt(r.Count - 1);
            carryExp = 1;
        }
        return r;
    }

    // .NET's "R" for double: exponent form if the exponent is less than -4 or greater than 14 (1.5E-05, 1E+15)
    private static string FormatR(string digits, int exp10)
    {
        int n = Strings.ByteLength(digits);
        if (exp10 > 14 || exp10 < -4)
        {
            var mant = n == 1 ? digits : Strings.Prefix(digits, 1) + "." + Strings.From(digits, 1);
            var sign = exp10 < 0 ? "-" : "+";
            int ae = exp10 < 0 ? -exp10 : exp10;
            var es = ae < 10 ? "0" + ae : "" + ae;
            return mant + "E" + sign + es;
        }
        if (exp10 < 0)
        {
            var zeros = new List<string>();
            for (int i = 0; i < -exp10 - 1; i++) zeros.Add("0");
            return "0." + string.Join("", zeros) + digits;
        }
        if (n <= exp10 + 1)
        {
            var zeros = new List<string>();
            for (int i = 0; i < exp10 + 1 - n; i++) zeros.Add("0");
            return digits + string.Join("", zeros);
        }
        return Strings.Prefix(digits, exp10 + 1) + "." + Strings.From(digits, exp10 + 1);
    }
}
