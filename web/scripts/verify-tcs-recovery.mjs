// Run against Vite dev with real TCS assets; no player wasm is required.
import assert from "node:assert/strict";
import { launchChromium, closeAfter } from "./lib/browser.mjs";

const base = process.env.LUB_URL || "http://localhost:5173/";
const compilerUrl = new URL("playground/tcs-compiler.ts", base).href;
/** 1 ファイルの probe project。value で版を区別する。 */
const probeFiles = (value) => ({
  "Probe.cs": `public static class Probe { public static int Value() { return ${value}; } }`,
});
/** page 内で session を開き、ok と error 文字列に畳む。 */
const probeOpen = (page, value) =>
  page.evaluate(
    async ({ url, files }) => {
      const compiler = await import(url);
      try {
        const result = await compiler.openTcsSession(files, "Probe");
        return { ok: result.ok, error: result.errors.join("\n") };
      } catch (error) {
        return { ok: false, error: String(error) };
      }
    },
    { url: compilerUrl, files: probeFiles(value) },
  );
const browser = await launchChromium();
const clearDeadline = closeAfter(browser, 120000, "TCS recovery verify");
try {
  // A failed native wasm download must be recoverable without reloading the page.
  const failedPage = await browser.newPage();
  let corrupt = true;
  await failedPage.route("**/dotnet.native.*.wasm", (route) =>
    corrupt
      ? route.fulfill({
          contentType: "application/wasm",
          body: "corrupt fixture",
        })
      : route.continue(),
  );
  await failedPage.goto(new URL("docs.html", base).href);
  assert.equal(
    (await probeOpen(failedPage, 1)).ok,
    false,
    "corrupt native wasm is rejected",
  );
  corrupt = false;
  const retry = await probeOpen(failedPage, 1);
  console.log(
    `${retry.ok ? "PASS" : "FAIL"}: native wasm integrity failure / retry ${retry.error}`,
  );
  await failedPage.close();

  // Managed epochs restart at 1 in a replacement worker. Old JS sessions must
  // remain tied to the original worker, even when the new epoch/path match.
  const page = await browser.newPage();
  await page.goto(new URL("docs.html", base).href);
  await page.evaluate(
    async ({ url, files }) => {
      window.__compiler = await import(url);
      window.__oldSession = (
        await window.__compiler.openTcsSession(files, "Probe")
      ).session;
    },
    { url: compilerUrl, files: probeFiles(1) },
  );
  const worker = page.workers()[0];
  const closed = new Promise((resolve) => worker.once("close", resolve));
  await worker.evaluate(() =>
    setTimeout(() => {
      throw new Error("injected worker crash");
    }, 0),
  );
  await closed;
  const stale = await page.evaluate(
    async ({ files, next }) => {
      const opened = await window.__compiler.openTcsSession(files, "Probe");
      if (!opened.ok) throw new Error(opened.errors.join("\n"));
      try {
        const update = await window.__oldSession.update(
          "Probe.cs",
          next["Probe.cs"],
        );
        return { rejected: !update.ok };
      } catch {
        return { rejected: true };
      }
    },
    { files: probeFiles(2), next: probeFiles(3) },
  );
  assert.ok(
    stale.rejected,
    "a session from a crashed worker cannot mutate the replacement worker",
  );
  console.log(
    "PASS: worker replacement rejects old sessions despite matching managed epoch",
  );
  assert.ok(
    retry.ok,
    `startup recovers after native download failure: ${retry.error}`,
  );
} finally {
  clearDeadline();
  await browser.close();
}
