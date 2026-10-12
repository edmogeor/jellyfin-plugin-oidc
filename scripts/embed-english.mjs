import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname } from "node:path";

const [template, dictionary, output] = process.argv.slice(2);
const strings = JSON.parse(readFileSync(dictionary, "utf8"));
const source = readFileSync(template, "utf8");
const marker = "/* @english-strings */ {}";
if (template.endsWith(".js") && !source.includes(marker))
  throw new Error("Missing English dictionary marker");
const content = template.endsWith(".js")
  ? source.replace(marker, () => JSON.stringify(strings))
  : source.replace(/\{\{(\w+)\}\}/g, (_, key) => {
      if (typeof strings[key] !== "string")
        throw new Error(`Missing English string: ${key}`);
      return strings[key].replace(
        /[&<>"']/g,
        (character) =>
          ({
            "&": "&amp;",
            "<": "&lt;",
            ">": "&gt;",
            '"': "&quot;",
            "'": "&#39;",
          })[character],
      );
    });
mkdirSync(dirname(output), { recursive: true });
writeFileSync(output, content);
