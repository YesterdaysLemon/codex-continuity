import assert from "node:assert/strict";
import test from "node:test";

import {
  deploymentPayload,
  deploymentSignature,
  notifyDeployment,
  verifyPublication,
} from "../scripts/deploy-production.mjs";

const sha = "0123456789abcdef0123456789abcdef01234567";

test("signs the exact deploy payload and waits for an older deployment", async () => {
  const payload = deploymentPayload({
    event: "push",
    branch: "main",
    repo: "YesterdaysLemon/codex-continuity",
    sha,
  });
  const requests = [];
  const responses = [
    new Response(JSON.stringify({ error: "deploy_in_progress" }), { status: 409 }),
    new Response(JSON.stringify({ ok: true, sha }), { status: 200 }),
  ];
  let slept = 0;

  const result = await notifyDeployment({
    url: "https://deploy.example.test/deploy/continuity",
    secret: "fixture-secret",
    payload,
    fetchImpl: async (url, init) => {
      requests.push({ url, init });
      return responses.shift();
    },
    sleep: async () => { slept += 1; },
  });

  const body = JSON.stringify(payload);
  assert.deepEqual(result, { ok: true, sha });
  assert.equal(slept, 1);
  assert.equal(requests.length, 2);
  assert.equal(requests[0].init.body, body);
  assert.equal(requests[0].init.headers["X-GitHub-Event"], "push");
  assert.equal(
    requests[0].init.headers["X-Hub-Signature-256"],
    deploymentSignature("fixture-secret", body),
  );
});

test("rejects malformed or unconfirmed revisions", async () => {
  assert.throws(
    () => deploymentPayload({ event: "push", branch: "main", repo: "repo", sha: "main" }),
    /40-character hexadecimal commit SHA/,
  );
  await assert.rejects(
    notifyDeployment({
      url: "https://deploy.example.test/deploy/continuity",
      secret: "fixture-secret",
      payload: { event: "push", branch: "main", repo: "repo", sha },
      fetchImpl: async () => new Response(JSON.stringify({ ok: true, sha: "f".repeat(40) })),
    }),
    /did not confirm/,
  );
});

test("verifies release, llms, and exact production revision markers", async () => {
  const requested = [];
  await verifyPublication({
    baseUrl: "https://continuity.example.test",
    expectedVersion: "0.2.1",
    expectedRevision: sha,
    fetchImpl: async (url) => {
      requested.push(url.pathname);
      const body = url.pathname === "/"
        ? 'Codex Continuity softwareVersion":"0.2.1'
        : url.pathname === "/llms.txt"
          ? "supported v0.2.1 release target"
          : `${sha}\n`;
      return new Response(body);
    },
  });

  assert.deepEqual(requested, ["/", "/llms.txt", "/deploy-revision.txt"]);
});
