// 実装ライブラリ lubx の FixedStep。
// pending 集合は scanKeys と平行な List<bool> で持つ。new bool[n] は Lua では
// 空 table になり .Length が 0 になるので、構築時に要素数ぶん false を詰める。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>
/// 固定 tick 駆動。可変レートの onFrame(dt) から毎フレーム frame() を呼ぶと、
/// ゲーム進行 (フレーム単位のルール・物理・AI) を display refresh 非依存の
/// 固定 Hz tick に分離する。tick は 1 フレームに 0〜maxCatchUp 回走る。
/// keyPressed などの edge は tick 粒度で配送され、tick 0 回のフレームの
/// edge も失われない。edge は tick callback の中で読むこと。
/// callback は保持しない (毎フレーム frame() に渡す) ので、playground の
/// live 反映後も次のフレームから新しいコードが呼ばれる。
/// maxCatchUp tick を回してもまだ 1 tick 分以上の時間が残ったフレームは、
/// 残りを捨ててゲームを実時間より遅らせる。捨てた時間は LastDropped /
/// TotalDropped で読める。
/// </summary>
public class FixedStep
{
    /// <summary>tick callback に渡される固定 dt (= 1/hz) 秒。</summary>
    public float TickDt;

    /// <summary>直近の frame() で走った tick 数。</summary>
    public int LastSteps { get; private set; }

    /// <summary>直近の frame() で捨てた時間 (秒)。catch-up 上限で打ち切った
    /// 残りと、Stop() で捨てた分。</summary>
    public float LastDropped { get; private set; }

    /// <summary>生成 (または ResetDropped()) 以降に捨てた時間の合計 (秒)。</summary>
    public float TotalDropped { get; private set; }

    private int maxCatchUp;
    private float accumulator = 0;
    private bool stopped = false;
    private bool running = false;

    // lub.Input が公開する全キー名 (Key 定数 + a..z + 0..9)。
    // キー名を足したらここにも足す。
    // TODO: runtime が timestamp 付き入力 event を公開したら、この走査を
    // event 消費に置き換える (公開 API は変えない)。
    private static string[] scanKeys = new string[]
    {
        "space", "enter", "escape", "tab", "backspace",
        "left", "right", "up", "down",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
    };

    // tick 粒度 edge の保留分。次の tick が消費するまでフレームを跨いで持ち越す。
    // key は scanKeys と同じ添字、mouse は button 番号 (1〜3) を添字にする。
    private List<bool> pendingKeyPressed = new List<bool>();
    private List<bool> pendingKeyReleased = new List<bool>();
    private List<bool> pendingMousePressed = new List<bool>();
    private List<bool> pendingMouseReleased = new List<bool>();

    /// <summary>hz: tick の周波数 (正の値、省略 = 60)。maxCatchUp: 1 回の
    /// frame() で走る tick 数の上限 (1 以上)。省略時は 50 ms 分の tick 数
    /// (hz / 20 の切り上げ、最低 3) で、60 Hz なら 3。どの hz でも 20 fps
    /// まで落ちても実時間どおりに進み、それより重いフレームの後に tick を
    /// まとめて回してさらに重くする量は 50 ms 分に抑えられる。
    /// tcs は default 値を Lua 側へ出さないので nullable + ?? で受ける
    /// (Rand と同じ)。</summary>
    public FixedStep(float? hz = null, int? maxCatchUp = null)
    {
        var h = hz ?? 60.0f;
        TickDt = 1.0f / h;
        this.maxCatchUp =
            maxCatchUp ?? Math.Max(3, (int)Math.Ceiling(h / 20.0f));
        for (int i = 0; i < scanKeys.Length; i++)
        {
            pendingKeyPressed.Add(false);
            pendingKeyReleased.Add(false);
        }
        for (int b = 0; b < 4; b++)
        {
            pendingMousePressed.Add(false);
            pendingMouseReleased.Add(false);
        }
    }

    /// <summary>onFrame から毎フレーム呼ぶ。実測 dt を積み、固定 tick を
    /// 0〜maxCatchUp 回実行する。上限まで回してもまだ 1 tick 分以上残って
    /// いれば、残りは捨てて LastDropped に記録する。tick は保持されない。</summary>
    public void Frame(float dt, Action<float> tick)
    {
        LatchEdges();
        if (dt > 0)
        {
            accumulator = accumulator + dt;
        }
        LastDropped = 0;
        stopped = false;
        running = true;
        int steps = 0;
        while (accumulator + 1e-9f >= TickDt && steps < maxCatchUp && !stopped)
        {
            tick(TickDt);
            ClearPending();
            accumulator = accumulator - TickDt;
            if (accumulator < 0)
            {
                accumulator = 0;
            }
            steps = steps + 1;
        }
        running = false;
        LastSteps = steps;
        // 上限で打ち切ったときは残りを全部捨てる。次のフレームへ持ち越すと、
        // 重いフレームの次にさらに多くの tick を回すことになる。Stop() された
        // ときも同じ。
        if (stopped || accumulator + 1e-9f >= TickDt)
        {
            Drop();
        }
    }

    /// <summary>tick callback 内から呼ぶと、このフレームの残り catch-up tick
    /// と溜まった時間を捨てて frame() を抜ける。捨てた時間は LastDropped /
    /// TotalDropped に入る。</summary>
    public void Stop()
    {
        stopped = true;
        // tick の中なら、今の tick の分を引いた残りを frame() が捨てる
        if (!running)
        {
            Drop();
        }
    }

    /// <summary>TotalDropped を 0 に戻す。</summary>
    public void ResetDropped()
    {
        TotalDropped = 0;
    }

    private void Drop()
    {
        LastDropped = LastDropped + accumulator;
        TotalDropped = TotalDropped + accumulator;
        accumulator = 0;
    }

    /// <summary>前回の tick 以降にキーが押されたか。tick callback 内で読む。</summary>
    public bool KeyPressed(string key)
    {
        int i = KeyIndex(key);
        return i >= 0 && pendingKeyPressed[i];
    }

    /// <summary>前回の tick 以降にキーが離されたか。tick callback 内で読む。</summary>
    public bool KeyReleased(string key)
    {
        int i = KeyIndex(key);
        return i >= 0 && pendingKeyReleased[i];
    }

    /// <summary>前回の tick 以降にボタンが押されたか (1=左 2=中 3=右、省略 = 左)。</summary>
    public bool MousePressed(int? button = null)
    {
        int b = button ?? 1;
        return b >= 1 && b <= 3 && pendingMousePressed[b];
    }

    /// <summary>前回の tick 以降にボタンが離されたか (1=左 2=中 3=右、省略 = 左)。</summary>
    public bool MouseReleased(int? button = null)
    {
        int b = button ?? 1;
        return b >= 1 && b <= 3 && pendingMouseReleased[b];
    }

    /// <summary>直近の tick から次の tick までの経過割合 (0〜1)。補間用。</summary>
    public float Alpha()
    {
        return Math.Min(accumulator / TickDt, 1.0f);
    }

    private static int KeyIndex(string key)
    {
        for (int i = 0; i < scanKeys.Length; i++)
        {
            if (scanKeys[i] == key)
            {
                return i;
            }
        }
        return -1;
    }

    // edge は runtime がフレームラッチしたものを毎フレーム吸い上げる。
    // tick 0 回のフレームでも失わないための持ち越しがここ。
    private void LatchEdges()
    {
        for (int i = 0; i < scanKeys.Length; i++)
        {
            if (Input.KeyPressed(scanKeys[i]))
            {
                pendingKeyPressed[i] = true;
            }
            if (Input.KeyReleased(scanKeys[i]))
            {
                pendingKeyReleased[i] = true;
            }
        }
        for (int b = 1; b <= 3; b++)
        {
            if (Input.MousePressed(b))
            {
                pendingMousePressed[b] = true;
            }
            if (Input.MouseReleased(b))
            {
                pendingMouseReleased[b] = true;
            }
        }
    }

    private void ClearPending()
    {
        for (int i = 0; i < pendingKeyPressed.Count; i++)
        {
            pendingKeyPressed[i] = false;
            pendingKeyReleased[i] = false;
        }
        for (int b = 0; b < 4; b++)
        {
            pendingMousePressed[b] = false;
            pendingMouseReleased[b] = false;
        }
    }
}
