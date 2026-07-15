import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const distDir = path.join(root, "dist");
const assetsDir = path.join(distDir, "assets");
const outputFile = path.join(root, "EEG-tES-本地演示.html");

const mimeTypes = {
  ".css": "text/css",
  ".glb": "model/gltf-binary",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".js": "text/javascript",
  ".png": "image/png",
  ".svg": "image/svg+xml",
  ".webp": "image/webp",
};

function asDataUrl(filePath) {
  const mime = mimeTypes[path.extname(filePath).toLowerCase()] ?? "application/octet-stream";
  return `data:${mime};base64,${fs.readFileSync(filePath).toString("base64")}`;
}

function inlineAssetReferences(source) {
  return source.replace(/\/assets\/([A-Za-z0-9._-]+)/g, (match, fileName) => {
    const filePath = path.join(assetsDir, fileName);
    return fs.existsSync(filePath) ? asDataUrl(filePath) : match;
  });
}

const indexHtml = fs.readFileSync(path.join(distDir, "index.html"), "utf8");
const cssName = indexHtml.match(/href="\/assets\/([^"]+\.css)"/)?.[1];
const jsName = indexHtml.match(/src="\/assets\/([^"]+\.js)"/)?.[1];

if (!cssName || !jsName) {
  throw new Error("未找到生产构建中的 CSS 或 JavaScript 入口文件。请先执行 npm run build。");
}

const css = inlineAssetReferences(fs.readFileSync(path.join(assetsDir, cssName), "utf8"))
  .replaceAll("</style", "<\\/style");
const js = inlineAssetReferences(fs.readFileSync(path.join(assetsDir, jsName), "utf8"))
  .replaceAll("</script", "<\\/script");

const standalone = `<!doctype html>
<html lang="zh-CN">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <meta name="color-scheme" content="light" />
    <title>EEG-tES 本地演示</title>
    <style>${css}</style>
  </head>
  <body>
    <div id="root"></div>
    <script>${js}</script>
  </body>
</html>`;

fs.writeFileSync(outputFile, standalone);
console.log(`已生成：${outputFile}`);
console.log(`文件大小：${(fs.statSync(outputFile).size / 1024 / 1024).toFixed(2)} MB`);
