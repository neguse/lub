// .NET 実行の入口。
// tcs VM の player では使わない: lub は Game.csproj 以下の全 *.cs を tcs に渡すので、tcs が定義しない NET シンボルで囲んで空に見せている。
#if NET
// .NET だけで動く入口なので、TinyC# の analyzer の診断 (top-level の args) は当てはまらない
#pragma warning disable TCS1001
return Lub.App.Run(typeof(Game), args);
#endif
