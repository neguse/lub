// tcs のセルフホストの compiler (TinyC# で書いた compiler を Lua にしたもの) を、Lua 5.5 の wasm の上で動かす worker。
// .NET / Roslyn の worker (tcs-worker.ts) と同じ要求 (open / update / linkSnapshot / complete / hover) を受ける。
// 違い: reload chunk を作らないので update はいつも requiresRestart (fresh player で受ける)。補完と hover は返さない。
// アセットは npm run gen-tcs-selfhost が tcs-selfhost-assets/ に作る。
import type { TcsRequest } from "./tcs-compiler";

const ASSET_BASE = import.meta.env.BASE_URL + "tcs-selfhost/";
const STUB_URL = import.meta.env.BASE_URL + "cs-lib/lub_stub.cs";
const IMPL_GLOB = import.meta.glob("../../cs-lib/**/*.cs", {
  query: "?raw",
  import: "default",
  eager: true,
}) as Record<string, string>;
const IMPL_SOURCES: Record<string, string> = {};
for (const [path, src] of Object.entries(IMPL_GLOB)) {
  const rel = path.replace(/^.*\/cs-lib\//, "");
  if (rel !== "lub_stub.cs") IMPL_SOURCES[rel] = src;
}

type Tcsc = {
  open(entry: string): void;
  setSource(path: string, text: string): void;
  setReference(path: string, text: string): void;
  setLibrary(path: string, text: string): void;
  compile(): {
    ok: boolean;
    lua: string;
    errors: string[];
    warnings: string[];
    parsed: number;
    revision: number;
  };
};

async function fetchText(url: string): Promise<string> {
  const r = await fetch(url);
  if (!r.ok)
    throw new Error(
      `fetch ${url} -> ${r.status}; run npm run gen-tcs-selfhost`,
    );
  return r.text();
}

let readyPromise: Promise<{
  tcsc: Tcsc;
  prelude: string;
  linq: string;
  stub: string;
}> | null = null;
function ensureRuntime() {
  if (readyPromise) return readyPromise;
  readyPromise = (async () => {
    const [mod, compilerLua, glueLua, prelude, linq, stub] = await Promise.all([
      import(/* @vite-ignore */ ASSET_BASE + "tcsc.mjs"),
      fetchText(ASSET_BASE + "compiler.lua"),
      fetchText(ASSET_BASE + "web.lua"),
      fetchText(ASSET_BASE + "prelude.lua"),
      fetchText(ASSET_BASE + "Linq.cs"),
      fetchText(STUB_URL),
    ]);
    const tcsc = (await mod.createTcsc({ compilerLua, glueLua })) as Tcsc;
    return { tcsc, prelude, linq, stub };
  })();
  return readyPromise;
}

let epoch = 0;
let entryClass = "";
let lastLua: string | null = null;
let revision = 0;

// 出力 (prelude + program + `return Entry`) に、runtime の commit ACK と同じ行を足す。
// player は読み込みが終わった印としてこの行を待つ (module registry の @@tcs_commit と同じ形)。
function snapshot(prelude: string, lua: string): string {
  const ack = `print('@@tcs_commit {"revision":${revision},"ok":true,"commitTimeMs":0}')\n`;
  const at = lua.lastIndexOf("\nreturn ");
  return at >= 0
    ? prelude + lua.slice(0, at + 1) + ack + lua.slice(at + 1)
    : prelude + lua + ack;
}

async function handle(
  method: string,
  args: any[],
): Promise<string | undefined> {
  const { tcsc, prelude, linq, stub } = await ensureRuntime();
  switch (method) {
    case "initialize":
      return undefined;
    case "open": {
      const files: Record<string, string> = { ...IMPL_SOURCES, ...args[0] };
      entryClass = args[1];
      epoch++;
      tcsc.open(entryClass);
      tcsc.setReference("lub_stub.cs", stub);
      tcsc.setLibrary("Linq.cs", linq);
      for (const [path, text] of Object.entries(files))
        tcsc.setSource(path, text);
      const r = tcsc.compile();
      revision = r.revision;
      lastLua = r.ok ? snapshot(prelude, r.lua) : null;
      return JSON.stringify({
        ok: r.ok,
        epoch,
        errors: r.errors,
        warnings: r.warnings,
      });
    }
    case "update": {
      const [e, path, content] = args;
      if (e !== epoch)
        return JSON.stringify({
          ok: false,
          errors: ["C# compiler session expired"],
        });
      tcsc.setSource(path, content);
      const r = tcsc.compile();
      revision = r.revision;
      if (r.ok) lastLua = snapshot(prelude, r.lua);
      return JSON.stringify({
        ok: r.ok,
        fastPath: false,
        requiresRestart: true,
        restartReasons: [
          `self-hosted compiler: rebuilt the program (parsed ${r.parsed} file(s))`,
        ],
        errors: r.errors,
        revision: r.revision,
      });
    }
    case "linkSnapshot":
      if (args[0] !== epoch || lastLua == null)
        return "--@@tcs_error no snapshot";
      return lastLua;
    case "complete":
      return JSON.stringify({ ok: true, items: [] });
    case "hover":
      return JSON.stringify({ ok: true, found: false, start: 0, end: 0 });
  }
  return undefined;
}

const cancelled = new Set<number>();
let queue = Promise.resolve();
self.addEventListener("message", ({ data }: MessageEvent<TcsRequest>) => {
  if (data.method === "cancel") {
    for (const id of data.args) cancelled.add(id);
    return;
  }
  queue = queue.then(async () => {
    const { id, method, args } = data;
    await new Promise((resolve) => setTimeout(resolve, 0));
    if (cancelled.delete(id)) {
      self.postMessage({ id, error: "cancelled" });
      return;
    }
    try {
      self.postMessage({ id, result: await handle(method, args) });
    } catch (error) {
      // runtime の読み込みに失敗した worker は使えない。host が worker を捨てる
      self.postMessage({
        id,
        error: String(error),
        fatal: readyPromise == null || method === "initialize",
      });
    }
  });
});
