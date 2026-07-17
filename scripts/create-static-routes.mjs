import { copyFileSync, mkdirSync } from "node:fs";
import { resolve } from "node:path";

const distDirectory = resolve("dist");
const sourceEntry = resolve(distDirectory, "index.html");
const routes = ["home", "setup", "history", "electrodes", "experiment"];

for (const route of routes) {
  const routeDirectory = resolve(distDirectory, route);
  mkdirSync(routeDirectory, { recursive: true });
  copyFileSync(sourceEntry, resolve(routeDirectory, "index.html"));
}

console.log(`created static entries for ${routes.length} routes`);