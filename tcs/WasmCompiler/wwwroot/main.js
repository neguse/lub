// WasmCompiler の手元スモーク: hello 相当を compile して Lua を表示する。
import { dotnet } from "./_framework/dotnet.js";

const out = document.getElementById("out");
try {
  const { getAssemblyExports, getConfig } = await dotnet.create();
  const exports = await getAssemblyExports(getConfig().mainAssemblyName);
  const res = JSON.parse(
    exports.CompilerExports.Compile(
      JSON.stringify({
        files: {
          "Hello.cs": [
            "public static class Hello",
            "{",
            "    public static int Answer() { return 42; }",
            "}",
          ].join("\n"),
        },
        entryClass: "Hello",
        checkNaming: false,
      }),
    ),
  );
  out.textContent = res.ok ? res.lua : res.errors.join("\n");
} catch (e) {
  out.textContent = "smoke failed: " + e;
}
