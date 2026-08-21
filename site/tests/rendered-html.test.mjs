import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const publicDirectory = new URL("../public/", import.meta.url);

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
  assert.match(html, /Unofficial · Windows · Experimental/);
  assert.match(html, /github\.com\/YesterdaysLemon\/codex-continuity/);
  assert.match(html, /github\.com\/sponsors\/YesterdaysLemon/);
  assert.match(html, /alirezaafshan\.com\/projects/);
  assert.doesNotMatch(html, /site-creator|starter loading skeleton/i);
});

test("serves agent and crawler discovery assets", async () => {
  const expectations = [
    ["/llms.txt", /restartsCodex: false/],
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
