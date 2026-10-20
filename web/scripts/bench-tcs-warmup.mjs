// Run against the Vite dev server: LUB_URL=http://localhost:5173/ npm run bench-tcs
// Exercises the real wasm compiler without requiring the player wasm assets.
import assert from "node:assert/strict";
import { chromium } from "playwright";

const base = process.env.LUB_URL || "http://localhost:5173/";
const browser = await chromium.launch({
  ...(process.env.CHROMIUM_PATH
    ? { executablePath: process.env.CHROMIUM_PATH }
    : {}),
  args: ["--no-sandbox"],
});
const deadline = setTimeout(() => {
  console.error("TCS benchmark timed out after 120s");
  void browser.close();
}, 120000);
try {
  const page = await browser.newPage();
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(error.message));
  await page.goto(new URL("docs.html", base).href);
  const result = await page.evaluate(async (compilerUrl) => {
    const { openTcsSession } = await import(compilerUrl);
    const source = `using Lub;
public static class WarmupProbe {
    private static int count = 1;
    public static int Value() { return count; }
}`;
    const file = "WarmupProbe.cs";
    let last = performance.now();
    let maxGapMs = 0;
    let ticks = 0;
    const heartbeat = setInterval(() => {
      const now = performance.now();
      maxGapMs = Math.max(maxGapMs, now - last);
      last = now;
      ticks++;
    }, 10);
    try {
      const start = performance.now();
      const opened = await openTcsSession({ [file]: source }, "WarmupProbe");
      const coldMs = performance.now() - start;
      if (!opened.ok) throw new Error(opened.errors.join("\n"));
      const session = opened.session;
      const snapshot = await session.linkSnapshot();
      const t1 = performance.now();
      const edit = await session.update(
        file,
        source.replace("return count;", "return count + 2;"),
      );
      const updateMs = performance.now() - t1;
      const editedSnapshot = await session.linkSnapshot();
      const invalid = await session.update(
        file,
        source.replace("return count;", "return Missing;"),
      );
      const recovered = await session.update(file, source);
      const speculative = source.replace("return count;", "Gfx. return count;");
      const completions = await session.complete(
        file,
        speculative,
        speculative.indexOf("Gfx.") + 4,
      );
      const hover = await session.hover(
        file,
        source,
        source.indexOf("return count") + 9,
      );
      const t2 = performance.now();
      const switched = await openTcsSession({ [file]: source }, "WarmupProbe");
      const switchMs = performance.now() - t2;
      const stale = await session.update(file, source);
      return {
        coldMs,
        updateMs,
        switchMs,
        maxGapMs,
        ticks,
        snapshot: snapshot?.includes("WarmupProbe"),
        snapshotChanged: snapshot !== editedSnapshot,
        edit,
        invalid,
        recovered,
        completions: completions.map((item) => item.label),
        hover,
        switched: switched.ok,
        stale,
      };
    } finally {
      clearInterval(heartbeat);
    }
  }, new URL("playground/tcs-compiler.ts", base).href);
  assert.deepEqual(pageErrors, []);
  assert.ok(
    result.snapshot && result.snapshotChanged,
    "linked output reflects the edit",
  );
  assert.ok(
    result.edit.ok && result.edit.fastPath,
    "body edit uses incremental compilation",
  );
  assert.ok(
    !result.invalid.ok &&
      result.invalid.errors.some((e) => e.includes("CS0103")),
    "diagnostics cross the worker boundary",
  );
  assert.ok(result.recovered.ok, "valid input recovers from a diagnostic");
  assert.ok(
    result.completions.includes("BeginPass"),
    "completion includes the Lub API",
  );
  assert.ok(
    result.hover.found && result.hover.display.includes("count"),
    "hover resolves a symbol",
  );
  assert.ok(
    result.switched && !result.stale.ok,
    "sample switch rejects the old session",
  );
  assert.ok(
    result.ticks > 0 && result.maxGapMs < 1000,
    "warmup keeps the main thread responsive",
  );
  console.log(
    `TCS cold ${Math.round(result.coldMs)}ms; switch ${Math.round(result.switchMs)}ms; body edit ${Math.round(result.updateMs)}ms; main-thread max timer gap ${Math.round(result.maxGapMs)}ms`,
  );
  console.log(
    "PASS: snapshot, incremental edit, diagnostics/recovery, completion, hover, stale session, main-thread responsiveness",
  );
} finally {
  clearTimeout(deadline);
  await browser.close();
}
