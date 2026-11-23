// Writes unsaved text to the drafts dir and, on the next launch, puts it back into the windows still unsaved.
// Drafts are not the files themselves, so they do not enter the game. env LIDER_DRAFTS is the dir (if missing, no drafts are made)
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

public class Drafts
{
    public const float WriteSeconds = 1;

    readonly string dir;
    // Path of the written draft file and the Document.Revision at the time of writing
    readonly Dictionary<string, int> written = new Dictionary<string, int>();
    float since;

    public Drafts(string dir)
    {
        this.dir = dir;
    }

    // The dir is flat, so each draft is named after its path from Root with '/' replaced by %2F
    public static string NameOf(string relative)
    {
        return relative.Replace("%", "%25").Replace("/", "%2F");
    }

    public static string PathOf(string name)
    {
        return name.Replace("%2F", "/").Replace("%25", "%");
    }

    public void Restore(Workspace w)
    {
        if (!Document.Booted)
            Document.Boot(8, 16);
        foreach (var e in Disk.ListDir(dir))
        {
            var path = w.PathOf(PathOf(e.Name));
            var draft = dir + "/" + e.Name;
            if (!w.Files.Contains(path))
            {
                EditorHost.RemoveFile(draft);
                continue;
            }
            w.FocusOn(path);
            var doc = w.Docs[path];
            if (EditorHost.Restore(doc.Id, draft))
            {
                doc.Sync();
                written[path] = doc.Revision;
            }
            else
                EditorHost.RemoveFile(draft);
        }
    }

    public void Tick(Workspace w, float dt)
    {
        since += dt;
        if (since < WriteSeconds)
            return;
        since = 0;
        foreach (var pair in w.Docs)
        {
            var path = pair.Key;
            var doc = pair.Value;
            if (doc.Dirty)
            {
                if (w.Files.Contains(path) && (!written.TryGetValue(path, out var revision) || revision != doc.Revision))
                {
                    Io.SaveText(DraftOf(w, path), string.Join("\n", doc.Lines()));
                    written[path] = doc.Revision;
                }
            }
            else if (written.ContainsKey(path))
                Forget(w, path);
        }
    }

    // Deletes the draft for path
    public void Forget(Workspace w, string path)
    {
        written.Remove(path);
        EditorHost.RemoveFile(DraftOf(w, path));
    }

    string DraftOf(Workspace w, string path)
    {
        return dir + "/" + NameOf(w.Relative(path));
    }
}
