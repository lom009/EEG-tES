import { writeFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
const commit = process.env.RENDER_GIT_COMMIT || execFileSync("git", ["rev-parse", "HEAD"], {encoding: "utf8"}).trim();
writeFileSync("dist/build-info.json", JSON.stringify({commit, builtAt: new Date().toISOString()}) + "\n");
