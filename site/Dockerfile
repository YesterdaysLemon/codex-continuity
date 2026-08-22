FROM node:24-bookworm-slim AS build

ARG DEPLOY_REVISION=""
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*
COPY site/package.json site/package-lock.json ./
RUN npm ci
COPY site/ ./
COPY .git /tmp/source-git
# Vinext beta standalone output does not yet trace its React peer runtime.
RUN source_revision="$(git --git-dir=/tmp/source-git rev-parse HEAD 2>/dev/null || true)" \
    && case "$DEPLOY_REVISION" in \
      "") revision="$source_revision" ;; \
      *[!0123456789abcdefABCDEF]*) \
        echo "DEPLOY_REVISION must be a 40-character hexadecimal commit SHA" >&2; exit 64 ;; \
      *) \
        if [ "${#DEPLOY_REVISION}" -ne 40 ]; then \
          echo "DEPLOY_REVISION must be a 40-character hexadecimal commit SHA" >&2; exit 64; \
        fi; \
        revision="$DEPLOY_REVISION" ;; \
    esac \
    && if [ -n "$source_revision" ] && [ -n "$DEPLOY_REVISION" ] && [ "$source_revision" != "$DEPLOY_REVISION" ]; then \
      echo "DEPLOY_REVISION does not match the source commit" >&2; exit 64; \
    fi \
    && case "$revision" in \
      ""|*[!0123456789abcdefABCDEF]*) \
        echo "could not resolve a 40-character source revision" >&2; exit 64 ;; \
    esac \
    && if [ "${#revision}" -ne 40 ]; then \
      echo "could not resolve a 40-character source revision" >&2; exit 64; \
    fi \
    && printf '%s\n' "$revision" > public/deploy-revision.txt \
    && npm run build \
    && cp -a node_modules/react node_modules/react-dom node_modules/scheduler \
      dist/standalone/node_modules/

FROM node:24-bookworm-slim

ENV HOST=0.0.0.0 \
    NODE_ENV=production \
    PORT=8080
WORKDIR /app
COPY --from=build --chown=node:node /app/dist/standalone ./
USER node
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["node", "-e", "fetch('http://127.0.0.1:8080/healthz').then(r => { if (!r.ok) process.exit(1); }).catch(() => process.exit(1));"]
CMD ["node", "server.js"]
