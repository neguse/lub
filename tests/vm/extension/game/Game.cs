// tcs VM の player の native の拡張の検査: Ext.ref.cs の関数を、隣の Ext.so が host の関数として登録する。
using System;
using Lub;

public static class Game
{
    public static void OnInit() { App.Config(new ConfigOpts { Width = 64, Height = 64 }); }

    public static void OnFrame(float dt)
    {
        int sum = Ext.Add(2, 3);
        bool ok = Ext.Greet("vm", out var text);
        if (sum == 5 && ok && text == "hello vm")
        {
            Console.WriteLine("PASS");
            App.Quit(0);
        }
        else
        {
            Console.WriteLine("FAIL sum=" + sum + " ok=" + ok + " text=" + text);
            App.Quit(1);
        }
    }
}
