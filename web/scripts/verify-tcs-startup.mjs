// Frontend startup/queue regression tests. Compiler and player are controlled
// fixtures; bench-tcs separately verifies the real wasm compiler.
import assert from "node:assert/strict";
import { chromium } from "playwright";

const base = process.env.LUB_URL || "http://localhost:5173/";
const browser = await chromium.launch({
  ...(process.env.CHROMIUM_PATH
    ? { executablePath: process.env.CHROMIUM_PATH }
    : {}),
  args: ["--no-sandbox"],
});
try {
  for (const mode of [
    "cold-edit",
    "cold-failure",
    "cold-failure-queued",
    "warm-failure-queued",
    "warm-edit",
    "warm-recovery",
    "restart-failure",
  ]) {
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.addInitScript(() => {
      window.__startupMessages = [];
      addEventListener("message", ({ data }) => {
        if (data.type === "startupFixture") window.__startupMessages.push(data);
      });
    });
    await page.route("**/tcs-compiler.ts*", (route) =>
      route.fulfill({
        contentType: "application/javascript",
        body: `
        let opens = 0;
        export async function preloadTcsRuntime() {}
        export async function openTcsSession(files) {
          window.__startupOpens = ++opens;
          await new Promise(r => setTimeout(r, 350));
          if (${JSON.stringify(mode)}.includes("failure") && ${JSON.stringify(mode)} !== "restart-failure" && opens === 1)
            throw new Error("injected startup failure");
          let source = Object.values(files).join("\\n"), revision = 1, updates = 0;
          return {ok:true,errors:[],warnings:[],session:{
            async update(_path, content) {
              if (${JSON.stringify(mode)} === "warm-recovery" && ++updates === 2) throw new Error("injected update failure");
              source=content; return {ok:true,revision:++revision,errors:[],requiresRestart:${JSON.stringify(mode)} === "restart-failure" && ++updates === 2,restartReasons:[]};
            },
            async linkSnapshot() { return "-- fixture revision=" + revision + "\\n" + source; },
            async complete() { return []; },
            async hover() { return {found:false}; }
          }};
        }
      `,
      }),
    );
    await page.route("**/tcs-prebuilt/*.lua", (route) =>
      route.fulfill({
        status:
          mode.startsWith("warm-") || mode === "restart-failure" ? 200 : 404,
        body: "-- fixture prebuilt",
      }),
    );
    let playerCount = 0;
    await page.route("**/player.html*", (route) => {
      const fail = mode === "restart-failure" && ++playerCount === 2;
      return route.fulfill({
        contentType: "text/html",
        body: `<script>
        addEventListener('message',({data})=>{
          if(!data.files)return;
          parent.postMessage({type:'startupFixture',operation:data.type,files:data.files},'*');
          const revision=Number(Object.values(data.files).join('').match(/fixture revision=(\\d+)/)?.[1]);
          if(revision)parent.postMessage({type:'log',msg:'@@tcs_commit '+JSON.stringify({revision,ok:${!fail},commitTimeMs:0})},'*');
        });
        setTimeout(()=>parent.postMessage({type:'playerReady'},'*'),0);
      </script>`,
      });
    });
    await page.goto(base);
    await page.waitForFunction(() => window.__startupOpens === 1);
    if (mode === "cold-failure") {
      await page.waitForFunction(() =>
        document
          .querySelector("#log")
          .textContent.includes("injected startup failure"),
      );
    }
    await page.evaluate(() => {
      const hook = window.__lubTest;
      const path = hook.listFiles().find((p) => p.endsWith(".cs"));
      hook.replaceContent(
        path,
        hook.getContent(path) + "\n// startup queued edit",
      );
    });
    await page.waitForFunction(
      () =>
        window.__startupMessages.some((m) =>
          Object.values(m.files).some((text) =>
            text.includes("startup queued edit"),
          ),
        ),
      null,
      { timeout: 4000 },
    );
    assert.ok(await page.evaluate(() => window.__lubTest.csQuery.ready()));
    if (mode === "warm-recovery" || mode === "restart-failure") {
      await page.evaluate(() => {
        const hook = window.__lubTest;
        const path = hook.listFiles().find((p) => p.endsWith(".cs"));
        hook.replaceContent(
          path,
          hook.getContent(path) + "\n// rejected update",
        );
      });
      await page.waitForFunction(
        (mode) =>
          document.querySelector("#status").textContent ===
          (mode === "warm-recovery"
            ? "compile error"
            : "restart failed (old player kept)"),
        mode,
      );
      await page.evaluate(() => {
        const hook = window.__lubTest;
        const path = hook.listFiles().find((p) => p.endsWith(".cs"));
        hook.replaceContent(
          path,
          hook.getContent(path) + "\n// recovered update",
        );
      });
      await page.waitForFunction(
        () =>
          window.__startupMessages.some(
            (m) =>
              m.operation === "setFiles" &&
              Object.values(m.files).some((text) =>
                text.includes("recovered update"),
              ),
          ),
        null,
        { timeout: 4000 },
      );
    }
    console.log(`PASS: ${mode}`);
    await context.close();
  }
} finally {
  await browser.close();
}
