import assert from "node:assert/strict";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { chromium } = require("/Users/fengyinan/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright");

const browser = await chromium.launch({
  headless: true,
  executablePath: "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
});

try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const runtimeErrors = [];
  page.on("pageerror", (error) => runtimeErrors.push(error.message));

  const baseUrl = process.env.EEG_DEMO_URL || "http://127.0.0.1:5173";
  await page.goto(`${baseUrl}/home`, { waitUntil: "networkidle" });
  await page.locator("img.home-background-image").waitFor();
  assert.equal(await page.locator("video").count(), 0, "homepage should use a static background");
  assert.equal(await page.locator("img.home-background-image").evaluate((image) => image.complete && image.naturalWidth > 0), true, "homepage background should load");
  await page.getByRole("button", { name: /开始实验/ }).click();
  await page.getByRole("button", { name: "新建实验", exact: true }).waitFor();
  assert.equal(await page.getByRole("button", { name: "新建实验", exact: true }).getAttribute("class"), "is-active");

  await page.goto(`${baseUrl}/home`, { waitUntil: "networkidle" });
  await page.getByRole("button", { name: /实验记录/ }).click();
  await page.getByRole("table", { name: "实验历史记录" }).waitFor();

  assert.deepEqual(runtimeErrors, [], "homepage interactions should not emit browser errors");
  console.log("home browser verification passed at 1440x900");
} finally {
  await browser.close();
}
