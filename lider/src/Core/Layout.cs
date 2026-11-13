// 配置の規則: スクリーンの上にウィンドウを並べる。
// ウィンドウの順番とスクリーンの大きさから、各ウィンドウの Bounds を決める。
namespace LiderCore;

public interface ILayout
{
    void Arrange(Screen screen);
}

// focus のある (無ければ最初の) ウィンドウだけを、スクリーンいっぱいに出す。ほかのウィンドウは出さない
public class Full : ILayout
{
    public void Arrange(Screen screen)
    {
        var shown = screen.Focus ?? (screen.Windows.Count > 0 ? screen.Windows[0] : null);
        foreach (var window in screen.Windows)
            window.Bounds = window == shown ? new Bounds(0, 0, screen.Width, screen.Height) : null;
    }
}
