import { mkdir, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(scriptDirectory, "..");
const projectRequire = createRequire(path.join(projectRoot, "package.json"));
const viteRequire = createRequire(projectRequire.resolve("vite"));
const { build } = viteRequire("esbuild");
const outputDirectory = path.join(projectRoot, "deliverables");
const outputFile = path.join(
  outputDirectory,
  "envelope-tacs-single-stimulation-prototype.html",
);

const entry = `
  import React from "react";
  import { createRoot } from "react-dom/client";
  import { EnvelopeTacsPrototype } from "./src/components/EnvelopeTacsPrototype.jsx";

  createRoot(document.getElementById("root")).render(
    <EnvelopeTacsPrototype
      onHome={() => window.alert("这是独立流程原型文件，不连接项目首页。")}
    />,
  );
`;

const buildResult = await build({
  absWorkingDir: projectRoot,
  stdin: {
    contents: entry,
    loader: "jsx",
    resolveDir: projectRoot,
    sourcefile: "envelope-tacs-standalone-entry.jsx",
  },
  bundle: true,
  write: false,
  outfile: "standalone.js",
  format: "iife",
  platform: "browser",
  target: ["chrome90", "safari15", "firefox90"],
  jsx: "automatic",
  minify: true,
  legalComments: "none",
  define: {
    "process.env.NODE_ENV": '"production"',
  },
});

const javascript = buildResult.outputFiles.find((file) => file.path.endsWith(".js"));
const stylesheet = buildResult.outputFiles.find((file) => file.path.endsWith(".css"));

if (!javascript || !stylesheet) {
  throw new Error("离线原型打包失败：未同时生成 JavaScript 和 CSS。 ");
}

const html = `<!doctype html>
<html lang="zh-CN">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <meta name="color-scheme" content="light" />
    <title>包络-tACS 单刺激言语训练 · 流程原型</title>
    <style>${stylesheet.text}</style>
  </head>
  <body style="margin:0">
    <div id="root"></div>
    <script>${javascript.text.replaceAll("</script", "<\\/script")}</script>
  </body>
</html>
`;

await mkdir(outputDirectory, { recursive: true });
await writeFile(outputFile, html, "utf8");

console.log(outputFile);
