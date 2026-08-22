import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
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
const { version: productVersion } = JSON.parse(
  await readFile(new URL("../package.json", import.meta.url), "utf8"),
);

test("uses unreserved payload variables for the deployment manager", async () => {
  const workflow = await readFile(
    new URL("../../.github/workflows/deploy-site.yml", import.meta.url),
    "utf8",
  );
  const deployStep = workflow.split("- name: Deploy and verify the exact green revision")[1];

  assert.ok(deployStep, "production deployment step is missing");
  assert.doesNotMatch(deployStep, /^\s+GITHUB_[A-Z_]+:/m);
  assert.match(deployStep, /^\s+DEPLOY_BRANCH: main$/m);
  assert.match(deployStep, /^\s+DEPLOY_EVENT_NAME: push$/m);
  assert.match(deployStep, /^\s+DEPLOY_REPOSITORY: YesterdaysLemon\/codex-continuity$/m);
  assert.match(
    deployStep,
    /^\s+DEPLOY_SHA: \$\{\{ github\.event\.workflow_run\.head_sha \}\}$/m,
  );
  assert.match(workflow, /github\.event\.workflow_run\.event == 'push'/);
});

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

  await assert.rejects(
    notifyDeployment({
      url: "https://deploy.example.test/deploy/continuity",
      secret: "fixture-secret",
      payload: { event: "push", branch: "main", repo: "repo", sha },
      fetchImpl: async () => new Response(
        JSON.stringify({ ok: true, skipped: true, reason: "wrong_ref" }),
        { status: 202 },
      ),
    }),
    /did not confirm the requested commit SHA\. Reason: wrong_ref\./,
  );

  await assert.rejects(
    notifyDeployment({
      url: "https://deploy.example.test/deploy/continuity",
      secret: "fixture-secret",
      payload: { event: "push", branch: "main", repo: "repo", sha },
      fetchImpl: async () => new Response(
        JSON.stringify({ ok: true, skipped: true, reason: "wrong_ref\nforged_log" }),
        { status: 202 },
      ),
    }),
    (error) => error.message === "Deployment manager did not confirm the requested commit SHA.",
  );
});

test("verifies release, llms, and exact production revision markers", async () => {
  const requested = [];
  await verifyPublication({
    baseUrl: "https://continuity.example.test",
    expectedVersion: productVersion,
    expectedRevision: sha,
    fetchImpl: async (url) => {
      requested.push(url.pathname);
      const body = url.pathname === "/"
        ? `Codex Continuity softwareVersion":"${productVersion}`
        : url.pathname === "/llms.txt"
          ? `supported v${productVersion} release target`
          : `${sha}\n`;
      return new Response(body);
    },
  });

  assert.deepEqual(requested, ["/", "/llms.txt", "/deploy-revision.txt"]);
});

test("rejects wrong release, llms, and revision markers", async () => {
  const validBodies = {
    "/": `Codex Continuity softwareVersion":"${productVersion}`,
    "/llms.txt": `supported v${productVersion} release target`,
    "/deploy-revision.txt": `${sha}\n`,
  };
  const cases = [
    [
      "/",
      "Codex Continuity softwareVersion\":\"0.1.0",
      new RegExp(`expected v${productVersion.replaceAll(".", "\\.")} release marker`),
    ],
    ["/llms.txt", "supported v0.1.0 release target", /llms.txt does not expose/],
    ["/deploy-revision.txt", `${"f".repeat(40)}\n`, /not serving the requested commit SHA/],
  ];

  for (const [changedPath, changedBody, expectedError] of cases) {
    await assert.rejects(
      verifyPublication({
        baseUrl: "https://continuity.example.test",
        expectedVersion: productVersion,
        expectedRevision: sha,
        fetchImpl: async (url) => new Response(
          url.pathname === changedPath ? changedBody : validBodies[url.pathname],
        ),
      }),
      expectedError,
    );
  }
});

test("production orchestration verifies the exact canonical revision", async () => {
  const calls = [];
  const environment = {
    DEPLOY_BRANCH: "main",
    DEPLOY_EVENT_NAME: "push",
    DEPLOY_REPOSITORY: "YesterdaysLemon/codex-continuity",
    DEPLOY_SHA: sha,
    DEPLOY_WEBHOOK_SECRET: "fixture-secret",
    DEPLOY_WEBHOOK_URL: "https://deploy.example.test/deploy/continuity",
    GITHUB_EVENT_NAME: "workflow_run",
    GITHUB_REF_NAME: "reserved-ref",
    GITHUB_REPOSITORY: "reserved/repository",
    GITHUB_SHA: "f".repeat(40),
    PRODUCTION_URL: "https://continuity.example.test",
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
      expectedVersion: productVersion,
      expectedRevision: sha,
    }],
  ]);
});
