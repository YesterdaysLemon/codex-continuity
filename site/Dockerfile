FROM node:24-bookworm-slim AS build

WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*
COPY site/package.json site/package-lock.json ./
RUN npm ci
COPY site/ ./
COPY .git /tmp/source-git
# Vinext beta standalone output does not yet trace its React peer runtime.
RUN if [ -d /tmp/source-git ]; then \
      git --git-dir=/tmp/source-git rev-parse HEAD; \
    else \
      printf 'unknown\n'; \
    fi > public/deploy-revision.txt \
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
  CMD ["node", "-e", "fetch('http://127.0.0.1:8080/llms.txt').then(r => { if (!r.ok) process.exit(1); }).catch(() => process.exit(1));"]
CMD ["node", "server.js"]
