import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  expectedReleaseAssetNames,
  planContinuousRelease,
} from "../../scripts/plan-continuous-release.mjs";

const workflowDirectory = new URL("../../.github/workflows/", import.meta.url);
const sha = "a".repeat(40);
const expectedAssetNames = [
  "CodexContinuity-v0.5.0-win-x64.zip",
  "CodexContinuity-v0.5.0-win-x64.zip.sha256",
  "CodexContinuity-win-x64.zip",
  "CodexContinuity-win-x64.zip.sha256",
  "CodexContinuity-v0.5.0-Setup.exe",
  "CodexContinuity-v0.5.0-Setup.exe.sha256",
  "CodexContinuity-Setup.exe",
  "CodexContinuity-Setup.exe.sha256",
  "CodexContinuity-v0.5.0-winget-manifests.zip",
  "install.ps1",
];

function input(overrides = {}) {
  return {
    conclusion: "success",
    workflowEvent: "push",
    headBranch: "main",
    headRepository: "YesterdaysLemon/codex-continuity",
    repository: "YesterdaysLemon/codex-continuity",
    expectedSha: sha,
    remoteMain: sha,
    supervisorVersion: "0.5.0",
    trayVersion: "0.5.0",
    siteVersion: "0.5.0",
    stableTags: ["v0.4.0"],
    tagSha: null,
    release: null,
    ...overrides,
  };
}

test("planner rejects every untrusted or stale workflow source", () => {
  const cases = [
    { conclusion: "failure" },
    { workflowEvent: "pull_request" },
    { headBranch: "feature" },
    { headRepository: "someone/codex-continuity" },
    { remoteMain: "b".repeat(40) },
  ];

  for (const overrides of cases) {
    assert.equal(planContinuousRelease(input(overrides)).action, "skip");
  }
  assert.equal(
    planContinuousRelease(input({ expectedSha: "not-a-sha" })).action,
    "fail",
  );
});

test("planner requires one public product version", () => {
  assert.equal(
    planContinuousRelease(input({ supervisorVersion: "version-next" })).action,
    "fail",
  );
  assert.equal(
    planContinuousRelease(input({ trayVersion: "0.2.1" })).action,
    "fail",
  );
  assert.equal(
    planContinuousRelease(input({ siteVersion: "0.2.1" })).action,
    "fail",
  );
});

test("planner creates only a new stable version at the exact green SHA", () => {
  assert.deepEqual(planContinuousRelease(input()), {
    action: "release",
    createTag: true,
    reason: "v0.5.0 is a new stable version at the exact green revision.",
    tag: "v0.5.0",
  });
  assert.equal(
    planContinuousRelease(input({ stableTags: ["v0.5.0"] })).action,
    "fail",
  );
  assert.equal(
    planContinuousRelease(input({ stableTags: ["v1.0.0"] })).action,
    "fail",
  );
});

test("planner CLI emits the same machine-readable release decision", () => {
  const plannerUrl = new URL("../../scripts/plan-continuous-release.mjs", import.meta.url);
  const result = spawnSync(process.execPath, [fileURLToPath(plannerUrl)], {
    encoding: "utf8",
    input: JSON.stringify(input()),
  });

  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), planContinuousRelease(input()));
});

test("planner skips a complete release and resumes every incomplete state", () => {
  assert.deepEqual(expectedReleaseAssetNames("v0.5.0"), expectedAssetNames);
  const completeRelease = {
    isDraft: false,
    isPrerelease: false,
    assets: expectedAssetNames.map((name) => ({ name })),
  };
  assert.equal(
    planContinuousRelease(input({ tagSha: "b".repeat(40), release: completeRelease })).action,
    "skip",
  );
  assert.equal(
    planContinuousRelease(input({
      stableTags: ["v0.5.0", "v1.0.0"],
      tagSha: "b".repeat(40),
      release: completeRelease,
    })).action,
    "fail",
  );

  const incompleteReleases = [
    { ...completeRelease, isDraft: true },
    { ...completeRelease, isPrerelease: true },
    { ...completeRelease, assets: completeRelease.assets.slice(1) },
    null,
  ];
  for (const release of incompleteReleases) {
    assert.deepEqual(planContinuousRelease(input({ tagSha: sha, release })), {
      action: "release",
      createTag: false,
      reason: "v0.5.0 is incomplete; resume the release pipeline.",
      tag: "v0.5.0",
    });
  }

  assert.equal(
    planContinuousRelease(input({ tagSha: "b".repeat(40), release: null })).action,
    "fail",
  );
});

test("workflow binds the reusable release to the tested SHA", async () => {
  const caller = await readFile(
    new URL("continuous-release.yml", workflowDirectory),
    "utf8",
  );
  const release = await readFile(new URL("release.yml", workflowDirectory), "utf8");

  assert.match(caller, /node scripts\/plan-continuous-release\.mjs/);
  assert.match(caller, /release_sha=\$env:EXPECTED_SHA/);
  assert.match(caller, /release_sha: \$\{\{ needs\.prepare\.outputs\.release_sha \}\}/);
  assert.match(release, /release_sha:/);
  assert.match(release, /ref: \$\{\{ inputs\.release_sha \|\| github\.sha \}\}/);
  assert.match(release, /\$tagSha -ne \$env:RELEASE_SHA/);
  assert.match(release, /\$headSha -ne \$env:RELEASE_SHA/);
  assert.match(release, /git ls-remote origin "refs\/tags\/\$tag"/);
  assert.match(release, /\$remoteTagSha -ne \$env:RELEASE_SHA/);
  assert.match(release, /Get-Content site\/package\.json/);
  assert.match(release, /\$tagVersion -ne \$siteVersion/);
});

test("release workflow preserves both entry points and the delivery contract", async () => {
  const workflow = (await readFile(new URL("release.yml", workflowDirectory), "utf8"))
    .replaceAll("\r\n", "\n");
  const assetBlock = workflow.match(/\$assets = @\(([\s\S]*?)\n\s*\)/)?.[1] ?? "";
  const attestationBlock = workflow.match(
    /uses: actions\/attest@v4[\s\S]*?subject-path: \|\n([\s\S]*?)\n\s+- name:/,
  )?.[1] ?? "";
  const expectedAssets = [
    "release/CodexContinuity-$tag-win-x64.zip",
    "release/CodexContinuity-$tag-win-x64.zip.sha256",
    "release/CodexContinuity-win-x64.zip",
    "release/CodexContinuity-win-x64.zip.sha256",
    "release/CodexContinuity-$tag-Setup.exe",
    "release/CodexContinuity-$tag-Setup.exe.sha256",
    "release/CodexContinuity-Setup.exe",
    "release/CodexContinuity-Setup.exe.sha256",
    "release/CodexContinuity-$tag-winget-manifests.zip",
    "install.ps1",
  ];

  assert.match(workflow, /push:\s+tags:\s+- v\*/);
  assert.match(workflow, /workflow_call:/);
  assert.match(workflow, /RELEASE_REF: \$\{\{ inputs\.release_ref \|\| github\.ref \}\}/);
  assert.match(workflow, /RELEASE_TAG: \$\{\{ inputs\.release_tag \|\| github\.ref_name \}\}/);
  assert.match(workflow, /RELEASE_SHA: \$\{\{ inputs\.release_sha \|\| github\.sha \}\}/);
  assert.match(workflow, /scripts\\sign-release\.ps1/);
  assert.match(workflow, /Verify release signing policy/);
  assert.match(
    workflow,
    /SIGNING_REQUESTED: \$\{\{ secrets\.WINDOWS_SIGNING_CERTIFICATE_BASE64 != '' \|\| secrets\.WINDOWS_SIGNING_CERTIFICATE_PASSWORD != '' \|\| vars\.WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT != '' \}\}/,
  );
  assert.match(
    workflow,
    /CONTINUITY_SIGNING_EXPECTED_THUMBPRINT: \$\{\{ vars\.WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT \}\}/,
  );
  assert.match(workflow, /-VerifyOnly -RequireUnsigned/);
  assert.match(workflow, /winget validate --manifest release\/winget/);
  assert.match(workflow, /uses: actions\/attest@v4/);
  assert.deepEqual(
    expectedAssets.filter((asset) => !assetBlock.includes(`"${asset}"`)),
    [],
  );
  const attestedSubjects = attestationBlock
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
  const publishedAssets = [...assetBlock.matchAll(/"([^"]+)"/g)]
    .map((match) => match[1]);
  const isAttested = (asset) => attestedSubjects.some((subject) => {
    if (!subject.includes("*")) {
      return subject === asset;
    }
    const [prefix, suffix] = subject.split("*");
    return asset.startsWith(prefix) && asset.endsWith(suffix);
  });
  assert.deepEqual(publishedAssets.filter((asset) => !isAttested(asset)), []);
  assert.match(workflow, /gh release create \$tag @assets --verify-tag/);
  assert.match(workflow, /gh release upload \$tag @assets --clobber/);
  assert.match(workflow, /gh release edit \$tag --draft=false --prerelease=false --latest/);
});
