# syntax=docker/dockerfile:1.7
#
# Spencer Denim frontend images (W0-15, docs/15-deployment-runbook.md). Build from the repository root:
#
#   docker build -f deploy/docker/frontend.Dockerfile --target storefront -t spencer-denim/storefront:<tag> .
#   docker build -f deploy/docker/frontend.Dockerfile --target web        -t spencer-denim/web:<tag> .
#
# storefront = the Angular SSR server (Node). web = the gateway: nginx with the admin app's static build baked in,
# routing /api to the API, /admin to those files and everything else to the storefront.
#
# The build context is filtered by frontend.Dockerfile.dockerignore (an allow-list). Base images are pinned.

ARG NODE_IMAGE=node:24.21.0-alpine3.24
ARG NGINX_IMAGE=nginxinc/nginx-unprivileged:1.30.5-alpine3.24

# ---------------------------------------------------------------------------------------------------------------
# deps: npm ci from the lockfile only, so the layer is reused until a dependency changes.
# ---------------------------------------------------------------------------------------------------------------
FROM ${NODE_IMAGE} AS deps
WORKDIR /src/frontend
COPY src/frontend/package.json src/frontend/package-lock.json ./
RUN --mount=type=cache,id=sd-npm,target=/root/.npm \
    npm ci --no-audit --no-fund

# ---------------------------------------------------------------------------------------------------------------
# build: both applications. The shared library is consumed from source (tsconfig paths), so it needs no build.
# ---------------------------------------------------------------------------------------------------------------
FROM deps AS build
ENV NG_CLI_ANALYTICS=false
COPY src/frontend/angular.json src/frontend/tsconfig.json ./
COPY src/frontend/projects/ projects/
RUN npx ng build storefront --configuration production \
 && npx ng build admin --configuration production

# ---------------------------------------------------------------------------------------------------------------
# storefront: the SSR server. The server bundle is self-contained (no node_modules needed at run time).
# ---------------------------------------------------------------------------------------------------------------
FROM ${NODE_IMAGE} AS storefront
ENV NODE_ENV=production \
    PORT=4000
WORKDIR /app
COPY --from=build --chown=root:root /src/frontend/dist/storefront/ ./dist/storefront/
USER node
EXPOSE 4000
HEALTHCHECK --interval=15s --timeout=5s --start-period=15s --retries=5 \
  CMD wget -q -O /dev/null http://127.0.0.1:4000/healthz || exit 1
CMD ["node", "dist/storefront/server/server.mjs"]

# ---------------------------------------------------------------------------------------------------------------
# web: the gateway. Runs as nginx's unprivileged user on 8080; its config is rendered from environment variables
# into /tmp at start-up, so the root filesystem can be read-only.
# ---------------------------------------------------------------------------------------------------------------
FROM ${NGINX_IMAGE} AS web
USER root
RUN rm -f /etc/nginx/conf.d/default.conf
COPY deploy/gateway/nginx.conf /etc/nginx/nginx.conf
COPY deploy/gateway/templates/ /etc/nginx/templates/
COPY --from=build /src/frontend/dist/admin/browser/ /usr/share/nginx/admin/
RUN chown -R root:root /usr/share/nginx/admin /etc/nginx/templates
USER 101
# Rendered by the image's own entrypoint (20-envsubst-on-templates.sh) into /tmp/gateway/*.conf.
ENV NGINX_ENVSUBST_OUTPUT_DIR=/tmp \
    NGINX_ENVSUBST_FILTER=^SD_ \
    SD_API_UPSTREAM=api:8080 \
    SD_STOREFRONT_UPSTREAM=storefront:4000 \
    SD_REAL_IP_FROM=127.0.0.1/32 \
    SD_MAX_BODY_SIZE=60m
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=5 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/gateway-health || exit 1
