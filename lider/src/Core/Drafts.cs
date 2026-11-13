// 保存していない本文を下書きの dir に書き、次に起動したら保存していないまま窓に戻す。
// 下書きは file でないので、ゲームには入らない。env LIDER_DRAFTS が dir (無ければ下書きを作らない)
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

public class Drafts
{
    public const float WriteSeconds = 1;

    readonly string dir;
    // 書いた下書きの file の path と、書いた時の Document.Revision
    readonly Dictionary<string, int> written = new Dictionary<string, int>();
    float since;

    public Drafts(string dir)
    {
        this.dir = dir;
    }

    // dir は 1 段なので、Root からの path の '/' を %2F にした名前で置く
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

    // path の下書きを消す
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
