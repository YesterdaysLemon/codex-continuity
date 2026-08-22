import assert from "node:assert/strict";
import test from "node:test";

import {
  deploymentPayload,
  main,
  notifyDeployment,
  verifyPublication,
} from "../scripts/deploy-production.mjs";

const sha = "0123456789abcdef0123456789abcdef01234567";
const literalBody = "{\"event\":\"push\",\"branch\":\"main\",\"repo\":\"YesterdaysLemon/codex-continuity\",\"sha\":\"0123456789abcdef0123456789abcdef01234567\"}";
const knownSignature = "sha256=89c842c455184d279bcbef68ba29a33fee4a59dce4b77723c655a95e487ede64";

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

  assert.deepEqual(result, { ok: true, sha });
  assert.equal(slept, 1);
  assert.equal(requests.length, 2);
  assert.equal(requests[0].init.body, literalBody);
  assert.equal(requests[0].init.headers["X-GitHub-Event"], "push");
  assert.equal(requests[0].init.headers["X-Hub-Signature-256"], knownSignature);
});

test("retries every transient gateway status", async () => {
  for (const status of [502, 503, 504]) {
    let attempts = 0;
    let sleeps = 0;
    const result = await notifyDeployment({
      url: "https://deploy.example.test/deploy/continuity",
      secret: "fixture-secret",
      payload: { event: "push", branch: "main", repo: "repo", sha },
      fetchImpl: async () => {
        attempts += 1;
        return attempts === 1
          ? new Response("gateway failure", { status })
          : new Response(JSON.stringify({ ok: true, sha }));
      },
      sleep: async () => { sleeps += 1; },
      maximumAttempts: 2,
    });
    assert.deepEqual(result, { ok: true, sha });
    assert.equal(attempts, 2);
    assert.equal(sleeps, 1);
  }
});

test("bounds retry exhaustion and does not retry other client errors", async () => {
  let transientAttempts = 0;
  let transientSleeps = 0;
  await assert.rejects(
    notifyDeployment({
      url: "https://deploy.example.test/deploy/continuity",
      secret: "fixture-secret",
      payload: { event: "push", branch: "main", repo: "repo", sha },
      fetchImpl: async () => {
        transientAttempts += 1;
        return new Response("unavailable", { status: 503 });
      },
      sleep: async () => { transientSleeps += 1; },
      maximumAttempts: 3,
    }),
    /unavailable beyond the retry window/,
  );
  assert.equal(transientAttempts, 3);
  assert.equal(transientSleeps, 2);

  let clientAttempts = 0;
  let clientSleeps = 0;
  await assert.rejects(
    notifyDeployment({
      url: "https://deploy.example.test/deploy/continuity",
      secret: "fixture-secret",
      payload: { event: "push", branch: "main", repo: "repo", sha },
      fetchImpl: async () => {
        clientAttempts += 1;
        return new Response("bad request", { status: 400 });
      },
      sleep: async () => { clientSleeps += 1; },
    }),
    /HTTP 400/,
  );
  assert.equal(clientAttempts, 1);
  assert.equal(clientSleeps, 0);
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

test("rejects wrong release, llms, and revision markers", async () => {
  const validBodies = {
    "/": 'Codex Continuity softwareVersion":"0.2.1',
    "/llms.txt": "supported v0.2.1 release target",
    "/deploy-revision.txt": `${sha}\n`,
  };
  const cases = [
    ["/", "Codex Continuity softwareVersion\":\"0.1.0", /expected v0.2.1 release marker/],
    ["/llms.txt", "supported v0.1.0 release target", /llms.txt does not expose/],
    ["/deploy-revision.txt", `${"f".repeat(40)}\n`, /not serving the requested commit SHA/],
  ];

  for (const [changedPath, changedBody, expectedError] of cases) {
    await assert.rejects(
      verifyPublication({
        baseUrl: "https://continuity.example.test",
        expectedVersion: "0.2.1",
        expectedRevision: sha,
        fetchImpl: async (url) => new Response(
          url.pathname === changedPath ? changedBody : validBodies[url.pathname],
        ),
      }),
      expectedError,
    );
  }
});

test("production orchestration verifies exact custom revision and Sites fallback", async () => {
  const calls = [];
  const environment = {
    DEPLOY_WEBHOOK_SECRET: "fixture-secret",
    DEPLOY_WEBHOOK_URL: "https://deploy.example.test/deploy/continuity",
    GITHUB_EVENT_NAME: "push",
    GITHUB_REF_NAME: "main",
    GITHUB_REPOSITORY: "YesterdaysLemon/codex-continuity",
    GITHUB_SHA: sha,
    PRODUCTION_URL: "https://continuity.example.test",
    SITES_URL: "https://fallback.example.test",
  };

  await main(environment, {
    notifyDeploymentImpl: async (options) => { calls.push(["notify", options]); },
    verifyPublicationImpl: async (options) => { calls.push(["verify", options]); },
  });

  assert.deepEqual(calls, [
    ["notify", {
      url: environment.DEPLOY_WEBHOOK_URL,
      secret: environment.DEPLOY_WEBHOOK_SECRET,
      payload: {
        event: "push",
        branch: "main",
        repo: "YesterdaysLemon/codex-continuity",
        sha,
      },
    }],
    ["verify", {
      baseUrl: environment.PRODUCTION_URL,
      expectedVersion: "0.2.1",
      expectedRevision: sha,
    }],
    ["verify", {
      baseUrl: environment.SITES_URL,
      expectedVersion: "0.2.1",
    }],
  ]);
});
