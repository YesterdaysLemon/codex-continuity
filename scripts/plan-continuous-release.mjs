import { pathToFileURL } from "node:url";

export function expectedReleaseAssetNames(tag) {
  return [
    `CodexContinuity-${tag}-win-x64.zip`,
    `CodexContinuity-${tag}-win-x64.zip.sha256`,
    "CodexContinuity-win-x64.zip",
    "CodexContinuity-win-x64.zip.sha256",
    `CodexContinuity-${tag}-Setup.exe`,
    `CodexContinuity-${tag}-Setup.exe.sha256`,
    "CodexContinuity-Setup.exe",
    "CodexContinuity-Setup.exe.sha256",
    `CodexContinuity-${tag}-winget-manifests.zip`,
    "install.ps1",
  ];
}

function parseStableVersion(value) {
  const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(value ?? "");
  return match ? match.slice(1).map(Number) : null;
}

function compareVersions(left, right) {
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) {
      return left[index] - right[index];
    }
  }
  return 0;
}

function fail(reason) {
  return { action: "fail", reason };
}

function skip(reason) {
  return { action: "skip", reason };
}

export function planContinuousRelease(input) {
  if (input.conclusion !== "success") {
    return skip("The completed CI workflow did not succeed.");
  }
  if (input.workflowEvent !== "push") {
    return skip("Only push CI runs may publish releases.");
  }
  if (input.headBranch !== "main") {
    return skip("Only main may publish releases.");
  }
  if (input.headRepository !== input.repository) {
    return skip("Fork workflow runs may not publish releases.");
  }
  if (!/^[0-9a-f]{40}$/.test(input.expectedSha ?? "")) {
    return fail(`CI did not provide a full commit SHA: ${input.expectedSha ?? ""}`);
  }
  if (input.remoteMain !== input.expectedSha) {
    return skip(
      `Skipping stale green revision ${input.expectedSha}; main is ${input.remoteMain}.`,
    );
  }

  const supervisorVersion = parseStableVersion(input.supervisorVersion);
  if (!supervisorVersion) {
    return fail(`Release version must use X.Y.Z syntax: ${input.supervisorVersion ?? ""}`);
  }
  if (input.supervisorVersion !== input.trayVersion) {
    return fail(
      `Supervisor and tray versions differ: ${input.supervisorVersion} != ${input.trayVersion}`,
    );
  }
  if (input.supervisorVersion !== input.siteVersion) {
    return fail(
      `Desktop and site versions differ: ${input.supervisorVersion} != ${input.siteVersion}`,
    );
  }

  const tag = `v${input.supervisorVersion}`;
  const tagExists = typeof input.tagSha === "string" && input.tagSha.length > 0;
  if (input.release && !tagExists) {
    return fail(`Release ${tag} exists without a fetched tag.`);
  }

  if (input.release) {
    const assetNames = new Set((input.release.assets ?? []).map(({ name }) => name));
    const missingAssets = expectedReleaseAssetNames(tag).filter(
      (name) => !assetNames.has(name),
    );
    if (!input.release.isDraft && !input.release.isPrerelease && missingAssets.length === 0) {
      return skip(`${tag} is already a complete stable release.`);
    }
  }

  if (tagExists) {
    if (input.tagSha !== input.expectedSha) {
      return fail(
        `Incomplete release tag ${tag} points to ${input.tagSha} instead of ${input.expectedSha}.`,
      );
    }
    return {
      action: "release",
      createTag: false,
      reason: `${tag} is incomplete; resume the release pipeline.`,
      tag,
    };
  }

  const stableVersions = (input.stableTags ?? [])
    .map((stableTag) => /^v(\d+\.\d+\.\d+)$/.exec(stableTag)?.[1])
    .filter(Boolean)
    .map(parseStableVersion);
  const latestVersion = stableVersions.sort(compareVersions).at(-1);
  if (latestVersion && compareVersions(supervisorVersion, latestVersion) <= 0) {
    return fail(
      `New release version ${input.supervisorVersion} must be greater than ${latestVersion.join(".")}.`,
    );
  }

  return {
    action: "release",
    createTag: true,
    reason: `${tag} is a new stable version at the exact green revision.`,
    tag,
  };
}

async function readStandardInput() {
  const chunks = [];
  for await (const chunk of process.stdin) {
    chunks.push(chunk);
  }
  return chunks.join("");
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const input = JSON.parse(await readStandardInput());
  process.stdout.write(JSON.stringify(planContinuousRelease(input)));
}
