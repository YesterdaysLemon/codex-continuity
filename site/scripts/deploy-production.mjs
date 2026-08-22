import { createHmac } from "node:crypto";
import { readFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";

const transientStatuses = new Set([502, 503, 504]);

function requireValue(value, name) {
  if (!value) {
    throw new Error(`${name} is required.`);
  }
  return value;
}

export function deploymentPayload({ event, branch, repo, sha }) {
  if (!/^[0-9a-f]{40}$/i.test(sha)) {
    throw new Error("DEPLOY_SHA must be a 40-character hexadecimal commit SHA.");
  }
  return { event, branch, repo, sha };
}

export function deploymentSignature(secret, body) {
  return `sha256=${createHmac("sha256", secret).update(body).digest("hex")}`;
}

export async function notifyDeployment({
  url,
  secret,
  payload,
  fetchImpl = fetch,
  sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)),
  retryDelayMilliseconds = 10_000,
  maximumAttempts = 180,
}) {
  const body = JSON.stringify(payload);
  const headers = {
    "Content-Type": "application/json",
    "X-GitHub-Event": payload.event,
    "X-Hub-Signature-256": deploymentSignature(secret, body),
  };

  for (let attempt = 1; attempt <= maximumAttempts; attempt += 1) {
    const response = await fetchImpl(url, { method: "POST", headers, body });
    const responseText = await response.text();
    let result;
    try {
      result = JSON.parse(responseText);
    } catch {
      result = null;
    }

    const deployInProgress = response.status === 409 && result?.error === "deploy_in_progress";
    if (deployInProgress || transientStatuses.has(response.status)) {
      if (attempt === maximumAttempts) {
        throw new Error("Deployment manager remained unavailable beyond the retry window.");
      }
      await sleep(retryDelayMilliseconds);
      continue;
    }
    if (!response.ok) {
      throw new Error(`Deployment manager returned HTTP ${response.status}.`);
    }
    if (result?.sha !== payload.sha) {
      const reason = result?.reason;
      const reasonDetail = typeof reason === "string" && /^[a-z0-9_-]{1,64}$/i.test(reason)
        ? ` Reason: ${reason}.`
        : "";
      throw new Error(
        `Deployment manager did not confirm the requested commit SHA.${reasonDetail}`,
      );
    }
    return result;
  }

  throw new Error("Deployment manager remained busy beyond the retry window.");
}

async function fetchText(fetchImpl, url) {
  const response = await fetchImpl(url, { cache: "no-store" });
  if (!response.ok) {
    throw new Error(`${url} returned HTTP ${response.status}.`);
  }
  return response.text();
}

export async function verifyPublication({
  baseUrl,
  expectedVersion,
  expectedRevision,
  fetchImpl = fetch,
}) {
  const base = new URL(baseUrl);
  const homepage = await fetchText(fetchImpl, new URL("/", base));
  const llms = await fetchText(fetchImpl, new URL("/llms.txt", base));
  if (!homepage.includes("Codex Continuity") || !homepage.includes(`softwareVersion":"${expectedVersion}`)) {
    throw new Error(`${base.origin} does not expose the expected v${expectedVersion} release marker.`);
  }
  if (!llms.includes(`supported v${expectedVersion} release target`)) {
    throw new Error(`${base.origin}/llms.txt does not expose the expected release marker.`);
  }
  if (expectedRevision) {
    const revision = await fetchText(fetchImpl, new URL("/deploy-revision.txt", base));
    if (revision.trim() !== expectedRevision) {
      throw new Error(`${base.origin} is not serving the requested commit SHA.`);
    }
  }
}

export async function main(
  environment = process.env,
  {
    notifyDeploymentImpl = notifyDeployment,
    verifyPublicationImpl = verifyPublication,
  } = {},
) {
  const packageJson = JSON.parse(
    await readFile(new URL("../package.json", import.meta.url), "utf8"),
  );
  const payload = deploymentPayload({
    event: requireValue(environment.DEPLOY_EVENT_NAME, "DEPLOY_EVENT_NAME"),
    branch: requireValue(environment.DEPLOY_BRANCH, "DEPLOY_BRANCH"),
    repo: requireValue(environment.DEPLOY_REPOSITORY, "DEPLOY_REPOSITORY"),
    sha: requireValue(environment.DEPLOY_SHA, "DEPLOY_SHA"),
  });
  await notifyDeploymentImpl({
    url: requireValue(environment.DEPLOY_WEBHOOK_URL, "DEPLOY_WEBHOOK_URL"),
    secret: requireValue(environment.DEPLOY_WEBHOOK_SECRET, "DEPLOY_WEBHOOK_SECRET"),
    payload,
  });
  await verifyPublicationImpl({
    baseUrl: requireValue(environment.PRODUCTION_URL, "PRODUCTION_URL"),
    expectedVersion: packageJson.version,
    expectedRevision: payload.sha,
  });
  await verifyPublicationImpl({
    baseUrl: requireValue(environment.SITES_URL, "SITES_URL"),
    expectedVersion: packageJson.version,
  });
  console.log(`Verified production deployment for ${payload.sha}.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error instanceof Error ? error.message : error);
    process.exitCode = 1;
  });
}
