import { createHmac } from "node:crypto";

const secret = process.env.DEPLOY_WEBHOOK_SECRET;
const url = process.env.DEPLOY_WEBHOOK_URL;
const sha = process.env.GITHUB_SHA;

if (!secret || !url || !/^[0-9a-f]{40}$/i.test(sha ?? "")) {
  throw new Error("The deploy probe requires its URL, secret, and a commit SHA.");
}

const candidates = [
  "push",
  "workflow_run",
  "workflow_dispatch",
  "repository_dispatch",
  "deployment",
  "deployment_status",
  "release",
  "check_suite",
  "pull_request",
  "schedule",
];
const matches = [];

for (const event of candidates) {
  const body = JSON.stringify({
    event,
    branch: "__continuity_event_probe__",
    repo: "__continuity_event_probe__",
    sha,
  });
  const signature = `sha256=${createHmac("sha256", secret).update(body).digest("hex")}`;
  const response = await fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-GitHub-Event": event,
      "X-Hub-Signature-256": signature,
    },
    body,
  });
  const result = await response.json();
  const reason = typeof result.reason === "string" && /^[a-z0-9_-]{1,64}$/i.test(result.reason)
    ? result.reason
    : "no_safe_reason";

  console.log(`${event}: HTTP ${response.status} ${reason}`);
  if (response.status === 202 && reason === "wrong_ref") {
    matches.push(event);
  }
}

if (matches.length !== 1) {
  throw new Error(`Expected exactly one accepted event; found ${matches.length}.`);
}

console.log(`Accepted event: ${matches[0]}`);
