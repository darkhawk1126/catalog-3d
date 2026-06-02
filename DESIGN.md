# catalog-3d — Design & Decision Record

STL hosting service with ACL-gated preview/download and a conditional static/3D viewer.
Replaces a shoehorned MediaWiki STL setup. C# backend, k3s homelab, HTTPS ingress.

> This is the agentic build reference. Decisions here are settled unless explicitly revisited.

## Settled decisions

| Area | Decision |
|------|----------|
| Backend | ASP.NET Core (.NET), API-first |
| DB | PostgreSQL in **both** dev and prod (no SQLite divergence), EF Core |
| Blob storage | On-disk via `IFileStore` abstraction (swappable to MinIO/S3). Bind-mount dev, PVC prod |
| Auth | ASP.NET Core authentication schemes behind one `IUserContext`. Dev = config-hardcoded users; Prod = OIDC (Authelia). API-key scheme slot reserved for future programmatic/wiki access |
| ACL model | **Collection-level RBAC.** OIDC groups → roles, scoped per collection |
| Preview | Static PNG = unauthorized/preview tier; interactive viewer = download tier (see Security model). Thumbnails precomputed at upload by a **render sidecar** (f3d) |
| Admin/Upload UI | **Blazor Server**, hosted in the same app, calling application services directly |
| Embeddable viewer | Separate JS bundle (three.js + STLLoader), iframe-able route, download-gated |
| MediaWiki future | **Design API-first, defer embed mechanism.** Commit only to a versioned JSON API + stable slug URLs. Keep "embed our viewer" vs "feed the existing Wikimedia 3D extension" both open |

## Security model — the cornerstone

The interactive viewer is **download-equivalent**: three.js needs raw geometry client-side, so
viewing interactively ⟺ obtaining the STL. Preview tiers therefore map onto the ACL, not file size.

Per-collection roles (a principal — OIDC group or dev user — is assigned one):

- **Admin** — manage collection, models, ACL assignments; all file access.
- **Uploader** — upload new models (⊇ Download).
- **Download** — STL access: interactive viewer **and** file download.
- **Preview** — listing + static PNG thumbnail only. STL bytes never sent to this browser.
- *(none)* — collection not listed (404 / omitted).

Consequences:
- Every model gets a precomputed static thumbnail at upload — it is the Preview-tier artifact, so the render sidecar is a security floor, not an enhancement.
- The geometry/file endpoint and the `/viewer/...` route are **Download**-gated; the thumbnail endpoint is **Preview**-gated.
- File-size thresholding is a UX nicety inside the Download tier only (lazy-load big models), never a security boundary.

## Data model (initial)

- **Collection** — id, slug, name, description, timestamps.
- **Model** — id, collection_id, slug, name, description, owner, status (`processing`/`ready`/`failed`), timestamps. A model may hold multiple files (variants, e.g. "with plinth").
- **ModelFile** — id, model_id, kind (`stl`/`thumbnail`/`other`), blob key (content hash), size, mime, sha256, tri_count, bbox, render_status. Stored content-addressed; public URLs are by slug, not hash (stable for the wiki).
- **RoleAssignment** — collection_id, principal (group/user id), role.
- **RenderJob** (optional explicit) — model_file_id, state, attempts.

Blob layout on disk: `<root>/blobs/<ab>/<full-hash>.stl`, `<root>/thumbs/<ab>/<full-hash>.png`.
DB maps slug → file → blob key. Content-addressing gives dedup + integrity; slug URLs give stable external links.

## Component / project layout

- `Catalog3d.Domain` — entities, role enums, value objects. No deps.
- `Catalog3d.Application` — use cases + interfaces: `IFileStore`, `IUserContext`, `IRenderQueue`, ACL resolution.
- `Catalog3d.Infrastructure` — EF Core (Postgres), filesystem `IFileStore`, dev + OIDC auth, render-sidecar HTTP client.
- `Catalog3d.Web` — host: versioned JSON API, Blazor Server admin/upload, embeddable viewer route, authz policies + resource-based collection handler.
- `render-sidecar` — separate container image wrapping **f3d** headless (`stl → png`) behind a tiny HTTP shim; shares the blob volume for I/O.

(Layering justified by the explicit pluggability requirements — auth, storage, render. Can collapse if it proves heavy.)

## API surface (v1, the durable contract)

- `GET  /api/v1/collections` — filtered to caller's Preview+ collections
- `GET  /api/v1/collections/{slug}`
- `GET  /api/v1/collections/{slug}/models`
- `GET  /api/v1/models/{slug}` — metadata + tiers available to caller
- `GET  /api/v1/models/{slug}/thumbnail` — Preview-gated PNG
- `GET  /api/v1/models/{slug}/files/{fileId}` — Download-gated STL stream
- `GET  /viewer/{fileId}` — Download-gated embeddable HTML viewer
- `POST /api/v1/collections/{slug}/models` — Uploader-gated, **streaming** multipart (never buffer in memory)
- Admin: collection CRUD, role assignment.

Stable slugs, versioned prefix. This is the commitment that keeps the wiki future open.

## Deployment (k3s)

- One Deployment, pod with two containers: `app` (ASP.NET Core + Blazor) + `render` (f3d sidecar).
- Shared PVC mounted into both for blob/thumbnail I/O.
- Postgres: homelab instance or StatefulSet.
- Ingress with HTTPS (traefik + cert-manager).
- Config via env/Secrets: OIDC (Authelia) client, DB connection, storage root.
- Dev: docker-compose — app + postgres + render sidecar + bind-mounted `./data`.

## Build sequence

1. **Walking skeleton** — solution, Postgres + EF Core, Collection/Model, dev auth scheme, list + streaming upload (no render yet), on-disk `IFileStore`, docker-compose. ACL enforced from the start.
2. **RBAC + auth** — per-collection role assignments, resource-based authorization, OIDC (Authelia) scheme wired alongside dev users.
3. **Preview pipeline** — render sidecar (f3d), upload→render job→thumbnail, Preview-gated thumbnail endpoint, Download-gated interactive viewer route + JS bundle.
4. **Blazor admin/upload UI** — collections, ACLs, group→role mapping, upload with progress + render status.
5. **K8s manifests** — 2-container Deployment, PVC, ingress+TLS, Secrets, Authelia OIDC.
6. **Harden** — upload streaming limits, API versioning polish, embed CSP/X-Frame knobs (deferred wiki embed).

## Authelia / OIDC integration (resolved from the live homelab)

- **Prod issuer**: `https://login.mallcop.dev` (Authelia **4.39.13**). Discovery at `/.well-known/openid-configuration`.
- **Dev**: a **local Authelia 4.39.13** in docker-compose is the dev OIDC source — self-contained, never touches the live instance.
- **Groups claim**: name `groups`, **JSON string array**, delivered via the **UserInfo endpoint** (prod has no `claims_policy`, so groups are *not* in the ID token). → OIDC handler sets `GetClaimsFromUserInfoEndpoint = true` and requests the `groups` scope. No Authelia change required.
- **Client (house style — immich variant)**: confidential (`public: false`), PKCE `S256`, `token_endpoint_auth_method: client_secret_post`, scopes `[openid, profile, email, groups]`, grants `[authorization_code, refresh_token]`, response `[code]`. ASP.NET Core callback `/signin-oidc`; prod redirect `https://catalog.mallcop.dev/signin-oidc`. **No `end_session_endpoint`** → local sign-out only.
- **Authorization ownership**: catalog-3d owns authz. Authelia groups are **coarse and shared** across the homelab (only `admins`, `media`, `games` exist) — do **not** add catalog-specific groups to Authelia. Mapping: config-driven `Authorization:SiteAdminGroups` (default `[admins]`) → Admin everywhere; all finer grants are `RoleAssignment` rows in catalog's DB. Principal convention: `user:<oidc-sub>` and `group:<name>`.
- **Scheme selection**: config-driven `Auth:Provider = Dev | Oidc`; both schemes registered so dev can run hardcoded identity *or* OIDC against local Authelia. Dev-over-http sets `RequireHttpsMetadata = false` in Development only.

### Deployment wiring (deferred to milestone 5 — do not touch the homelab repo before then)
- Add `catalog-3d` client to `charts/infra/authelia/values.yaml` (`client_secret` as `$pbkdf2-sha512$310000$…` hash); app-side plaintext as a SealedSecret in catalog's namespace.
- GitOps: edit repo → push to self-hosted remote `insta@10.13.1.30:/main/documents/git/homelab.git` → Argo CD syncs. SealedSecrets via kube-system controller.
- Ingress: Traefik (`ingressClassName: traefik`, `websecure`) + cert-manager `letsencrypt-prod` (DNS-01 Porkbun), host `catalog.mallcop.dev`, TLS secret `catalog-3d-tls`, LB `10.13.1.15`. **No forward-auth middleware** (OIDC-native); pick `traefik-internal-only` or `traefik-crowdsec-bouncer` by exposure.
- **DataProtection key ring**: dev uses ephemeral keys (auth cookies/anti-forgery don't survive restart). Production MUST persist the key ring (PVC or k8s Secret) or auth cookies break across pod restarts/replicas.

## Open / deferred

- Wiki embed mechanism (viewer-iframe vs feed-the-3D-extension) — decide when the plugin is actually built.
- f3d vs Blender-headless for render — f3d is the default pick (single binary, headless STL→PNG); revisit only if quality/format needs exceed it.
- MinIO migration — left open via `IFileStore`; not built now.
