import CopyInstallCommand from "./CopyInstallCommand";

export default function Home() {
  return (
    <main>
      <a className="skip-link" href="#top">Skip to content</a>
      <nav className="nav" aria-label="Primary navigation">
        <a className="brand" href="#top" aria-label="Codex Continuity home">
          <span className="brand-mark" aria-hidden="true">
            <i />
            <i />
            <i />
          </span>
          <span>Codex Continuity</span>
        </a>
        <div className="nav-cluster">
          <a className="nav-link nav-section-link" href="#how">How it works</a>
          <a className="nav-link nav-section-link" href="#install">Install</a>
          <a className="nav-link nav-section-link" href="#cli">CLI</a>
          <a
            className="nav-link"
            href="https://github.com/YesterdaysLemon/codex-continuity"
          >
            GitHub <span aria-hidden="true">↗</span>
          </a>
        </div>
      </nav>

      <section className="hero" id="top">
        <div className="hero-copy">
          <div className="eyebrow">
            <span className="status-dot" aria-hidden="true" />
            Unofficial · Windows · Experimental
          </div>
          <h1>
            Keep the agents.
            <span>Replace the window.</span>
          </h1>
          <p className="lede">
            A tiny continuity layer that keeps Codex threads alive while the
            desktop app updates, quits, or restarts.
          </p>
          <div className="actions">
            <a
              className="button button-primary"
              href="https://github.com/YesterdaysLemon/codex-continuity/releases/latest"
            >
              Download for Windows <span aria-hidden="true">↓</span>
            </a>
            <a
              className="button button-secondary"
              href="https://github.com/YesterdaysLemon/codex-continuity"
            >
              View source <span aria-hidden="true">↗</span>
            </a>
          </div>
          <p className="microcopy">Open source · MIT · No desktop patching</p>
        </div>

        <div className="continuity-card" aria-label="Continuity architecture">
          <div className="card-meta">
            <span>LIVE CONTINUITY TRACE</span>
            <span className="live-pill">WIN11 VERIFIED</span>
          </div>
          <div className="node desktop-node">
            <span className="node-index">01</span>
            <div>
              <strong>Desktop UI</strong>
              <small>Safe to restart</small>
            </div>
            <span className="node-state dim">REPLACED</span>
          </div>
          <div className="connection" aria-hidden="true">
            <span>loopback websocket</span>
            <i />
          </div>
          <div className="node server-node">
            <span className="node-index">02</span>
            <div>
              <strong>Agent backend</strong>
              <small>Supervised outside the app</small>
            </div>
            <span className="node-state">ACTIVE</span>
          </div>
          <div className="proof-line">
            <span className="check" aria-hidden="true">✓</span>
            <span>
              This launch page was built inside a thread that survived the
              first restart.
            </span>
          </div>
        </div>
      </section>

      <section className="proof-strip" aria-label="Verified product facts">
        <div>
          <strong>01</strong>
          <span>real restart survived</span>
        </div>
        <div>
          <strong>127.0.0.1</strong>
          <span>loopback only</span>
        </div>
        <div>
          <strong>0</strong>
          <span>desktop files patched</span>
        </div>
        <div>
          <strong>MIT</strong>
          <span>open source</span>
        </div>
      </section>

      <section className="section how-section" id="how">
        <div className="section-heading">
          <span className="section-number">01 / ARCHITECTURE</span>
          <h2>The window was never the work.</h2>
          <p>
            Codex normally launches the process that owns your threads. When
            the window exits, that backend exits with it. Continuity moves one
            process boundary—and changes the failure mode.
          </p>
        </div>
        <div className="steps">
          <article className="step-card">
            <span>01</span>
            <h3>Supervise</h3>
            <p>A tiny background utility starts and watches the official Codex app-server.</p>
          </article>
          <article className="step-card accent-card">
            <span>02</span>
            <h3>Reconnect</h3>
            <p>The desktop talks to that backend over a loopback WebSocket instead of owning it.</p>
          </article>
          <article className="step-card">
            <span>03</span>
            <h3>Update the UI</h3>
            <p>Microsoft Store can replace the window while the externally supervised backend keeps running.</p>
          </article>
        </div>
      </section>

      <section className="section split-section">
        <div className="boundary-card safe-card">
          <span className="section-number">WHAT CHANGES</span>
          <h2>One owner.</h2>
          <ul>
            <li>The app-server runs outside the desktop process tree.</li>
            <li>Future desktop launches reconnect to one loopback endpoint.</li>
            <li>The in-app restart prompt stays out of the way.</li>
          </ul>
        </div>
        <div className="boundary-card quiet-card">
          <span className="section-number">WHAT DOESN’T</span>
          <h2>Your Codex.</h2>
          <ul>
            <li>The official app-server still handles threads and authentication.</li>
            <li>Signed desktop updates still arrive through Microsoft Store.</li>
            <li>No installed app files are patched, replaced, or resigned.</li>
          </ul>
        </div>
      </section>

      <section className="section install-section" id="install">
        <div className="install-copy">
          <span className="section-number">02 / INSTALL</span>
          <h2>One command.<br />No forced restart.</h2>
          <p>
            The bootstrapper verifies the release checksum, runs an isolated
            reconnect check, and installs per-user. If Codex is open, Continuity
            arms itself and waits for a natural close. It never asks for a
            restart or starts a competing backend.
          </p>
          <a
            className="text-link"
            href="https://github.com/YesterdaysLemon/codex-continuity#install"
          >
            Read the full install guide <span aria-hidden="true">↗</span>
          </a>
        </div>
        <div className="terminal" aria-label="PowerShell installation command">
          <div className="terminal-bar">
            <span>PowerShell</span>
            <span>win-x64 / latest stable</span>
          </div>
          <CopyInstallCommand />
          <div className="terminal-foot">
            <span>No admin prompt · SHA-256 verified</span>
            <span>Tray optional with -NoTray</span>
          </div>
        </div>
      </section>

      <section className="section agent-section" id="cli">
        <div>
          <span className="section-number">03 / INSTALLED CLI</span>
          <h2>Inspect.<br />Repair. Remove.</h2>
        </div>
        <div className="agent-copy">
          <p>
            Installation adds one stable command to your user PATH. Open a new
            PowerShell window, then manage Continuity without hunting through
            versioned folders.
          </p>
          <div className="terminal" aria-label="Installed Codex Continuity commands">
            <div className="terminal-bar">
              <span>CodexContinuity</span>
              <span>user PATH / v0.4.0</span>
            </div>
            <pre><code>CodexContinuity status{"\n"}CodexContinuity probe{"\n"}CodexContinuity repair{"\n"}CodexContinuity uninstall</code></pre>
            <div className="terminal-foot">
              <span>Uninstall never stops active agents</span>
              <span>Files leave at next sign-in</span>
            </div>
          </div>
        </div>
      </section>

      <section className="section agent-section" id="agents">
        <div>
          <span className="section-number">04 / AGENT DISCOVERY</span>
          <h2>Inspect first.<br />Then act.</h2>
        </div>
        <div className="agent-copy">
          <p>
            Agents and automation can read a mutation-free JSON plan before
            downloading anything. The machine-readable guide lives at
            <a href="/llms.txt"> /llms.txt</a>.
          </p>
          <pre><code>powershell.exe -NoProfile -ExecutionPolicy Bypass -File $i -Plan -Json</code></pre>
        </div>
      </section>

      <section className="section proof-section">
        <div className="proof-quote">
          <span className="quote-mark" aria-hidden="true">“</span>
          <blockquote>
            This page and the first public release were built inside the same
            Codex thread after its desktop window restarted.
          </blockquote>
          <p>Verified migration run · Windows 11 · August 20, 2026</p>
        </div>
        <div className="proof-details">
          <div><span>Desktop backend child</span><strong>none</strong></div>
          <div><span>External backend</span><strong className="green">active</strong></div>
          <div><span>Thread after restart</span><strong className="green">continued</strong></div>
        </div>
      </section>

      <section className="section warning-section">
        <span className="warning-symbol" aria-hidden="true">!</span>
        <div>
          <span className="section-number">EXPERIMENTAL BOUNDARY</span>
          <h2>Useful, reversible, unofficial.</h2>
          <p>
            Codex Continuity relies on experimental app-server transport and
            undocumented desktop environment hooks. A future Codex release may
            change them. v0.4.0 supports Windows 11 x64 only; there is no macOS
            or Linux build today. The utility is deliberately small,
            fail-closed, and removable with one command.
          </p>
        </div>
        <a className="button button-secondary" href="https://github.com/YesterdaysLemon/codex-continuity/blob/main/REVERSE_ENGINEERING.md">
          See the evidence <span aria-hidden="true">↗</span>
        </a>
      </section>

      <section className="final-cta">
        <span className="section-number">READY WHEN THE WINDOW ISN’T</span>
        <h2>Keep the long-running work.</h2>
        <div className="actions">
          <a className="button button-primary" href="https://github.com/YesterdaysLemon/codex-continuity/releases/latest">
            Download latest <span aria-hidden="true">↓</span>
          </a>
          <a className="button button-secondary" href="https://github.com/YesterdaysLemon/codex-continuity">
            Star on GitHub <span aria-hidden="true">↗</span>
          </a>
          <a className="button button-secondary" href="https://github.com/sponsors/YesterdaysLemon">
            Sponsor <span aria-hidden="true">♥</span>
          </a>
        </div>
      </section>

      <footer>
        <a className="brand" href="#top">
          <span className="brand-mark" aria-hidden="true"><i /><i /><i /></span>
          <span>Codex Continuity</span>
        </a>
        <p>
          Built by <a href="https://alirezaafshan.com">Alireza Afshan</a> for
          work that takes longer than a window.
        </p>
        <div>
          <a href="https://alirezaafshan.com/projects">More projects</a>
          <a href="https://github.com/YesterdaysLemon/codex-continuity">Source</a>
          <a href="https://github.com/YesterdaysLemon/codex-continuity/blob/main/LICENSE">MIT License</a>
        </div>
      </footer>
    </main>
  );
}
