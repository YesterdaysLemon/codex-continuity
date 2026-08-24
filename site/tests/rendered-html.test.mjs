import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const publicDirectory = new URL("../public/", import.meta.url);
const sourceDirectory = new URL("../app/", import.meta.url);
const { version: productVersion } = JSON.parse(
  await readFile(new URL("../package.json", import.meta.url), "utf8"),
);

async function render(path = "/", accept = "text/html") {
  const workerUrl = new URL("../dist/server/index.js", import.meta.url);
  workerUrl.searchParams.set("test", `${process.pid}-${Date.now()}`);
  const { default: worker } = await import(workerUrl.href);

  return worker.fetch(
    new Request(new URL(path, "http://localhost/"), {
      headers: { accept },
    }),
    {
      ASSETS: {
        fetch: async (request) => {
          const pathname = new URL(request.url).pathname;
          const relativePath = pathname.replace(/^\//, "");
          try {
            const body = await readFile(new URL(relativePath, publicDirectory));
            const contentType = relativePath.endsWith(".svg")
              ? "image/svg+xml"
              : relativePath.endsWith(".png")
                ? "image/png"
                : relativePath.endsWith(".xml")
                  ? "application/xml"
                  : "text/plain";
            return new Response(body, { headers: { "content-type": contentType } });
          } catch {
            return new Response("Not found", { status: 404 });
          }
        },
      },
    },
    {
      waitUntil() {},
      passThroughOnException() {},
    },
  );
}

test("server-renders the Codex Continuity launch page", async () => {
  const response = await render();
  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-type") ?? "", /^text\/html\b/i);

  const html = await response.text();
  assert.match(html, /<title>Codex Continuity — Keep the agents\. Replace the window\.<\/title>/i);
  assert.match(html, /Keep the agents\./);
  assert.match(html, /Replace the window\./);
  assert.match(html, /Download for Windows/);
  assert.match(html, /curl\.exe -fsSL/);
  assert.match(html, /-Plan -Json/);
  assert.match(html, /\/llms\.txt/);
  assert.match(html, /SoftwareApplication/);
  const escapedVersion = productVersion.replaceAll(".", "\\.");
  assert.match(html, new RegExp(`softwareVersion[^<]*${escapedVersion}`));
  assert.match(html, new RegExp(`v${escapedVersion} supports Windows`));
  assert.match(html, /Unofficial · Windows · Experimental/);
  assert.match(html, /Skip to content/);
  assert.match(html, /WIN11 VERIFIED/);
  assert.match(html, /One command\./);
  assert.match(html, /No forced restart\./);
  assert.match(html, /waits for a natural close/i);
  assert.match(html, /never asks for a\s+restart/i);
  assert.match(html, /CodexContinuity status/);
  assert.match(html, /Files leave at next sign-in/);
  assert.match(html, /there is no macOS\s+or Linux build today/i);
  assert.doesNotMatch(html, /Zero interrupted agents|>PROVEN</i);
  assert.match(html, /github\.com\/YesterdaysLemon\/codex-continuity/);
  assert.match(html, /github\.com\/sponsors\/YesterdaysLemon/);
  assert.match(html, /alirezaafshan\.com\/projects/);
  assert.doesNotMatch(html, /site-creator|starter loading skeleton/i);
});

test("retains responsive and keyboard-accessible site polish", async () => {
  const css = await readFile(new URL("globals.css", sourceDirectory), "utf8");

  assert.match(css, /\.agent-copy\s*\{\s*min-width:\s*0;/);
  assert.match(css, /button:focus-visible/);
  assert.match(css, /\.install-command button\s*\{[^}]*position:\s*static;/s);
  assert.doesNotMatch(css, /var\(--text\)/);
});

test("serves agent and crawler discovery assets", async () => {
  const expectations = [
    ["/llms.txt", new RegExp(`supported v${productVersion.replaceAll(".", "\\.")} release target`)],
    ["/robots.txt", /Sitemap:/],
    ["/sitemap.xml", /<loc>https:\/\/continuity\.alirezaafshan\.com\//],
    ["/icon.svg", /<svg/],
  ];

  for (const [path, pattern] of expectations) {
    const response = await render(path, "*/*");
    assert.equal(response.status, 200, path);
    assert.match(await response.text(), pattern, path);
  }

  const socialCard = await render("/og.png", "image/png");
  assert.equal(socialCard.status, 200);
  assert.match(socialCard.headers.get("content-type") ?? "", /^image\/png\b/i);
  assert.ok((await socialCard.arrayBuffer()).byteLength > 100_000);
});
