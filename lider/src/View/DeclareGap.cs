// lub は宣言の途切れた texture を捨て、次の宣言で空の texture を作る。
// 中身が変わったときだけ描き直す板は、宣言が途切れたら描き直す必要がある。
namespace LiderCore;

public class DeclareGap
{
    // lider の frame の番号 (lider を隠している間も進む)
    public static int Frame;
    int last = -1;

    // 宣言するときに呼ぶ。前の frame に宣言していなければ true
    public bool Lapsed()
    {
        var lapsed = Frame - last > 1;
        last = Frame;
        return lapsed;
    }
}
