# Production website deployment

The public site uses the VPS deploy manager because OpenAI Sites currently
offers interactive production publishing and short-lived source credentials,
not a durable unattended identity for GitHub Actions.

## Trust and sequencing

- `.github/workflows/deploy-site.yml` runs only after the repository `CI`
  workflow succeeds for a push to `main`.
- The deploy job checks out the exact successful SHA, repeats the site build,
  lint, rendered tests, and container build, then signs that SHA for the VPS.
- GitHub Actions lets green deployment jobs overlap so every successful CI
  completion reaches the deploy manager. Its per-app lock serializes mutations;
  waiting jobs retry, and any stale SHA is rejected after the manager fetches
  the current `main`.
- The deploy manager independently fetches `main`, requires its HEAD to equal
  the signed SHA, asks the candidate `/healthz` endpoint to verify the homepage,
  `/llms.txt`, and a 40-hex source revision before swapping production, and
  retains the previous image for rollback.
- The workflow accepts success only when the manager echoes the requested SHA,
  and the canonical custom domain exposes that SHA at `/deploy-revision.txt`,
  the expected release, and `/llms.txt`.

## Isolated VPS allocation

The Continuity app has its own allowlist entry, webhook secret, dedicated
loopback webhook listener, repository checkout, deployment environment file,
containers, loopback ports, log, lock, and Caddy route. It must not reuse the
shared listener's app registry or the `website` app entry or secret.

| Setting | Value |
| --- | --- |
| App ID | `continuity` |
| Repository | `YesterdaysLemon/codex-continuity` |
| Branch | `main` |
| Checkout | `/opt/codex-continuity/app` |
| Production container | `codex-continuity-site` |
| Candidate container | `codex-continuity-site-candidate` |
| Production port | `3040` |
| Candidate port | `3041` |
| Container port | `8080` |
| Webhook listener | `127.0.0.1:9020` |
| Health path | `/healthz` |

The GitHub repository stores the deploy URL and HMAC secret as
`CONTINUITY_DEPLOY_WEBHOOK_URL` and `CONTINUITY_DEPLOY_WEBHOOK_SECRET` Actions
secrets. The same HMAC secret exists only in the root-owned deploy-manager
environment on the VPS.

## Rollback

For an application rollback after a successful deployment, revert the bad
commit on `main`. The revert becomes a new green SHA and follows the same
candidate health gate; historical SHAs cannot bypass the exact-current-`main`
rule. If a newly started production container fails its health check during a
deployment, the manager automatically restores the previous image.

For a routing rollback, first confirm that Sites still retains the
`continuity.alirezaafshan.com` custom-domain attachment and publish the intended
release there. Only then restore the domain to the Sites CNAME target
`custom-domains.chatgpt.site.`. The obsolete slug URL is not a production
health signal and must not be used to validate the rollback.
