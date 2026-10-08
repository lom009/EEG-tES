import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(scriptDirectory, "..");
const projectRequire = createRequire(path.join(projectRoot, "package.json"));
const viteRequire = createRequire(projectRequire.resolve("vite"));
const { build } = viteRequire("esbuild");
const outputDirectory = path.join(projectRoot, "deliverables");
const outputFile = path.join(outputDirectory, "single-stimulation-figma-demo.html");
const assetDirectory = path.join(projectRoot, "public", "assets", "single-stimulation");
const mimeTypes = { ".svg":"image/svg+xml", ".png":"image/png", ".jpg":"image/jpeg", ".jpeg":"image/jpeg", ".wav":"audio/wav" };

const embeddedAssets = {};
for (const name of await readdir(assetDirectory)) {
  const mime = mimeTypes[path.extname(name).toLowerCase()];
  if (!mime) continue;
  const data = await readFile(path.join(assetDirectory,name));
  embeddedAssets[name] = `data:${mime};base64,${data.toString("base64")}`;
}

const entry = `
  import React from "react";
  import { createRoot } from "react-dom/client";
  import { SingleStimulationDemo } from "./src/components/SingleStimulationDemo.jsx";
  window.__SINGLE_STIM_ASSETS__ = ${JSON.stringify(embeddedAssets)};
  createRoot(document.getElementById("root")).render(<SingleStimulationDemo />);
`;

const buildResult = await build({
  absWorkingDir: projectRoot,
  stdin: { contents:entry, loader:"jsx", resolveDir:projectRoot, sourcefile:"single-stimulation-standalone-entry.jsx" },
  bundle:true,
  external:["/assets/*"],
  write:false,
  outfile:"standalone.js",
  format:"iife",
  platform:"browser",
  target:["chrome90","safari15","firefox90"],
  jsx:"automatic",
  minify:true,
  legalComments:"none",
  define:{ "process.env.NODE_ENV":'"production"' },
});

const javascript = buildResult.outputFiles.find(file=>file.path.endsWith(".js"));
const stylesheet = buildResult.outputFiles.find(file=>file.path.endsWith(".css"));
if (!javascript || !stylesheet) throw new Error("离线原型打包失败：未同时生成 JavaScript 和 CSS。");

const css = stylesheet.text.replaceAll("/assets/single-stimulation/49316.svg",embeddedAssets["49316.svg"]);
const html = `<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width,initial-scale=1" />
  <meta name="color-scheme" content="light" />
  <title>EEG-tES · 包络-tACS 单刺激演示</title>
  <style>${css}</style>
</head>
<body style="margin:0"><div id="root"></div><script>${javascript.text.replaceAll("</script","<\\/script")}</script></body>
</html>`;

await mkdir(outputDirectory,{recursive:true});
await writeFile(outputFile,html,"utf8");
console.log(outputFile);
