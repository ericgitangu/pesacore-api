# Security headers — INCLUDED into every location block.
# nginx `add_header` does NOT inherit into a location that declares its own
# add_header (e.g. Cache-Control), so the server-level security headers are
# silently dropped there. Re-include this snippet in each location so the full
# security envelope is present on every response. (.inc, not .conf, so nginx's
# `include conf.d/*.conf` does not auto-load it as a server.)
add_header Strict-Transport-Security "max-age=63072000; includeSubDomains; preload" always;
add_header X-Content-Type-Options "nosniff" always;
add_header X-Frame-Options "DENY" always;
add_header Referrer-Policy "strict-origin-when-cross-origin" always;
add_header Permissions-Policy "camera=(), microphone=(), geolocation=(), payment=()" always;
# script-src needs 'unsafe-inline' because Blazor WASM ships an INLINE <script type="importmap">
# (governed by script-src; CSP3 requires 'unsafe-inline' or a hash for it). Without it the
# runtime can't resolve dotnet.js and hangs at "Initializing runtime…". 'wasm-unsafe-eval' lets
# the .NET runtime instantiate the .wasm. PROD-HARDEN: replace 'unsafe-inline' with a per-build
# hash/nonce of the importmap (ASP.NET Core <ImportMap> + MapStaticAssets) — documented trade-off.
add_header Content-Security-Policy "default-src 'self'; script-src 'self' 'wasm-unsafe-eval' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'" always;
