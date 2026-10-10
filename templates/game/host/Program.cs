// .NET 実行の入口。tcs→Lua では使わない: lub は Game.csproj 以下の全 *.cs を
// tcs に渡すので、tcs が定義しない NET シンボルで囲んで空に見せている。
#if NET
return Lub.App.Run(typeof(Game), args);
#endif
