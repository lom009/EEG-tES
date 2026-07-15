import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const projectRoot = fileURLToPath(new URL("../", import.meta.url));
const sourceFiles = ["src/App.jsx", "src/styles.css"];
const assetReferences = new Set();

for (const sourceFile of sourceFiles) {
  const source = readFileSync(new URL(`../${sourceFile}`, import.meta.url), "utf8");
  for (const match of source.matchAll(/\/assets\/[A-Za-z0-9._/-]+/g)) {
    assetReferences.add(match[0]);
  }
}

assert.ok(assetReferences.size > 0, "application should reference local assets");

const missingAssets = [...assetReferences].filter((assetPath) => {
  return !existsSync(`${projectRoot}public${assetPath}`);
});

assert.deepEqual(missingAssets, [], `missing local assets: ${missingAssets.join(", ")}`);
console.log(`local-asset verification passed for ${assetReferences.size} files`);
