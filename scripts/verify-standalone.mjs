import fs from "node:fs";
import { execFileSync } from "node:child_process";

const htmlPath = new URL("../EEG-tES-本地演示.html", import.meta.url);
const html = fs.readFileSync(htmlPath, "utf8");

if (html.includes('src="/assets/') || html.includes('href="/assets/')) {
  throw new Error("单文件中仍存在外部资源引用。");
}

const chrome = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const rendered = execFileSync(
  chrome,
  ["--headless", "--disable-gpu", "--no-sandbox", "--dump-dom", htmlPath.href],
  { encoding: "utf8", maxBuffer: 30 * 1024 * 1024 },
);

const requiredMarkers = ["setup-shell", "新建实验", "历史记录"];
const missing = requiredMarkers.filter((marker) => !rendered.includes(marker));

if (missing.length) {
  throw new Error(`Chrome 渲染后缺少页面标记：${missing.join("、")}`);
}

console.log("单文件资源检查：通过");
console.log("Chrome file:// 渲染检查：通过");
console.log(`HTML 大小：${(fs.statSync(htmlPath).size / 1024 / 1024).toFixed(2)} MB`);
