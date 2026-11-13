// 窓の下の帯の入力欄 (Lite XL の core/commandview.lua)。検索、置き換え、行へ移動、file を開く、名前を付けて保存。
// 打った文字から候補を作り、上下で選び、Return で決める。Escape で閉じる。
using System;
using System.Collections.Generic;

namespace LiderEditor;

public class Suggestion
{
    public string Text = "";
    public string Info = "";
    // 行へ移動の候補の行 (0 は無し)
    public int Line;
}

public class CommandView
{
    public const int None = 0;
    public const int Find = 1;
    public const int ReplaceFind = 2;
    public const int ReplaceWith = 3;
    public const int GoToLine = 4;
    public const int OpenFile = 5;
    public const int FindFile = 6;
    public const int SaveAs = 7;

    const int MaxSuggestions = 10;

    public readonly DocView View;
    public int Kind;
    public string Label = "";
    public bool ShowSuggestions = true;
    public bool Typeahead = true;
    public List<Suggestion> Suggestions = new List<Suggestion>();
    public int SuggestionIdx = 1;
    int suggestionsOffset = 1;
    float suggestionsHeight;
    float selectionOffset;
    int lastChangeId;
    string lastText = "";
    string? saveSuggestion;

    // 開いた command の材料
    public string ReplaceOld = "";
    public int GoToView;
    List<Suggestion>? goToItems;
    List<string>? projectFiles;

    public CommandView()
    {
        View = new DocView(new Doc { SingleLine = true }, true);
        View.SizeY = 0;
    }

    public bool IsOpen()
    {
        return Kind != None;
    }

    public string GetText()
    {
        return View.Doc.GetText(1, 1, 1, Str.Huge);
    }

    public void SetText(string text, bool select)
    {
        lastText = text;
        View.Doc.Remove(1, 1, Str.Huge, Str.Huge);
        View.Doc.TextInput(SingleLine(text));
        if (select)
            View.Doc.SetSelection(Str.Huge, Str.Huge, 1, 1);
    }

    static string SingleLine(string text)
    {
        return text.Replace("\n", "");
    }

    // 入力欄に打った文字 (改行は入れない)
    public void TextInput(string text)
    {
        View.Doc.TextInput(SingleLine(text));
    }

    public void Enter(string label, int kind, string? text, bool selectText, bool showSuggestions)
    {
        if (Kind != None)
            return;
        Kind = kind;
        ShowSuggestions = showSuggestions;
        Typeahead = true;
        if (text != null || selectText)
            SetText(text ?? "", selectText);
        EditorCore.SetActive(EditorCore.CommandId);
        UpdateSuggestions();
        Label = label + ": ";
        View.Label = Label;
    }

    public void Exit(bool submitted, bool inexplicit)
    {
        var kind = Kind;
        if (EditorCore.Active == EditorCore.CommandId)
            EditorCore.SetActive(EditorCore.LastActive);
        Kind = None;
        View.Doc = new Doc { SingleLine = true };
        Suggestions = new List<Suggestion>();
        goToItems = null;
        projectFiles = null;
        if (!submitted)
            Commands.CancelCommand(kind, !inexplicit);
        saveSuggestion = null;
        lastText = "";
    }

    public void Submit()
    {
        var item = SuggestionIdx >= 1 && SuggestionIdx <= Suggestions.Count ? Suggestions[SuggestionIdx - 1] : null;
        var text = GetText();
        var kind = Kind;
        var old = ReplaceOld;
        var goTo = GoToView;
        Exit(true, false);
        Commands.SubmitCommand(kind, text, item, old, goTo);
    }

    public void Complete()
    {
        if (Suggestions.Count > 0)
            SetText(Suggestions[SuggestionIdx - 1].Text, false);
    }

    int Overflow(int n, int count)
    {
        if (count == 0)
            return 0;
        return ((n - 1) % count + count) % count + 1;
    }

    public void MoveSuggestionIdx(int dir)
    {
        if (ShowSuggestions)
        {
            SuggestionIdx = Overflow(SuggestionIdx + dir, Suggestions.Count);
            Complete();
            lastChangeId = View.Doc.ChangeId();
        }
        else
        {
            var current = Suggestions.Count > 0 && SuggestionIdx >= 1 ? Suggestions[SuggestionIdx - 1].Text : null;
            var text = GetText();
            if (text == current)
            {
                var n = SuggestionIdx + dir;
                if (n == 0 && saveSuggestion != null)
                    SetText(saveSuggestion, false);
                else
                {
                    SuggestionIdx = Overflow(n, Suggestions.Count);
                    Complete();
                }
            }
            else
            {
                saveSuggestion = text;
                Complete();
            }
            lastChangeId = View.Doc.ChangeId();
            Suggest(GetText());
        }
        var maxVisible = Math.Min(MaxSuggestions, Suggestions.Count);
        if (dir > 0)
        {
            if (suggestionsOffset + maxVisible < SuggestionIdx + 1)
                suggestionsOffset = SuggestionIdx - maxVisible + 1;
            else if (suggestionsOffset > SuggestionIdx)
                suggestionsOffset = SuggestionIdx;
        }
        else
        {
            if (suggestionsOffset > SuggestionIdx)
                suggestionsOffset = SuggestionIdx;
            else if (suggestionsOffset + maxVisible < SuggestionIdx + 1)
                suggestionsOffset = SuggestionIdx - maxVisible + 1;
        }
    }

    // 今の command の候補 (検索は、ここで見つけた所を選んで見せる)
    List<Suggestion> Suggest(string text)
    {
        var res = new List<Suggestion>();
        if (Kind == Find)
        {
            Commands.UpdatePreview(text);
            foreach (var s in EditorCore.PreviousFind)
                res.Add(new Suggestion { Text = s });
        }
        else if (Kind == ReplaceFind)
        {
            foreach (var s in EditorCore.PreviousFind)
                res.Add(new Suggestion { Text = s });
        }
        else if (Kind == ReplaceWith)
        {
            foreach (var s in EditorCore.PreviousReplace)
                res.Add(new Suggestion { Text = s });
        }
        else if (Kind == GoToLine)
        {
            if (!AllDigits(text))
                res = FuzzyItems(GoToItems(), text, false);
        }
        else if (Kind == OpenFile || Kind == SaveAs)
        {
            foreach (var s in Paths.Suggest(text))
                res.Add(new Suggestion { Text = s });
        }
        else if (Kind == FindFile)
        {
            res = FuzzyFiles(text);
        }
        return res;
    }

    static bool AllDigits(string text)
    {
        var u = new TinySystem.Text.Utf8Bytes(text);
        for (var i = 0; i < u.Length; i++)
            if (u[i] < '0' || u[i] > '9')
                return false;
        return true;
    }

    List<Suggestion> GoToItems()
    {
        if (goToItems != null)
            return goToItems;
        var items = new List<Suggestion>();
        var view = EditorCore.ViewOf(GoToView);
        if (view != null)
            for (var i = 1; i <= view.Doc.Lines.Count; i++)
                items.Add(new Suggestion { Text = view.Doc.Lines[i - 1], Line = i, Info = "line: " + i });
        goToItems = items;
        return items;
    }

    // query に合う物を、点の高い順に (同じ点は元の順)
    static List<Suggestion> FuzzyItems(List<Suggestion> items, string query, bool files)
    {
        var scored = new List<Suggestion>();
        var scores = new List<int>();
        foreach (var item in items)
        {
            var score = Fuzzy.Match(item.Text, query, files);
            if (score == Fuzzy.NoMatch)
                continue;
            var at = scored.Count;
            while (at > 0 && scores[at - 1] < score)
                at--;
            scored.Insert(at, item);
            scores.Insert(at, score);
        }
        return scored;
    }

    List<Suggestion> FuzzyFiles(string query)
    {
        if (projectFiles == null)
            projectFiles = Paths.ProjectFiles() ?? new List<string>();
        var items = new List<Suggestion>();
        foreach (var f in projectFiles)
            items.Add(new Suggestion { Text = f });
        if (query != "")
            return FuzzyItems(items, query, true);
        // 空なら最近開いた file (2 番目から、最初の物は最後) の後に全部
        var res = new List<Suggestion>();
        var recents = EditorCore.VisitedFiles;
        for (var i = 1; i < recents.Count; i++)
            res.Add(new Suggestion { Text = recents[i] });
        if (recents.Count > 0)
            res.Add(new Suggestion { Text = recents[0] });
        foreach (var item in FuzzyItems(items, "", true))
            res.Add(item);
        return res;
    }

    void UpdateSuggestions()
    {
        Suggestions = Suggest(GetText());
        SuggestionIdx = 1;
        suggestionsOffset = 1;
    }

    float SuggestionLineHeight()
    {
        return DrawList.FontHeight() + Style.PaddingY;
    }

    public void Update()
    {
        View.Update();
        if (EditorCore.Active != EditorCore.CommandId && Kind != None)
            Exit(false, true);
        if (lastChangeId != View.Doc.ChangeId())
        {
            UpdateSuggestions();
            if (Typeahead && Suggestions.Count >= SuggestionIdx && SuggestionIdx >= 1)
            {
                var current = GetText();
                var suggested = Suggestions[SuggestionIdx - 1].Text;
                if (Str.Len(lastText) < Str.Len(current) && suggested.StartsWith(current))
                {
                    SetText(suggested, false);
                    View.Doc.SetSelection(1, Str.Len(current) + 1, 1, Str.Huge);
                }
                lastText = current;
            }
            lastChangeId = View.Doc.ChangeId();
        }
        View.BarGutter = DrawList.Width(Label) + Style.PaddingX;
        var slh = SuggestionLineHeight();
        suggestionsHeight = ShowSuggestions ? Math.Min(Suggestions.Count, MaxSuggestions) * slh : 0;
        selectionOffset = (SuggestionIdx - suggestionsOffset + 1) * slh;
        View.SizeY = EditorCore.Active == EditorCore.CommandId ? DrawList.FontHeight() + Style.PaddingY * 2 : 0;
    }

    // 窓の下の端に帯を描く (帯の下地は lider が命令の外枠に塗る)
    public void Draw(float w, float h)
    {
        var barH = Math.Max(View.SizeY, DrawList.FontHeight() + Style.PaddingY * 2);
        View.SizeX = w;
        View.SizeY = barH;
        View.PosX = 0;
        View.PosY = h - barH;
        DrawList.PushClip(0, h - barH, w, barH);
        View.Draw();
        DrawList.PopClip();
    }

    // 帯の上に候補の箱を描く (帯の後に描く)
    public void DrawSuggestions()
    {
        if (!ShowSuggestions)
            return;
        var lh = SuggestionLineHeight();
        var dh = Style.DividerSize;
        var x = View.LineX();
        var bh = (float)Math.Ceiling(suggestionsHeight);
        var rx = View.PosX;
        var ry = View.PosY - bh - dh;
        var rw = View.SizeX;
        DrawList.PushClip(rx, ry, rw, bh);
        if (Suggestions.Count > 0)
        {
            DrawList.Rect(rx, ry, rw, bh, Style.Background3);
            DrawList.Rect(rx, ry - dh, rw, dh, Style.Divider);
            DrawList.Rect(rx, View.PosY - selectionOffset - dh, rw, lh, Style.LineHighlight);
        }
        var first = Math.Max(suggestionsOffset, 1);
        var last = Math.Min(suggestionsOffset + MaxSuggestions, Suggestions.Count);
        for (var i = first; i <= last; i++)
        {
            var item = Suggestions[i - 1];
            var color = i == SuggestionIdx ? Style.Accent : Style.Text;
            var y = View.PosY - (i - first + 1) * lh - dh;
            DrawList.DrawAligned(Style.Font, color, item.Text, 0, x, y, 0, lh);
            if (item.Info != "")
                DrawList.DrawAligned(Style.Font, Style.Dim, item.Info, 2, x, y, View.SizeX - x - Style.PaddingX, lh);
        }
        DrawList.PopClip();
    }
}
