// 名前の付いた命令の表。
// エディタの中の命令 (Commands) のほかに、lider の操作もここに載せ、key の表 (Keymap) と検索から、同じ名前で呼ぶ。
using System;
using System.Collections.Generic;

namespace LiderEditor;

public class Command
{
    // 表の中の名前 ("window:close")。検索ではこの字で引く
    public string Name = "";
    // 何をするか (検索と案内に出す)
    public string Label = "";
    // 今の focus で効くなら実行して true、効かないなら false (key の表は、次の命令を試す)
    public Func<bool> Run = () => false;
    // key を押し続けている間、繰り返すか
    public bool Repeats;
    // エディタの入力欄 (CommandView、NagView) が出ている間も効くか
    public bool OverModal;
}

public static class CommandTable
{
    public static readonly List<Command> All = new List<Command>();
    static readonly Dictionary<string, Command> byName = new Dictionary<string, Command>();
    // 今の key が、押し続けている間の繰り返しか (key を渡す側が決める)
    public static bool Repeated;

    public static Command Register(string name, string label, Func<bool> run)
    {
        var command = new Command { Name = name, Label = label, Run = run };
        if (byName.TryGetValue(name, out var old))
            All.Remove(old);
        byName[name] = command;
        All.Add(command);
        return command;
    }

    public static Command? Find(string name)
    {
        return byName.TryGetValue(name, out var command) ? command : null;
    }

    // 名前の命令を実行する (表に無いか、効かなければ false)
    public static bool Run(string name)
    {
        var command = Find(name);
        return command != null && command.Run();
    }
}
