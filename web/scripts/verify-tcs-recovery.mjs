// Run against Vite dev with real TCS assets; no player wasm is required.
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
  void browser.close();
}, 120000);
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
  const open = async (page) =>
    page.evaluate(async (url) => {
      const compiler = await import(url);
      try {
        const result = await compiler.openTcsSession(
          {
            "Probe.cs":
              "public static class Probe { public static int Value() { return 1; } }",
          },
          "Probe",
        );
        return { ok: result.ok, error: result.errors.join("\n") };
      } catch (error) {
        return { ok: false, error: String(error) };
      }
    }, new URL("playground/tcs-compiler.ts", base).href);
  assert.equal(
    (await open(failedPage)).ok,
    false,
    "corrupt native wasm is rejected",
  );
  corrupt = false;
  const retry = await open(failedPage);
  console.log(
    `${retry.ok ? "PASS" : "FAIL"}: native wasm integrity failure / retry ${retry.error}`,
  );
  await failedPage.close();

  // Managed epochs restart at 1 in a replacement worker. Old JS sessions must
  // remain tied to the original worker, even when the new epoch/path match.
  const page = await browser.newPage();
  await page.goto(new URL("docs.html", base).href);
  await page.evaluate(async (url) => {
    window.__compiler = await import(url);
    window.__oldSession = (
      await window.__compiler.openTcsSession(
        {
          "Probe.cs":
            "public static class Probe { public static int Value() { return 1; } }",
        },
        "Probe",
      )
    ).session;
  }, new URL("playground/tcs-compiler.ts", base).href);
  const worker = page.workers()[0];
  const closed = new Promise((resolve) => worker.once("close", resolve));
  await worker.evaluate(() =>
    setTimeout(() => {
      throw new Error("injected worker crash");
    }, 0),
  );
  await closed;
  const stale = await page.evaluate(async () => {
    const opened = await window.__compiler.openTcsSession(
      {
        "Probe.cs":
          "public static class Probe { public static int Value() { return 2; } }",
      },
      "Probe",
    );
    if (!opened.ok) throw new Error(opened.errors.join("\n"));
    try {
      const update = await window.__oldSession.update(
        "Probe.cs",
        "public static class Probe { public static int Value() { return 3; } }",
      );
      return { rejected: !update.ok };
    } catch {
      return { rejected: true };
    }
  });
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
  clearTimeout(deadline);
  await browser.close();
}
