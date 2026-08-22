import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const workflowDirectory = new URL("../../.github/workflows/", import.meta.url);

test("continuous release accepts only the exact current green main revision", async () => {
  const workflow = await readFile(
    new URL("continuous-release.yml", workflowDirectory),
    "utf8",
  );

  assert.match(workflow, /workflow_run:/);
  assert.match(workflow, /workflows: \[CI\]/);
  assert.match(workflow, /branches: \[main\]/);
  assert.match(workflow, /workflow_run\.conclusion == 'success'/);
  assert.match(workflow, /workflow_run\.event == 'push'/);
  assert.match(workflow, /workflow_run\.head_branch == 'main'/);
  assert.match(
    workflow,
    /workflow_run\.head_repository\.full_name == github\.repository/,
  );
  assert.match(workflow, /ref: \$\{\{ github\.event\.workflow_run\.head_sha \}\}/);
  assert.match(workflow, /git ls-remote origin refs\/heads\/main/);
  assert.match(workflow, /if \(\$remoteMain -ne \$env:EXPECTED_SHA\)/);
  assert.match(workflow, /if \(\$tagSha -ne \$env:EXPECTED_SHA\)/);
  assert.match(workflow, /git tag --annotate \$tag \$env:EXPECTED_SHA/);
  assert.match(workflow, /uses: \.\/\.github\/workflows\/release\.yml/);
});

test("one release implementation verifies its tag, ref, and project versions", async () => {
  const workflow = await readFile(new URL("release.yml", workflowDirectory), "utf8");

  assert.match(workflow, /workflow_call:/);
  assert.match(workflow, /release_ref:/);
  assert.match(workflow, /release_tag:/);
  assert.match(workflow, /ref: \$\{\{ inputs\.release_ref \|\| github\.ref \}\}/);
  assert.match(workflow, /\$env:RELEASE_REF -ne "refs\/tags\/\$tag"/);
  assert.match(workflow, /git rev-list -n 1 "refs\/tags\/\$tag"/);
  assert.match(workflow, /tag=\$tagVersion supervisor=\$supervisorVersion tray=\$trayVersion/);
  assert.match(workflow, /gh release create \$tag @assets --verify-tag/);
});
