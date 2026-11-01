/** playground の C# の変換は既定で tcs の wasm (tcs2c で C にした tcs) が行い、補完と hover は .NET の wasm が答える。
 * `?compiler=dotnet` なら変換も .NET の wasm で行う (同じ tcs の実装を .NET で動かしたもの。tcs-prebuilt の cold 起動を使う)。 */
export const useDotnetCompiler =
  typeof location !== "undefined" &&
  new URLSearchParams(location.search).get("compiler") === "dotnet";
