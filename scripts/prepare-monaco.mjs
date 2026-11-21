import { cp, mkdir, rm, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const source = path.join(root, "node_modules", "monaco-editor", "min", "vs");
const destination = path.join(root, "src", "Studio.App", "Assets", "monaco", "vs");

try {
  const sourceStat = await stat(source);
  if (!sourceStat.isDirectory()) throw new Error("Monaco package min/vs folder is missing.");
} catch {
  throw new Error("Monaco is not installed. Run: npm install --no-save --no-package-lock monaco-editor@0.52.2");
}

await rm(destination, { recursive: true, force: true });
await mkdir(path.dirname(destination), { recursive: true });
await cp(source, destination, { recursive: true, force: true });

const loader = path.join(destination, "loader.js");
await stat(loader);
process.stdout.write("Monaco 0.52.2 assets copied to src/Studio.App/Assets/monaco/vs\n");
