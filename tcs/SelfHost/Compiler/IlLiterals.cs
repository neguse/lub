using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// 10 進の大きな非負の整数 (下の桁から 1 桁ずつ)。float の正確な 10 進展開に使う
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

    // 10^n を掛ける (下に 0 を n 桁足す)
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

// IL の literal のテキスト (IlLit.LuaText の形。C と VM の backend も同じ形を読む)。
// float の値の表記は参照実装の LuaEmitter の FormatLuaNumber と FloatLit と同じで、
// 整数の演算だけで決めるので、Lua (LUA_32BITS を含む)、C、.NET のどこでも同じ文字列になる
public static class IlLiterals
{
    // 文字列の literal (引用符で囲み、backslash、引用符、改行、復帰、tab と制御文字を escape する)
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
            // 制御文字は 2 桁固定の \xXX (Lua は \x を 2 桁で読むので後の文字と混ざらない)
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

    // C# の float の定数を double にして、.NET の double.ToString("R") (往復できる最短の表記) で書く。
    // 小数点も指数も無ければ ".0" を付ける。NaN と無限は Lua の式
    public static string FormatDoubleOfFloat(float x)
    {
        // NaN だけが自分と等しくない (float.IsNaN は TinyC# に無い)
#pragma warning disable CS1718
        if (x != x) return "(0/0)";
#pragma warning restore CS1718
        if (x > 3.4028235e38f) return "math.huge";
        if (x < -3.4028235e38f) return "(-math.huge)";
        var text = RoundTripDoubleText(x);
        if (text.Contains(".") || text.Contains("E") || text.Contains("e")) return text;
        return text + ".0";
    }

    // x (float32 の値) を double の値として往復できる最短の 10 進。.NET の double の "R" と同じ形
    public static string RoundTripDoubleText(float x)
    {
        if (x == 0)
        {
            // -0 は "-0"
            if (1 / x < 0) return "-0";
            return "0";
        }
        bool negative = x < 0;
        float a = negative ? -x : x;
        // a = m × 2^p (m は 24 bit 以下の整数)。2 倍と半分は float でも正確
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
        // 下の 0 の bit を外す
        while (m % 2 == 0)
        {
            m = m / 2;
            p++;
        }
        var digits = ShortestDigits(m, p, out int exp10);
        return (negative ? "-" : "") + FormatR(digits, exp10);
    }

    // m × 2^p の、double として往復できる最短の有効数字 (先頭は 0 でない) と、最上位の桁の 10 の冪
    private static string ShortestDigits(int m, int p, out int exp10)
    {
        // double の指数 E (2^E <= v < 2^(E+1)) と、隣の double との間の半分 (2^(E-53))
        int bits = 0;
        int mm = m;
        while (mm > 0)
        {
            bits++;
            mm = mm / 2;
        }
        int e = p + bits - 1;
        bool powerOfTwo = m == 1;
        // 値を 10^F 倍した整数で比べる。F は v と半分の間の小数の桁の最大
        int fracV = p < 0 ? -p : 0;
        int fracHalf = e - 53 < 0 ? 53 - e : 0;
        int fracLow = e - 54 < 0 ? 54 - e : 0;
        int scale = fracV;
        if (fracHalf > scale) scale = fracHalf;
        if (fracLow > scale) scale = fracLow;
        var v = Scaled(m, p, scale);
        var halfUp = Scaled(1, e - 53, scale);
        // 2 の冪のすぐ下の double は間隔が半分
        var halfDown = powerOfTwo ? Scaled(1, e - 54, scale) : halfUp;
        // v の 10 進の桁 (上から)
        var all = new List<int>();
        for (int i = v.D.Count - 1; i >= 0; i--) all.Add(v.D[i]);
        while (all.Count > 1 && all[0] == 0) all.RemoveAt(0);
        // 最上位の桁の 10 の冪
        exp10 = all.Count - 1 - scale;
        for (int n = 1; n <= 17; n++)
        {
            var cand = RoundToDigits(all, n, out int carryExp);
            // cand × 10^(exp10 + carryExp - n + 1) を 10^scale 倍した整数
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

    // m × 2^p × 10^scale (整数になる)
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

    // 上からの桁 all を n 桁に丸める (四捨五入、ちょうど半分は偶数へ)。繰り上がりで桁が増えたら carryExp = 1
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

    // .NET の double の "R": 指数が -4 より小さいか 14 より大きければ指数の形 (1.5E-05、1E+15)
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
