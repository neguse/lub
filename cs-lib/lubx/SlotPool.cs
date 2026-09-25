// 実装ライブラリ lubx の SlotPool。
// tcs はユーザー定義ジェネリクスを持たないので、pool は slot 番号 (int) だけを
// 配り、中身は利用側が slot 番号を添字にした List に置く。
// 生存フラグ・世代・空き slot の stack はどれも List で持つ。new T[n] は Lua
// では長さを持たない空 table になり、List.Clear() は Lua でクロージャを作る
// ので、どちらも使わない。

using System.Collections.Generic;

/// <summary>
/// slot を使い回すプール。Alloc() は空いた slot 番号 (最後に空いたものから)
/// を返し、無ければ末尾に足す。Free() は生存フラグを下ろすだけで、要素を
/// 詰めたりコピーしたりしない。そのため 0〜Capacity-1 を IsAlive で絞って
/// 回すループの途中で Free してよい。Generation(i) は slot i が Alloc される
/// たびに 1 増えるので、slot 番号と一緒に覚えておけば、消えて別の用途に
/// 使い回された slot を古い番号で触っていないか確かめられる。
/// 中身は slot 番号を添字にした List に置き、初めて使う slot のときだけ
/// オブジェクトを作って、使い回しでは値を入れ直す:
/// <code>
/// var pool = new SlotPool();
/// var bullets = new List&lt;Bullet&gt;();
///
/// // 出す
/// int i = pool.Alloc();
/// if (i == bullets.Count)
///     bullets.Add(new Bullet());
/// bullets[i].Reset(x, y);
///
/// // 毎 tick 動かす (回しながら消してよい)
/// for (int j = 0; j &lt; pool.Capacity; j++)
/// {
///     if (!pool.IsAlive(j))
///         continue;
///     var b = bullets[j];
///     b.Y = b.Y - b.Speed * dt;
///     if (b.Y &lt; 0)
///         pool.Free(j);
/// }
/// </code>
/// ループの途中で Alloc した slot は、番号によって同じループで回ることも
/// 回らないこともある。
/// </summary>
public class SlotPool
{
    /// <summary>これまでに作った slot の数。slot 番号は 0〜Capacity-1。</summary>
    public int Capacity { get; private set; }

    /// <summary>生きている slot の数。</summary>
    public int Live { get; private set; }

    private List<bool> alive = new List<bool>();
    private List<int> generations = new List<int>();
    private List<int> freeSlots = new List<int>();

    /// <summary>slot を 1 つ確保して番号を返す。</summary>
    public int Alloc()
    {
        int n = freeSlots.Count;
        if (n > 0)
        {
            int i = freeSlots[n - 1];
            freeSlots.RemoveAt(n - 1);
            alive[i] = true;
            generations[i] = generations[i] + 1;
            Live = Live + 1;
            return i;
        }
        alive.Add(true);
        generations.Add(1);
        Capacity = Capacity + 1;
        Live = Live + 1;
        return Capacity - 1;
    }

    /// <summary>slot を空ける。生きていない番号や範囲外の番号では何もしない。</summary>
    public void Free(int i)
    {
        if (!IsAlive(i))
        {
            return;
        }
        alive[i] = false;
        freeSlots.Add(i);
        Live = Live - 1;
    }

    public bool IsAlive(int i)
    {
        if (i < 0 || i >= Capacity)
        {
            return false;
        }
        return alive[i];
    }

    /// <summary>slot i がこれまでに Alloc された回数 (1 から)。範囲外は 0。</summary>
    public int Generation(int i)
    {
        if (i < 0 || i >= Capacity)
        {
            return 0;
        }
        return generations[i];
    }

    /// <summary>全 slot を空ける。Capacity と世代はそのまま残り、次の Alloc は
    /// 0 番から順に使い直す。</summary>
    public void Clear()
    {
        while (freeSlots.Count > 0)
        {
            freeSlots.RemoveAt(freeSlots.Count - 1);
        }
        for (int i = Capacity - 1; i >= 0; i--)
        {
            alive[i] = false;
            freeSlots.Add(i);
        }
        Live = 0;
    }
}
