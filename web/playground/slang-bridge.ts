// Loads @shader-slang/slang-wasm (vendored at /public/slang/) and exposes
// window.slangCompile so the C side's EM_ASYNC_JS bridge can drive it.
//
// Contract (defined in src/shader.cpp):
//   window.slangCompile(src: string, entry: string, stage: 0|1|2)
//     => Promise<{wgsl: string, reflectJson: string} | {error: string}>
//   stage: 0 = vertex, 1 = fragment, 2 = compute
//
// We pin to the GitHub release shipped via `web/scripts/fetch-slang-wasm.sh`.
// The package isn't on npm — slang-playground vendors the same release.
//
// Slang stage codes match SlangCompiler.SLANG_STAGE_* in slang-playground:
//   1 = vertex, 5 = fragment, 6 = compute.
//
// API discovery notes (from interface.d.ts shipped in the release zip):
//   - default export is `MainModuleFactory(options?) => Promise<MainModule>`.
//   - MainModule.createGlobalSession() => GlobalSession
//   - MainModule.getCompileTargets() => [{name: "WGSL"|"SPIRV"|..., value: int}]
//   - globalSession.createSession(targetValueNumber) => Session
//   - session.loadModuleFromSource(src, modName, path) => Module
//   - module.findAndCheckEntryPoint(name, stage) => EntryPoint
//   - session.createCompositeComponentType([module, entryPoint]) => ComponentType
//   - composite.link() => ComponentType (linked program)
//   - linked.getEntryPointCode(0, 0) => string (WGSL code)
//   - linked.getLayout(0).toJsonObject() => reflection object
//
// Locator policy: we use the WASM file co-located with slang-wasm.js by
// telling Emscripten to use a relative URL via `locateFile`. The .wasm sits
// next to the .js in /slang/. Without this, emdawnwebgpu's resolver looks
// for slang-wasm.wasm relative to the page (player.html) which would
// 404.

type StageCode = 0 | 1 | 2;

// Slang's internal stage enum (matches slang.h SLANG_STAGE_*).
const SLANG_STAGE_VERTEX = 1;
const SLANG_STAGE_FRAGMENT = 5;
const SLANG_STAGE_COMPUTE = 6;

function stageToSlang(stage: StageCode): number {
  switch (stage) {
    case 0:
      return SLANG_STAGE_VERTEX;
    case 1:
      return SLANG_STAGE_FRAGMENT;
    case 2:
      return SLANG_STAGE_COMPUTE;
    default:
      throw new Error(`unknown stage code ${stage}`);
  }
}

// MainModule is loaded lazily so missing files (forgot to run fetch script)
// don't crash module evaluation. Errors surface through window.slangCompile's
// {error} response and reach the user as a Slang diagnostic.
let slangModulePromise: Promise<any> | null = null;
let globalSession: any = null;
let wgslTarget: number = -1;
// Session shared across compile calls. See compileOne() for the rationale.
let sharedSession: any = null;
// Monotonic counter used to give each loadModuleFromSource() a unique module
// name. Slang caches modules within a session by name; if we re-use a name the
// stale module is returned and a fresh entry-point lookup fails.
let moduleSeq: number = 0;

async function loadSlangModule(): Promise<any> {
  if (slangModulePromise) return slangModulePromise;
  slangModulePromise = (async () => {
    // Dynamic import keeps slang-wasm.js out of the main editor bundle and
    // skips Rollup's static-import resolver entirely. We sneak the URL
    // through `new Function` so Vite/Rollup never see it as an `import`
    // expression at build time. At runtime the browser loads it from the
    // vendored copy in /public/slang/.
    const importer = new Function("u", "return import(u)");
    const mod = (await importer("/slang/slang-wasm.js")) as any;
    const factory = mod.default;
    if (typeof factory !== "function") {
      throw new Error("slang-wasm.js did not provide a default Module factory");
    }
    const main = await factory({
      // emscripten locator: maps slang-wasm.wasm -> /slang/slang-wasm.wasm.
      // Without this it would default to a path relative to the HTML doc.
      locateFile: (path: string) => {
        if (path.endsWith(".wasm")) return "/slang/" + path;
        return path;
      },
    });
    return main;
  })();
  return slangModulePromise;
}

export async function initSlang(): Promise<void> {
  const main = await loadSlangModule();
  globalSession = main.createGlobalSession();
  if (!globalSession) {
    const err = main.getLastError?.();
    throw new Error(
      "Slang createGlobalSession failed: " + (err?.message ?? "<unknown>"),
    );
  }
  // Look up the integer code for "WGSL" once and cache it.
  const targets = main.getCompileTargets() as { name: string; value: number }[];
  for (const t of targets) {
    if (t.name === "WGSL") {
      wgslTarget = t.value;
      break;
    }
  }
  if (wgslTarget < 0) {
    throw new Error(
      "Slang has no WGSL target (targets: " +
        targets.map((t) => t.name).join(",") +
        ")",
    );
  }

  (window as any).slangCompile = async (
    src: string,
    entry: string,
    stage: StageCode,
  ) => {
    return compileOne(main, src, entry, stage);
  };
}

// Patch WGSL group numbers so Slang's "everything in @group(0)" layout
// matches sokol-gfx's WGPU convention:
//   * uniform        -> @group(0)   (unchanged)
//   * texture_*      -> @group(1)
//   * sampler*       -> @group(1)
//   * var<storage,*> -> @group(1)
//   * texture_storage_* (storage image) -> @group(1)
//
// Each global `@binding(B) @group(G) var<...> name : ty;` declaration is a
// separate line in Slang's WGSL output. We do a single-pass regex over the
// `@group(N) var ...` form, deciding the new group from the inferred kind:
//
//   - "var<uniform>"      -> uniform block, keep group 0
//   - "var<storage, ..>"  -> storage buffer, move to group 1
//   - "var ... : sampler" or " : sampler_comparison" -> sampler, group 1
//   - "var ... : texture_..." -> texture or storage image, group 1
//   - anything else (workgroup, private, function-scope) untouched.
//
// Regex (multiline, single line per @binding declaration):
//   ^(\s*)@binding\(\d+\)\s+@group\((\d+)\)\s+var(?:<[^>]+>)?\s+[A-Za-z_]\w*\s*:\s*([A-Za-z_]\w*)
// We rewrite the group number IN PLACE, preserving every other character so
// line numbers / column counts in error messages stay aligned.
//
// Returns the rewritten WGSL plus the number of matched declarations so the
// caller can sanity-check it against the reflection's resource count and warn
// when Slang emits a declaration form the regex doesn't recognise.
function remapWgslGroups(src: string): { wgsl: string; remapped: number } {
  // Match group declarations on a single line. We accept (and leave alone)
  // any spacing variations Slang might use.
  const re =
    /@binding\((\d+)\)\s+@group\((\d+)\)\s+var(<[^>]*>)?(\s+[A-Za-z_]\w*\s*:\s*[A-Za-z_]\w*)/g;
  let count = 0;
  const wgsl = src.replace(re, (full, _bind, _grp, varTpl, tail) => {
    count++;
    // varTpl is the optional <...> after `var`. tail is `  name : type_id`.
    const tpl = (varTpl ?? "").toLowerCase();
    const tail_l = (tail ?? "").toLowerCase();
    // Type id sits after the colon in `tail`.
    const colon = tail_l.lastIndexOf(":");
    const typeId = colon >= 0 ? tail_l.slice(colon + 1).trim() : "";

    // Default: keep whatever group Slang assigned.
    let newGroup = _grp;
    if (tpl.startsWith("<uniform")) {
      newGroup = "0";
    } else if (tpl.startsWith("<storage")) {
      newGroup = "1";
    } else if (
      typeId.startsWith("texture_") ||
      typeId === "sampler" ||
      typeId === "sampler_comparison"
    ) {
      newGroup = "1";
    }
    // Reconstruct the prefix; we only swap the @group(N) literal.
    return full.replace(/@group\(\d+\)/, `@group(${newGroup})`);
  });
  return { wgsl, remapped: count };
}

// Count the resources reflected by Slang that we expect remapWgslGroups to
// have rewritten. We compare this against the regex's match count so a future
// Slang release that emits, say, a multi-line @binding declaration triggers a
// loud warning rather than silently mis-grouping resources at runtime.
//
// Schema (Slang WASM v2026.8.1 reflection):
//   { parameters: [
//       { binding: { kind: "constantBuffer"|"shaderResource"|
//                          "sampler"|"unorderedAccess"|"descriptorTableSlot",
//                    index: int, space: int },
//         type:    { kind, ... },
//         ... },
//       ...
//     ],
//     ... }
// kinds that map to a @binding @group declaration in WGSL: every kind above
// except function parameters and entry-point varyings (those aren't in
// `parameters`). The simplest robust count is `parameters.length` when the
// field is present.
function expectedResourceCount(reflectObj: any): number {
  const params = reflectObj?.parameters;
  if (!Array.isArray(params)) return -1;
  return params.length;
}

async function compileOne(
  main: any,
  src: string,
  entry: string,
  stage: StageCode,
): Promise<{ wgsl: string; reflectJson: string } | { error: string }> {
  // Slang's embind bindings hand out ClassHandle objects for every long-lived
  // structure (module / entry point / composite / linked program / layout).
  // The C++ side reference-counts them and only frees on .delete(). Without
  // explicit deletes we leak ~MB per compile, which adds up fast during a
  // hot-reload editing session. Track everything and clean it up in a
  // try/finally so exceptions still trigger release.
  //
  // Phase 8 note (slang-wasm v2026.8.1 / Emscripten): destroying a per-compile
  // Session after building / discarding ~3 sessions aborts the wasm runtime
  // ("Aborted(native code called abort()) ... unreachable"). The failing case
  // in our suite is sample 06 (`06_gbuffer.vs.slang`) — third compile after
  // page load, with three vertex-input attributes — but the abort itself is
  // in Slang's internal session teardown, not in shader parsing. Once
  // aborted, every subsequent slang call throws "memory access out of bounds"
  // because the runtime is dead. Workaround: share a single session across
  // every compile and never delete it; give each module a unique name so
  // Slang's per-session module cache doesn't return a stale handle on the
  // next compile. Stress-tested at 5 passes × 8 compiles in Node with
  // slang-wasm v2026.8.1 (see /tmp probes used during Phase 8).
  let userModule: any = null;
  let entryPoint: any = null;
  let composite: any = null;
  let linked: any = null;
  let layout: any = null;
  try {
    if (!sharedSession) {
      sharedSession = globalSession.createSession(wgslTarget);
      if (!sharedSession) {
        const err = main.getLastError?.();
        return {
          error: "createSession failed: " + (err?.message ?? "<unknown>"),
        };
      }
    }
    // Module name + path must be unique per call: Slang's session keeps a
    // module cache keyed by name, and reusing a name returns the cached one
    // (whose entry-point lookups would fail for a different shader source).
    const modName = "user_" + moduleSeq++;
    userModule = sharedSession.loadModuleFromSource(
      src,
      modName,
      modName + ".slang",
    );
    if (!userModule) {
      const err = main.getLastError?.();
      return {
        error: "loadModuleFromSource failed: " + (err?.message ?? "<unknown>"),
      };
    }
    const slangStage = stageToSlang(stage);
    entryPoint = userModule.findAndCheckEntryPoint(entry, slangStage);
    if (!entryPoint) {
      const err = main.getLastError?.();
      return {
        error:
          "findAndCheckEntryPoint(" +
          entry +
          ") failed: " +
          (err?.message ?? "<unknown>"),
      };
    }
    composite = sharedSession.createCompositeComponentType([
      userModule,
      entryPoint,
    ]);
    if (!composite) {
      const err = main.getLastError?.();
      return {
        error:
          "createCompositeComponentType failed: " +
          (err?.message ?? "<unknown>"),
      };
    }
    linked = composite.link();
    if (!linked) {
      const err = main.getLastError?.();
      return { error: "link failed: " + (err?.message ?? "<unknown>") };
    }
    // getEntryPointCode(entryPointIndex=0, targetIndex=0). With a single-entry
    // composite, index 0 always corresponds to the entry we just added.
    const rawWgsl: string = linked.getEntryPointCode(0, 0);
    if (!rawWgsl || rawWgsl === "Error" || rawWgsl.length === 0) {
      const err = main.getLastError?.();
      return {
        error:
          "getEntryPointCode returned empty: " + (err?.message ?? "<unknown>"),
      };
    }
    // Sokol-gfx's WGPU backend expects UBs in @group(0) and textures /
    // samplers / storage buffers in @group(1). Slang emits everything in
    // @group(0). Patch the WGSL declarations so the @binding numbers stay
    // intact but resources land in their expected groups.
    const { wgsl: groupWgsl, remapped } = remapWgslGroups(rawWgsl);
    // WebGPU core only supports write-only storage textures. Slang emits
    // read_write for RWTexture2D; downgrade to write so the shader matches
    // sokol's writeonly bind-group layout entry.
    const wgsl = groupWgsl.replace(
      /texture_storage_(\w+)<([^,]+),\s*read_write>/g,
      "texture_storage_$1<$2, write>",
    );
    let reflectObj: any = {};
    try {
      layout = linked.getLayout(0);
      if (layout) reflectObj = layout.toJsonObject() ?? {};
    } catch (e) {
      // Reflection failure is non-fatal — we still got WGSL. The reflection
      // mapper degrades gracefully on missing fields.
      console.warn("[slang-bridge] reflection extraction threw:", e);
    }
    const reflectJson = JSON.stringify(reflectObj);
    // Sanity-check: if reflection says there ARE resources but the regex
    // matched zero or fewer-than-expected declarations, Slang likely emitted
    // a form we don't recognise (e.g. multi-line @binding or a new resource
    // kind). Warn so future maintainers see it before runtime breakage.
    const expected = expectedResourceCount(reflectObj);
    if (expected > 0 && remapped < expected) {
      console.warn(
        "[slang-bridge] WGSL group remap matched",
        remapped,
        "declarations but reflection reports",
        expected,
        "parameters; group rewrite may have missed a form. WGSL:\n",
        wgsl,
      );
    }
    // Debug: dump the first compile's reflection so a maintainer can see
    // the schema in the browser console. Toggle via the URL hash.
    if (location.hash.includes("debug-slang")) {
      console.log("[slang-bridge] WGSL for", entry, "\n", wgsl);
      console.log("[slang-bridge] reflection for", entry, "\n", reflectJson);
    }
    return { wgsl, reflectJson };
  } catch (e: any) {
    return { error: "slangCompile threw: " + (e?.message ?? String(e)) };
  } finally {
    // Slang's WASM bindings track lifetimes via embind. Release everything we
    // allocated, in reverse construction order. The optional-chained
    // `.delete?.()` is defensive — some return values might not be
    // ClassHandles in older Slang versions, and we don't want a missing
    // method to take down the cleanup chain.
    //
    // NB: sharedSession is intentionally NOT deleted here — see the long
    // comment at the top of compileOne(). Deleting it after a few compiles
    // aborts the wasm runtime in slang-wasm v2026.8.1.
    try {
      layout?.delete?.();
    } catch {}
    try {
      linked?.delete?.();
    } catch {}
    try {
      composite?.delete?.();
    } catch {}
    try {
      entryPoint?.delete?.();
    } catch {}
    try {
      userModule?.delete?.();
    } catch {}
  }
}
