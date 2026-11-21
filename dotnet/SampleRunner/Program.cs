// サンプルの entry class (static OnFrame を持つ唯一の class) を探して回す。
// tests/cs (-p:Tests=true) は entry class が多いので、--entry CLASS で選ぶ。
return Lub.App.Run(typeof(Program).Assembly, args);
