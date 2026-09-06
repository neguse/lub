// .NET 実行の入口。tcs→Lua で動かすときは使わない (lub は Game.csproj の
// ディレクトリ直下の *.cs だけを読む)。
return Lub.Run(typeof(Game), args);
