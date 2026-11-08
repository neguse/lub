using System.Collections.Generic;

namespace TcsSelf;

// IL の組み立ての文脈。IL を使う側 (IlExporter) が型と member に入るたびに設定し、IlBuild が読む
public sealed class IlContext
{
    public readonly IlBuild B;
    // IL を C backend に渡す経路は引数の copy 地点を残す (C は値渡しで copy が要らない)
    public bool KeepsArgumentCopies;
    // 組み立て中の型
    public TypeDeclSym? CurrentDecl;
    // generic な型の instance の member は型引数の既定値を self から読む
    public bool TypeArgDefaultsOnSelf;
    // 組み立て中の method (式本体が void かを見る)
    public Symbol? CurrentFunction;
    // 組み立て中の lambda の戻りが void か (0 = lambda の外、1 = void、2 = 値)
    public int CurrentLambdaVoid;
    // IL にできなかった形の記録 (調べるため)
    public List<string> InternalErrors = new List<string>();

    public IlContext()
    {
        B = new IlBuild(this);
    }

    public void Internal(string message)
    {
        InternalErrors.Add(message);
    }
}
