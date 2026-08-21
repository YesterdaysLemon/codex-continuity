"use client";

import { useState } from "react";

export const installCommand = `$i="$env:TEMP\\codex-continuity-install.ps1"; curl.exe -fsSL https://github.com/YesterdaysLemon/codex-continuity/releases/latest/download/install.ps1 -o $i; powershell.exe -NoProfile -ExecutionPolicy Bypass -File $i -StartNow`;

export default function CopyInstallCommand() {
  const [copied, setCopied] = useState(false);

  async function copy() {
    await navigator.clipboard.writeText(installCommand);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1800);
  }

  return (
    <div className="install-command">
      <pre><code><span className="prompt">PS›</span> {installCommand}</code></pre>
      <button type="button" onClick={copy} aria-live="polite">
        {copied ? "Copied ✓" : "Copy command"}
      </button>
    </div>
  );
}
