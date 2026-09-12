# =============================================================================
# PolicyFlow React SPA
#
# Multi-stage: Node builds, nginx serves. The runtime image contains only static
# files and a web server — no Node, no node_modules, no source. That is a ~25MB
# image instead of ~1.2GB, and nothing in it can execute JavaScript server-side.
#
# Built from this directory:
#   docker build -f src/PolicyFlow.Web/Dockerfile src/PolicyFlow.Web
# =============================================================================

# -----------------------------------------------------------------------------
# Stage 1: build the bundle
# -----------------------------------------------------------------------------
FROM node:24-alpine AS build
WORKDIR /app

# Manifests first, so the dependency install layer is cached independently of
# source changes.
COPY package.json package-lock.json ./

# `npm ci`, not `npm install`: it installs exactly the locked versions and fails
# if the lockfile disagrees with package.json. A build that silently resolves a
# different dependency tree than the one that was tested is not reproducible.
RUN npm ci

COPY tsconfig*.json vite.config.ts index.html ./
COPY src/ ./src/

# Typechecks (tsc -b) and bundles. A type error fails the image build rather than
# shipping broken code.
RUN npm run build

# -----------------------------------------------------------------------------
# Stage 2: serve
# -----------------------------------------------------------------------------
FROM nginx:1.27-alpine AS runtime

# Replace the default site with ours: SPA fallback routing plus the /api proxy.
RUN rm /etc/nginx/conf.d/default.conf
COPY nginx.conf                  /etc/nginx/conf.d/policyflow.conf
# The security-header snippet, included by every location block that adds headers
# of its own. Named .conf but never loaded as a server on its own — nginx only
# auto-includes conf.d/*.conf at the http level, and this file contains only
# add_header directives, which are valid there.
COPY nginx-security-headers.conf /etc/nginx/conf.d/policyflow-security-headers.conf

COPY --from=build /app/dist /usr/share/nginx/html

# Port 8080, not 80. Ports below 1024 need elevated privileges, and this
# container has no reason to run with them.
EXPOSE 8080

# 127.0.0.1, not localhost. In this image `localhost` resolves to the IPv6 ::1
# first, while nginx listens only on IPv4 0.0.0.0:8080 — so the probe gets
# "connection refused" and the container is reported unhealthy while actually
# serving traffic perfectly well.
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD wget --quiet --tries=1 --spider http://127.0.0.1:8080/ || exit 1

CMD ["nginx", "-g", "daemon off;"]
