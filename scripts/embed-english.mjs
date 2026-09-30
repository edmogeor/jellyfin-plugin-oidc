import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname } from "node:path";

const [template, dictionary, output] = process.argv.slice(2);
const strings = JSON.parse(readFileSync(dictionary, "utf8"));
const html = readFileSync(template, "utf8").replace(
  /\{\{(\w+)\}\}/g,
  (_, key) => {
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
  },
);
mkdirSync(dirname(output), { recursive: true });
writeFileSync(output, html);
