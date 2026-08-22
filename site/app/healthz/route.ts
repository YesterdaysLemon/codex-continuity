import packageMetadata from "../../package.json";

const revisionPattern = /^[0-9a-f]{40}$/i;

async function fetchText(pathname: string) {
  const port = process.env.PORT ?? "8080";
  const response = await fetch(`http://127.0.0.1:${port}${pathname}`, {
    cache: "no-store",
  });
  if (!response.ok) {
    throw new Error(`${pathname} returned HTTP ${response.status}`);
  }
  return response.text();
}

export async function GET() {
  try {
    const [homepage, llms, revisionText] = await Promise.all([
      fetchText("/"),
      fetchText("/llms.txt"),
      fetchText("/deploy-revision.txt"),
    ]);
    const revision = revisionText.trim();
    const expectedVersion = packageMetadata.version;
    const healthy = homepage.includes("Codex Continuity")
      && homepage.includes(`softwareVersion":"${expectedVersion}`)
      && llms.includes(`supported v${expectedVersion} release target`)
      && revisionPattern.test(revision);

    return Response.json(
      healthy ? { ok: true, revision } : { ok: false },
      { status: healthy ? 200 : 503 },
    );
  } catch {
    return Response.json({ ok: false }, { status: 503 });
  }
}
