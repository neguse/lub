// Table of named commands.
// Besides the commands inside the editor (Commands), lider operations are also listed here, and the key table (Keymap) and search call them by the same names.
using System;
using System.Collections.Generic;

namespace LiderEditor;

public class Command
{
    // Name in the table ("window:close"). Search looks things up by this text
    public string Name = "";
    // What it does (shown in search and help)
    public string Label = "";
    // If it applies to the current focus, runs it and returns true; otherwise false (the key table tries the next command)
    public Func<bool> Run = () => false;
    // Whether it repeats while the key is held
    public bool Repeats;
    // Whether it also works while the editor's input fields (CommandView, NagView) are shown
    public bool OverModal;
}

public static class CommandTable
{
    public static readonly List<Command> All = new List<Command>();
    static readonly Dictionary<string, Command> byName = new Dictionary<string, Command>();
    // Whether the current key is a repeat while held (decided by whoever passes the key)
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

    // Runs the named command (false if it is not in the table or does not apply)
    public static bool Run(string name)
    {
        var command = Find(name);
        return command != null && command.Run();
    }
}
