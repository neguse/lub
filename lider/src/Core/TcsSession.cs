// 名前を付けた tcs の増分 session (tcs/SelfHost/Tool/Sessions.cs)。lider の player に組み込んだ tcs (Tcs.ref.cs) で開く。
// 同じ program の別の版 (保存した版と編集中の版など) を、名前を変えて並べて変換できる。
// 変換が通ると、開いたときに指した path に module (.tcsb) か batch (差分) を書く。ゲームの app (App.Use) はそれを読む。
// 頼んだ変換の結果は Take で受け取る。
// 今は頼んだその場で変換する (その frame は変換の間だけ止まる) ので、頼んだ後の Take で必ず受け取れる。
using System.Collections.Generic;

namespace LiderCore;

// 変換の 1 回の結果
public class TcsResult
{
    // module (最初の build)、batch (差分を当てる)、restart (module を読み直す)、error (最後に通った版のまま)
    public string Kind = "";
    // 通った回数
    public int Revision;
    // 診断 (Kind が error のとき)
    public readonly List<string> Errors = new List<string>();
    // 作り直す理由 (Kind が restart のとき)
    public readonly List<string> Reasons = new List<string>();
    // 当てたが知らせたい変化 (Kind が batch のとき)
    public readonly List<string> Notes = new List<string>();

    public static TcsResult Parse(string reply)
    {
        var r = new TcsResult();
        var first = true;
        foreach (var line in reply.Split('\n'))
        {
            if (line == "")
                continue;
            if (first)
            {
                r.Kind = line;
                first = false;
            }
            else if (line.StartsWith("revision ") && int.TryParse(Utf8.Sub(line, 9), out var n))
                r.Revision = n;
            else if (line.StartsWith("error "))
                r.Errors.Add(Utf8.Sub(line, 6));
            else if (line.StartsWith("reason "))
                r.Reasons.Add(Utf8.Sub(line, 7));
            else if (line.StartsWith("note "))
                r.Notes.Add(Utf8.Sub(line, 5));
        }
        return r;
    }
}

public class TcsSession
{
    public readonly string Name;
    readonly List<TcsResult> done = new List<TcsResult>();

    public TcsSession(string name)
    {
        Name = name;
    }

    // session を開き (同じ名前なら開き直し)、files を変換する。
    // entry は entry class、modulePath と batchPath は結果を書く path、exports は entry の他に作る関数 (C.M か C.*)、traces は --trace の path の先頭、refs は参照 (変換しない宣言) の本文。
    // paths と texts は変換する file の path と本文 (同じ順)
    public void Open(string entry, string modulePath, string batchPath, List<string> exports, List<string> traces, List<string> refs,
        List<string> paths, List<string> texts)
    {
        Tcs.Arg("open");
        Tcs.Arg(Name);
        Tcs.Arg(entry);
        Tcs.Arg(modulePath);
        Tcs.Arg(batchPath);
        ArgList(exports);
        ArgList(traces);
        ArgList(refs);
        for (var i = 0; i < paths.Count && i < texts.Count; i++)
        {
            Tcs.Arg(paths[i]);
            Tcs.Arg(texts[i]);
        }
        Call();
    }

    // file を足すか、本文を替えて変換する
    public void Update(string path, string text)
    {
        Tcs.Arg("update");
        Tcs.Arg(Name);
        Tcs.Arg(path);
        Tcs.Arg(text);
        Call();
    }

    // file を外して変換する
    public void Remove(string path)
    {
        Tcs.Arg("remove");
        Tcs.Arg(Name);
        Tcs.Arg(path);
        Call();
    }

    public void Close()
    {
        Tcs.Arg("close");
        Tcs.Arg(Name);
        Tcs.Call(out _);
        done.Clear();
    }

    // 頼んだ順に、終わった変換の結果を受け取る (受け取った物は消える)
    public List<TcsResult> Take()
    {
        var taken = new List<TcsResult>(done);
        done.Clear();
        return taken;
    }

    static void ArgList(List<string> items)
    {
        Tcs.Arg("" + items.Count);
        foreach (var item in items)
            Tcs.Arg(item);
    }

    void Call()
    {
        if (Tcs.Call(out var reply) && reply != null)
            done.Add(TcsResult.Parse(reply));
        else
        {
            var failed = new TcsResult { Kind = "error" };
            failed.Errors.Add("the tcs compiler is not in this player");
            done.Add(failed);
        }
    }
}
