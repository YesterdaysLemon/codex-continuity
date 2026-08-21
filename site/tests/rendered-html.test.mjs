import assert from "node:assert/strict";
import test from "node:test";

async function render() {
  const workerUrl = new URL("../dist/server/index.js", import.meta.url);
  workerUrl.searchParams.set("test", `${process.pid}-${Date.now()}`);
  const { default: worker } = await import(workerUrl.href);

  return worker.fetch(
    new Request("http://localhost/", {
      headers: { accept: "text/html" },
    }),
    {
      ASSETS: {
        fetch: async () => new Response("Not found", { status: 404 }),
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
