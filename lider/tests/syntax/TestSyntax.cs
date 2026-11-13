// lider の窓に開いた .cs を、エディタの core が C# として色分けして描く (src/Editor/Syntax.cs)。
// 描いた命令の text の色 (前の空白を除いた text ごと) で、注釈、keyword、型、文字列、数が、ふつうの名前 (Target) とも互いとも違う色になり、関数の名前もふつうの名前と違う色になることを見る
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestSyntax
{
    static bool ran;
    static bool failed;

    public static void OnInit()
    {
        TestInit();
        Lider.OnInit();
    }

    public static void OnEvent(EventData e)
    {
        Lider.OnEvent(e);
    }

    public static void OnQuit()
    {
        Lider.OnQuit();
    }

    public static void OnFrame(float dt)
    {
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void TestFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        var path = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
        var w = Lider.Work;
        Check(path != null && w != null, "LIDER_TEST_FILE or the workspace is missing");
        if (failed)
            return;
        w!.FocusOn(path!);
        var doc = w.Focus!;
        var n = EditorHost.Draw(doc.Id, PanelView.BasePxW, PanelView.BasePxH);
        var colors = new Dictionary<string, int>();
        for (var i = 0; i < n; i++)
            if (EditorHost.CmdKind(i) == 2 && EditorHost.CmdLine(i) > 0)
                colors[EditorHost.CmdText(i).Trim()] = EditorHost.CmdColor(i);
        var names = new List<string> { "Target", "// TestSyntax が色を見る file", "public", "string", "\"text\"", "0x1F" };
        foreach (var name in names)
            Check(colors.ContainsKey(name), "no text command for '" + name + "'");
        Check(colors.ContainsKey("Value"), "no text command for 'Value'");
        if (failed)
            return;
        for (var a = 0; a < names.Count; a++)
            for (var b = a + 1; b < names.Count; b++)
                Check(colors[names[a]] != colors[names[b]], "'" + names[a] + "' and '" + names[b] + "' have the same color");
        Check(colors["Value"] != colors["Target"], "the function name 'Value' has the color of a plain name");
        Check(colors.ContainsKey("static") && colors["static"] == colors["public"], "'static' is not colored as a keyword like 'public'");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
