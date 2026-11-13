// 窓の上の帯の確認 (Lite XL の core/nagview.lua)。答えるまで key を取る。
// 左右で答えを選び、Return で決める。y と n でも答えられ、Escape は「いいえ」。
using System;
using System.Collections.Generic;

namespace LiderEditor;

public class NagRequest
{
    public string Title = "";
    public string Message = "";
    public List<string> Options = new List<string>();
    public int Yes;
    public int No;
    // 答えたときにすること (0 は何もしない、1 は view の file を消す、2 は file を消す)
    public int Action;
    public int View;
    public string Path = "";
}

public class NagView
{
    public const int ActionNone = 0;
    public const int ActionDeleteView = 1;
    public const int ActionDeleteFile = 2;

    const float BorderWidth = 1;
    const float UnderlineWidth = 2;
    const float UnderlineMargin = 1;

    public bool Visible;
    public bool ForceFocus;
    public NagRequest? Current;
    public int Hovered;
    readonly List<NagRequest> queue = new List<NagRequest>();
    float showHeight;
    float targetHeight;
    public float SizeX = 800;
    // 最後に選んだ答え
    public string LastAnswer = "";

    public void Show(NagRequest request)
    {
        queue.Add(request);
        Next();
    }

    List<string> MessageLines()
    {
        var lines = new List<string>();
        if (Current != null)
            foreach (var l in Current.Message.Split('\n'))
                lines.Add(l);
        return lines;
    }

    float MessageHeight()
    {
        return MessageLines().Count * DrawList.FontHeight();
    }

    float ButtonsHeight()
    {
        var lh = DrawList.FontHeight();
        return lh + 2 * BorderWidth + lh;
    }

    void Next()
    {
        if (queue.Count > 0)
        {
            Current = queue[0];
            queue.RemoveAt(0);
            Visible = true;
            targetHeight = Math.Max(MessageHeight(), ButtonsHeight());
            Hovered = Current.Yes;
            ForceFocus = true;
            EditorCore.SetActive(EditorCore.NagId);
            EditorCore.Redraw = true;
        }
        else
        {
            ForceFocus = false;
            EditorCore.RestoreActive();
            Visible = false;
        }
    }

    public void ChangeHovered(int i)
    {
        if (i != Hovered)
        {
            Hovered = i;
            EditorCore.Redraw = true;
        }
    }

    public void PreviousEntry()
    {
        if (Current == null)
            return;
        var hover = Hovered != 0 ? Hovered : 1;
        ChangeHovered(hover == 1 ? Current.Options.Count : hover - 1);
    }

    public void NextEntry()
    {
        if (Current == null)
            return;
        var hover = Hovered != 0 ? Hovered : 1;
        ChangeHovered(hover == Current.Options.Count ? 1 : hover + 1);
    }

    public void SelectYes()
    {
        if (Current == null)
            return;
        ChangeHovered(Current.Yes);
        Select();
    }

    public void SelectNo()
    {
        if (Current == null)
            return;
        ChangeHovered(Current.No);
        Select();
    }

    public void Select()
    {
        if (Current == null || Hovered < 1 || Hovered > Current.Options.Count)
            return;
        var r = Current;
        var yes = Hovered == r.Yes;
        LastAnswer = r.Options[Hovered - 1];
        if (yes && r.Action == ActionDeleteView)
        {
            if (Paths.Remove(r.Path))
                EditorCore.Deleted.Add(r.View);
        }
        else if (yes && r.Action == ActionDeleteFile)
            Paths.Remove(r.Path);
        Next();
    }

    public void OnTextInput(string text)
    {
        if (!Visible)
            return;
        var t = Str.Lower(text);
        if (t == "y")
            SelectYes();
        else if (t == "n")
            SelectNo();
    }

    public void Update()
    {
        if (!Visible && showHeight <= 0)
            return;
        if (Visible && EditorCore.Active == EditorCore.NagId && Current != null)
            showHeight = targetHeight + 2 * Style.PaddingY;
        else
        {
            showHeight = 0;
            Current = null;
        }
    }

    public string Signature()
    {
        return (Current != null ? Current.Title : "") + "|" + Hovered;
    }

    // 窓の上の端に描く
    public void Draw(float w)
    {
        if ((!Visible && showHeight <= 0) || Current == null)
            return;
        SizeX = w;
        float ox = 0;
        float oy = 0;
        DrawList.Rect(ox, oy, SizeX, showHeight, Style.Nagbar);
        ox += Style.PaddingX;
        DrawList.PushClip(ox, oy, SizeX, showHeight);
        if (queue.Count > 0)
        {
            ox = DrawList.DrawAligned(Style.Font, Style.NagbarText, "[" + queue.Count + "]", 0, ox, oy, SizeX, showHeight);
            ox += Style.PaddingX;
        }
        var lh = DrawList.FontHeight();
        var lines = MessageLines();
        oy = oy + Style.PaddingY + (targetHeight - MessageHeight()) / 2;
        foreach (var line in lines)
        {
            DrawList.DrawText(Style.Font, line, ox, oy, Style.NagbarText);
            oy += lh;
        }
        // 答えの button を右から並べる
        var bh = ButtonsHeight();
        var bx = SizeX;
        var by = showHeight - bh - Style.PaddingY;
        for (var i = Current.Options.Count; i >= 1; i--)
        {
            var text = Current.Options[i - 1];
            var bw = DrawList.Width(text) + 2 * BorderWidth + Style.PaddingX;
            bx = bx - bw - Style.PaddingX;
            var fw = bw - 2 * BorderWidth;
            var fh = bh - 2 * BorderWidth;
            var fx = bx + BorderWidth;
            var fy = by + BorderWidth;
            DrawList.Rect(bx, by, bw, bh, Style.NagbarText);
            DrawList.Rect(fx, fy, fw, fh, Style.Nagbar);
            if (i == Hovered)
                DrawList.Rect(fx + UnderlineMargin, fy + fh - UnderlineMargin - UnderlineWidth, fw - 2 * UnderlineMargin, UnderlineWidth, Style.NagbarText);
            DrawList.DrawAligned(Style.Font, Style.NagbarText, text, 1, fx, fy, fw, fh);
        }
        DrawList.PopClip();
    }
}
