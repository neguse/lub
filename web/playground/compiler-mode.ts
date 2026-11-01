/** `?compiler=selfhost` なら tcs の WebAssembly 版 (tcs2c で C にした tcs) を使う。
 * dotnet の wasm bundle (npm run gen-tcs) が要らない。変換と hot reload は .NET 版と同じで、補完と hover は無い。 */
export const useSelfHostCompiler =
  typeof location !== "undefined" &&
  new URLSearchParams(location.search).get("compiler") === "selfhost";
