/** `?compiler=selfhost` なら tcs のセルフホストの compiler (Lua の wasm) を使う。
 * dotnet の wasm bundle (npm run gen-tcs) が要らない。補完と hover は無く、編集はいつも fresh player で受ける。 */
export const useSelfHostCompiler =
  typeof location !== "undefined" &&
  new URLSearchParams(location.search).get("compiler") === "selfhost";
