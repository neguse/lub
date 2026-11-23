// Checks the round trip with files.
//   - A CRLF file is saved with CRLF, and a file without a final newline gets one added on save
//   - Opening the same file twice shares the doc, and the doc remains when one of them is closed
//   - FollowDisk copies only the lines changed outside into a doc that has no edits, keeps the cursor on the same character, and counts the doc as saved. It does not copy into a doc that has edits
//   - Restore puts a draft in as unsaved edits, and undo goes back to the original. A draft with the same text, or an empty draft, is not put in
//   - The removal count and position (RemovedCount, RemovedX, RemovedY) increase on each removal and on each insertion reverted by undo
//   - ChangeId changes on edits and goes back to the previous number on undo. Saving makes the doc no longer Dirty, and undo makes it Dirty
using System;
using Lub;
using LiderEditor;

public static class TestFiles
{
    static int stage;
    static float next;
    static int id;
    static string path = "";

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        if (Ed.Failed || Sys.Seconds() < next)
            return;
        next = Sys.Seconds() + 0.4f;
        if (stage == 0)
        {
            Ed.Boot();
            var crlf = Ed.Open(Ed.Write("crlf.txt", "a\r\nb\r\n"));
            Ed.Check(EditorHost.LineCount(crlf) == 2 && EditorHost.Line(crlf, 1) == "a", "opened the CRLF file as " + EditorHost.LineCount(crlf) + " lines");
            EditorHost.Insert(crlf, "x");
            EditorHost.Save(crlf);
            Ed.Check(Ed.Read("crlf.txt") == "xa\r\nb\r\n", "saved the CRLF file as " + Ed.Read("crlf.txt").Replace("\r", "\\r").Replace("\n", "\\n"));
            var bare = Ed.Open(Ed.Write("bare.txt", "a\nb"));
            EditorHost.Save(bare);
            Ed.Check(Ed.Read("bare.txt") == "a\nb\n", "saved a file without the last newline as '" + Ed.Read("bare.txt") + "'");
            // Two views of the same file
            var twin = EditorHost.Open("bare.txt");
            Ed.Check(twin != bare && EditorHost.Path(twin) == EditorHost.Path(bare), "the second view of bare.txt has another path");
            EditorHost.Insert(twin, "q");
            Ed.Check(EditorHost.Line(bare, 1) == "qa", "the views do not share the doc: " + EditorHost.Line(bare, 1));
            EditorHost.Close(twin);
            Ed.Check(EditorHost.Line(bare, 1) == "qa" && EditorHost.Dirty(bare), "closing the twin dropped the doc");

            path = Ed.Write("Follow.cs", "class F\n{\n    int a;\n    int b;\n}\n");
            id = Ed.Open(path);
            EditorHost.SetCursor(id, 3, 5);
            EditorHost.FollowDisk(id);
        }
        else if (stage == 1)
        {
            // Rewrite line 3 outside and add one line (the modification time is in seconds, so the size also changes)
            Io.SaveText(path, "class F\n{\n    int aa;\n    int z;\n    int b;\n}\n");
            var saves = EditorHost.SaveCount(id);
            EditorHost.FollowDisk(id);
            Ed.Check(EditorHost.Line(id, 3) == "    int aa;" && EditorHost.Line(id, 4) == "    int z;" && EditorHost.Line(id, 5) == "    int b;", "followed: " + EditorHost.Line(id, 3) + " / " + EditorHost.Line(id, 4));
            Ed.Check(!EditorHost.Dirty(id) && EditorHost.SaveCount(id) == saves + 1, "following the disk is not a save");
            // The cursor stays where the number of preceding non-whitespace characters is the same (before int on line 3)
            Ed.CheckCursor(id, 3, 5);
            EditorHost.Insert(id, "c");
            Io.SaveText(path, "changed\n");
            EditorHost.FollowDisk(id);
            Ed.Check(EditorHost.Line(id, 1) == "class F", "followed the disk into an edited doc");
            EditorHost.Save(id);
        }
        else if (stage == 2)
        {
            // Draft
            var draft = Ed.Write("draft.txt", "class F\n{\n    // draft\n}");
            var change = EditorHost.ChangeId(id);
            Ed.Check(EditorHost.Restore(id, draft), "Restore did not take the draft");
            Ed.Check(EditorHost.Dirty(id) && EditorHost.Line(id, 3) == "    // draft" && EditorHost.LineCount(id) == 4, "restored: " + EditorHost.Line(id, 3) + ", " + EditorHost.LineCount(id) + " lines");
            Ed.Check(!EditorHost.Restore(id, draft), "Restore took the same text again");
            Ed.Check(!EditorHost.Restore(id, Ed.Write("empty.txt", "")), "Restore took an empty draft");
            Ed.Ctrl(29);
            Ed.Check(EditorHost.ChangeId(id) == change && !EditorHost.Dirty(id), "undo did not go back to the saved text");
        }
        else if (stage == 3)
        {
            var removed = EditorHost.RemovedCount(id);
            EditorHost.SetCursor(id, 3, 5);
            Ed.Press(Ed.Delete);
            Ed.Check(EditorHost.RemovedCount(id) == removed + 1, "Delete was not counted");
            Ed.Check(EditorHost.RemovedX(id) == EditorHost.TextX(id) + 4 * Ed.CellW && EditorHost.RemovedY(id) == EditorHost.LineY(id, 3), "removed at " + EditorHost.RemovedX(id) + "," + EditorHost.RemovedY(id));
            EditorHost.Insert(id, "xyz");
        }
        else if (stage == 4)
        {
            var removed = EditorHost.RemovedCount(id);
            Ed.Ctrl(29);
            Ed.Check(EditorHost.RemovedCount(id) == removed + 1, "undoing the insert was not counted");
            Ed.Check(EditorHost.LastError() == "", "error: " + EditorHost.LastError());
            Ed.Pass();
            return;
        }
        stage++;
    }

    public static ErrorAction OnError(string message)
    {
        return Ed.OnError(message);
    }
}
