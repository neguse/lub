using System;
using static Lub;

/// <summary>fixed tick 1 回分の入力。</summary>
public class NgsInputSnapshot
{
    public int DirX = 0; // -1/0/1
    public int DirY = 0; // -1/0/1 (down = +1、画面下方向)
    public bool Fire = false; // Z held
    public bool Slow = false; // X held
    public bool Menu = false; // Z pressed-this-frame (trigger)
    public bool Cancel = false; // ESC trigger
    public bool NoGod = false; // C held
}

public interface INgsInputSource
{
    void Capture(); // render ごとに実入力と edge を保存
    void Refresh(); // fixed tick ごとに snapshot を更新
    NgsInputSnapshot Current(); // 最新の snapshot
}

/// <summary>実入力。frame でラッチされた edge を fixed tick まで持ち越す。</summary>
public class NgsInput : INgsInputSource
{
    readonly NgsInputSnapshot snap = new NgsInputSnapshot();
    bool up = false;
    bool down = false;
    bool left = false;
    bool right = false;
    bool fire = false;
    bool slow = false;
    bool noGod = false;
    bool pendingFire = false;
    bool pendingMenu = false;
    bool pendingCancel = false;

    public NgsInputSnapshot Current() => snap;

    public void Capture()
    {
        up = Input.KeyDown("up");
        down = Input.KeyDown("down");
        left = Input.KeyDown("left");
        right = Input.KeyDown("right");
        fire = Input.KeyDown("z");
        slow = Input.KeyDown("x");
        noGod = Input.KeyDown("c");
        if (Input.KeyPressed("z"))
        {
            pendingFire = true;
            pendingMenu = true;
        }
        if (Input.KeyPressed("escape")) pendingCancel = true;
    }

    public void Refresh()
    {
        snap.DirX = (right ? 1 : 0) - (left ? 1 : 0);
        snap.DirY = (down ? 1 : 0) - (up ? 1 : 0);
        snap.Fire = fire || pendingFire;
        snap.Slow = slow;
        snap.NoGod = noGod;
        snap.Menu = pendingMenu;
        snap.Cancel = pendingCancel;
        pendingFire = false;
        pendingMenu = false;
        pendingCancel = false;
    }
}

/// <summary>script で決める入力 (golden / debug 用)。script が null なら無入力。</summary>
public class NgsMockInput : INgsInputSource
{
    readonly NgsInputSnapshot snap = new NgsInputSnapshot();
    readonly Func<int, NgsInputSnapshot>? script;
    int frame = 0;

    public NgsMockInput(Func<int, NgsInputSnapshot>? script)
    {
        this.script = script;
    }

    public NgsInputSnapshot Current() => snap;

    public void Capture()
    {
    }

    public void Refresh()
    {
        var s = script == null ? null : script(frame);
        if (s == null)
        {
            snap.DirX = 0;
            snap.DirY = 0;
            snap.Fire = false;
            snap.Slow = false;
            snap.Menu = false;
            snap.Cancel = false;
            snap.NoGod = false;
        }
        else
        {
            snap.DirX = s.DirX;
            snap.DirY = s.DirY;
            snap.Fire = s.Fire;
            snap.Slow = s.Slow;
            snap.Menu = s.Menu;
            snap.Cancel = s.Cancel;
            snap.NoGod = s.NoGod;
        }
        frame = frame + 1;
    }
}
