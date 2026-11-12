// 実装ライブラリ lubx の AppTimeline。
// App.Use で開いた app の直近の frame を控え、止めて、過去の frame に戻る。

using Lub;

namespace Lubx;

/// <summary>
/// 開いた app を App.Step の代わりに進め、frame ごとに、進める前の状態 (App.Save) と、見せた入力 (StepInput.Keep) と dt を控える。
/// 止めている間の Step は、止めた frame の状態を読み込み、控えた入力と dt でその frame を作り直して描く (code を直せば、直した code で描き直す)。
/// 控えは firstSlot から capacity 個の番号を使う。同じ番号をほかの App.Save に使わないこと。
/// 物理の world、音、file は戻らない (App.Load と同じ)。
/// </summary>
public class AppTimeline
{
    readonly int capacity;
    readonly int firstSlot;
    readonly float[] dts;
    // 次に書く場所と、控えている数
    int head;
    int count;
    // 止めている位置 (何個前か。0 は直近に控えた frame)。止めていなければ -1
    int paused = -1;

    public AppTimeline(int capacity, int firstSlot = 0)
    {
        this.capacity = capacity;
        this.firstSlot = firstSlot;
        dts = new float[capacity];
    }

    /// <summary>控えている frame の数。
    /// </summary>
    public int Count => count;

    /// <summary>止めている位置 (何個前か)。止めていなければ -1。
    /// </summary>
    public int Position => paused;

    int Index(int back) => (head - 1 - back + capacity * 2) % capacity;

    /// <summary>back 個前 (0 は直近に控えた frame) の frame で止める。控えている範囲に丸める。控えが無ければ何もしない。
    /// </summary>
    public void Pause(int back)
    {
        if (count == 0) return;
        paused = back < 0 ? 0 : back >= count ? count - 1 : back;
    }

    /// <summary>止めている位置から続ける。それより後の控えは捨てる。
    /// </summary>
    public void Resume()
    {
        if (paused < 0) return;
        // 止めた frame は次の Step が同じ状態から作り直して控え直す
        head = Index(paused);
        count -= paused + 1;
        resume = true;
        paused = -1;
    }

    // Resume の後の最初の Step は、止めていた frame の状態から始める
    bool resume;

    /// <summary>app を 1 frame 進める (止めていれば、止めた frame を作り直す)。
    /// 戻り値と引数は App.Step と同じ。状態を控えられない app (App.Save が false) は、控えずに進める。
    /// </summary>
    public bool Step(AppRef app, TextureRef target, float dt, StepInput? input = null)
    {
        if (paused >= 0)
        {
            var at = Index(paused);
            if (!App.Load(app, firstSlot + at, out _)) return false;
            return App.Step(app, target, dts[at], new StepInput { Replay = firstSlot + at });
        }
        var slot = firstSlot + head;
        if (resume)
        {
            resume = false;
            App.Load(app, slot, out _);
        }
        if (!App.Save(app, slot, out _))
            return App.Step(app, target, dt, input);
        dts[head] = dt;
        head = (head + 1) % capacity;
        if (count < capacity) count++;
        return App.Step(app, target, dt, new StepInput
        {
            Keyboard = input?.Keyboard,
            Mouse = input?.Mouse,
            Gamepad = input?.Gamepad,
            MouseX = input?.MouseX,
            MouseY = input?.MouseY,
            Replay = input?.Replay,
            Keep = slot,
        });
    }

    /// <summary>控えを全部捨てる。
    /// </summary>
    public void Clear()
    {
        for (var i = 0; i < capacity; i++) App.Drop(firstSlot + i);
        head = 0;
        count = 0;
        paused = -1;
        resume = false;
    }
}
