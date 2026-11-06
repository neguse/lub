// 大きな配列の一部を tvm_inspect で見るテスト (inspect_test.c が呼ぶ)。
public class Box
{
    public int Hp = 7;
}

public static class Game
{
    public static Box[] Boxes = Make();

    static Box[] Make()
    {
        var a = new Box[1 << 20];
        a[(1 << 20) - 1] = new Box();
        return a;
    }
}
