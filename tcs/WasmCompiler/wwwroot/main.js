// Local smoke test of WasmCompiler: compiles a hello equivalent into a tcs VM module and shows the result.
import { dotnet } from "./_framework/dotnet.js";

const out = document.getElementById("out");
try {
  const { getAssemblyExports, getConfig } = await dotnet.create();
  const exports = await getAssemblyExports(getConfig().mainAssemblyName);
  const res = JSON.parse(
    exports.VmExports.Open(
      JSON.stringify({
        files: {
          "Hello.cs": [
            "public static class Hello",
            "{",
            "    public static int Answer() { return 42; }",
            "}",
          ].join("\n"),
        },
        exports: ["Hello.*"],
      }),
    ),
  );
  out.textContent = res.ok ? `${res.kind}: ${res.module.length} base64 chars` : res.errors.join("\n");
} catch (e) {
  out.textContent = "smoke failed: " + e;
}
