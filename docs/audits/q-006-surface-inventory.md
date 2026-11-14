# Q-006 · Cross-tenant isolation audit — surface inventory

Card Q-006's first deliverable: every tenant-reachable surface of the Zenkins server, enumerated
before the probes ran. The audit rule is **enumerate before probing** — a probe that is missing is a
visible gap, and an endpoint that is missing from this list is a test failure by construction.

The document is machine-checked (TC-001). `TenantIsolationInventoryTests` boots the real `Program`
pipeline, enumerates the built `EndpointDataSource` (minimal APIs **and** Blazor SSR pages) plus the
SignalR hub methods by reflection, and diffs both directions against the tables below:

- every registered endpoint/hub method must appear exactly once here, and
- every documented route/method must still be registered (no stale rows).

Every tenant-reachable row also names the probe test that pins its refusal contract; the inventory
test reflects over the test assembly and fails when a named probe does not exist.

Conventions: routes are spelled exactly as the router reports them (`:guid` constraints and the
PascalCase Blazor parameters included). `GET/POST` is how Blazor SSR pages register. The tenant that
a request's data reads run under is named in the "Tenant selection" column — it is always a server
side value (cookie session, token row, ticket or webhook row), never a client-controlled id.

## HTTP surfaces

| Method | Route | Tenant selection | Refusal contract | Probe |
|---|---|---|---|---|
| GET/POST | / | cookie session → active organization | reads only the active org's dashboard rows | Tenant_pages_render_only_the_active_organizations_rows |
| GET/POST | /no-organization | cookie session (no org selected) | shell state page, no tenant rows | — |
| GET/POST | /secrets | cookie session → active organization | lists only the active org's secret names, never values | Tenant_pages_render_only_the_active_organizations_rows |
| GET/POST | /workers | cookie session → active organization | lists only the active org's workers | Tenant_pages_render_only_the_active_organizations_rows |
| GET/POST | /workers/{WorkerId:guid} | cookie session → active organization | foreign/unknown worker id = real 404, no existence leak | Foreign_tenant_pages_answer_404_without_existence_leaks |
| GET/POST | /projects | cookie session → active organization | lists only the active org's projects | Tenant_pages_render_only_the_active_organizations_rows |
| GET/POST | /projects/new | cookie session (create form) | creates inside the active org only | — |
| GET/POST | /projects/{ProjectId:guid} | cookie session → active organization | foreign/unknown id = 404, no existence leak | Foreign_tenant_pages_answer_404_without_existence_leaks |
| GET/POST | /projects/{ProjectId:guid}/runs | cookie session → active organization | foreign/unknown id = 404 | Foreign_tenant_pages_answer_404_without_existence_leaks |
| GET/POST | /projects/{ProjectId:guid}/pipelines | cookie session → active organization | foreign/unknown id = 404 | Foreign_tenant_pages_answer_404_without_existence_leaks |
| GET/POST | /projects/{ProjectId:guid}/settings | cookie session → active organization | foreign/unknown id = 404; renders this project's webhook/credential forms only | Foreign_tenant_pages_answer_404_without_existence_leaks |
| GET/POST | /projects/{ProjectId:guid}/edit | cookie session → active organization | foreign/unknown id = 404 | Foreign_tenant_pages_answer_404_without_existence_leaks |
| GET/POST | /projects/{ProjectId:guid}/delete/confirm | cookie session → active organization | foreign/unknown id = 404 | Foreign_tenant_pages_answer_404_without_existence_leaks |
| GET/POST | /runs/{RunId:guid} | cookie session → active organization | foreign/unknown run id = real 404; renders this run's steps/artifacts/log only | Foreign_tenant_pages_answer_404_without_existence_leaks |
| GET/POST | /organizations/members | cookie session → active organization | lists only the active org's memberships | Tenant_pages_render_only_the_active_organizations_rows |
| GET/POST | /organizations/settings | cookie session → active organization | renames the active org only | Active_organization_forms_target_only_the_active_tenant |
| GET/POST | /account/login | anonymous sign-in form (no tenant rows) | — | — |
| GET/POST | /account/logout | anonymous sign-out (no tenant rows) | — | — |
| GET/POST | /account/register | anonymous first-run registration (no tenant rows) | — | — |
| GET/POST | /error | exception-handler re-execution target (no tenant rows) | — | — |
| GET/POST | /not-found | status-page re-execution target (no tenant rows) | — | — |
| POST | /organizations/switch | membership of the session user (never the form value) | non-member target = 403, previous selection kept | Switching_to_an_organization_without_membership_is_refused_and_keeps_the_selection |
| POST | /organizations/settings | cookie session → active organization (Owner-only) | Member = 403; acts on the active org only | Active_organization_forms_target_only_the_active_tenant |
| POST | /organizations/members/invite | cookie session → active organization (Owner-only) | invites into the active org only; the target account's other memberships untouched | Active_organization_forms_target_only_the_active_tenant |
| POST | /organizations/members/role | cookie session → active organization (Owner-only); membership id resolved through the tenant filter | foreign/unknown membership id = 404, row untouched | Member_management_cannot_touch_another_organizations_membership |
| POST | /organizations/members/remove | cookie session → active organization (Owner-only); membership id resolved through the tenant filter | foreign/unknown membership id = 404, row untouched | Member_management_cannot_touch_another_organizations_membership |
| POST | /projects/create | cookie session → active organization | creates inside the active org only | — |
| POST | /projects/{projectId:guid}/edit | cookie session → active organization; route id is authoritative | foreign/unknown id = 404, no existence leak | Foreign_tenant_pages_answer_404_without_existence_leaks |
| POST | /projects/{projectId:guid}/delete | cookie session → active organization; route id is authoritative | foreign/unknown id = 404 | Foreign_tenant_pages_answer_404_without_existence_leaks |
| POST | /secrets/create | cookie session → active organization (Owner-only) | stores ciphertext in the active org only | Secret_mutations_cannot_reach_another_tenants_secret |
| POST | /secrets/{secretId:guid}/rotate | cookie session → active organization (Owner-only); id resolved through the tenant filter | foreign/unknown id = bodyless 404, row untouched | Secret_mutations_cannot_reach_another_tenants_secret |
| POST | /secrets/{secretId:guid}/delete | cookie session → active organization (Owner-only); id resolved through the tenant filter | foreign/unknown id = bodyless 404, row untouched | Secret_mutations_cannot_reach_another_tenants_secret |
| POST | /git-credentials/create | cookie session → active organization (Owner-only) | stores ciphertext in the active org only | Git_credential_mutations_cannot_reach_another_tenants_credential |
| POST | /git-credentials/{credentialId:guid}/rotate | cookie session → active organization (Owner-only); id resolved through the tenant filter | foreign/unknown id = bodyless 404, row untouched | Git_credential_mutations_cannot_reach_another_tenants_credential |
| POST | /git-credentials/{credentialId:guid}/delete | cookie session → active organization (Owner-only); id resolved through the tenant filter | foreign/unknown id = bodyless 404, row untouched | Git_credential_mutations_cannot_reach_another_tenants_credential |
| GET | /projects/{projectId:guid}/git-credentials | cookie session → active organization (Owner-only); project resolved through the tenant filter | foreign/unknown project = 404, no credential metadata | The_settings_credential_list_of_another_tenants_project_is_a_bare_404 |
| POST | /workers/registration-tokens | cookie session → active organization (Owner-only) | mints a token bound to the active org | Worker_management_cannot_reach_another_tenants_worker_or_token |
| POST | /workers/registration-tokens/{tokenId:guid}/revoke | cookie session → active organization (Owner-only); token id resolved through the tenant filter | foreign/unknown token id = 404, row untouched | Worker_management_cannot_reach_another_tenants_worker_or_token |
| POST | /workers/{workerId:guid}/disable | cookie session → active organization (Owner-only); worker id resolved through the tenant filter | foreign/unknown worker id = 404, row untouched | Worker_management_cannot_reach_another_tenants_worker_or_token |
| POST | /workers/{workerId:guid}/enable | cookie session → active organization (Owner-only); worker id resolved through the tenant filter | foreign/unknown worker id = 404, row untouched | Worker_management_cannot_reach_another_tenants_worker_or_token |
| POST | /workers/{workerId:guid}/delete | cookie session → active organization (Owner-only); worker id resolved through the tenant filter | foreign/unknown worker id = 404, row untouched | Worker_management_cannot_reach_another_tenants_worker_or_token |
| POST | /projects/{projectId:guid}/runs/start | cookie session → active organization; project resolved through the tenant filter | foreign/unknown project = 404 | Starting_a_run_on_another_tenants_project_is_a_bare_404 |
| POST | /runs/{runId:guid}/cancel | cookie session → active organization; run resolved through the tenant filter | foreign/unknown run = 404, row untouched | Canceling_another_tenants_run_is_a_bare_404_and_touches_nothing |
| POST | /projects/{projectId:guid}/webhooks/create | cookie session → active organization (Owner-only); project resolved through the tenant filter | foreign/unknown project = 404 | Webhook_management_cannot_reach_another_tenants_webhook |
| POST | /projects/{projectId:guid}/webhooks/{webhookId:guid}/rotate | cookie session → active organization (Owner-only); both ids resolved through the tenant filter | foreign/unknown ids = 404, row untouched | Webhook_management_cannot_reach_another_tenants_webhook |
| POST | /projects/{projectId:guid}/webhooks/{webhookId:guid}/enable | cookie session → active organization (Owner-only); both ids resolved through the tenant filter | foreign/unknown ids = 404, row untouched | Webhook_management_cannot_reach_another_tenants_webhook |
| POST | /projects/{projectId:guid}/webhooks/{webhookId:guid}/disable | cookie session → active organization (Owner-only); both ids resolved through the tenant filter | foreign/unknown ids = 404, row untouched | Webhook_management_cannot_reach_another_tenants_webhook |
| POST | /projects/{projectId:guid}/webhooks/{webhookId:guid}/delete | cookie session → active organization (Owner-only); both ids resolved through the tenant filter | foreign/unknown ids = 404, row untouched | Webhook_management_cannot_reach_another_tenants_webhook |
| GET | /runs/{runId:guid}/log/download | cookie session → active organization; run resolved through the tenant filter | foreign/unknown run = bare 404, no log bytes, no existence oracle | Log_download_refuses_a_foreign_run_without_bytes_or_oracle |
| GET | /runs/{runId:guid}/artifacts/{artifactId:guid}/download | cookie session → active organization; artifact+run resolved through the tenant filter | foreign/unknown ids = bare 404, no bytes, no existence oracle | Artifact_download_refuses_a_foreign_artifact_without_oracle |
| POST | /api/workers/register | `Authorization: Bearer zreg_…`; the registration-token row selects the tenant | bad/absent token = bare 401 before validation | The_claim_never_crosses_the_tenant_boundary |
| POST | /api/workers/heartbeat | `Authorization: Bearer zwrk_…`; the token row selects the tenant | bad/absent token = bare 401; cancel signals only for this worker's runs | The_heartbeat_cancel_channel_never_carries_another_organizations_run |
| POST | /api/workers/claim | `Authorization: Bearer zwrk_…`; the token row selects the tenant | claims only the token tenant's queued runs (204 when none) | The_claim_never_crosses_the_tenant_boundary |
| POST | /api/workers/runs/{runId:guid}/accepted | `Authorization: Bearer zwrk_…`; run resolved through the token tenant | foreign/unknown run = 404 | Run_report_endpoints_refuse_a_foreign_token_with_a_bare_404 |
| POST | /api/workers/runs/{runId:guid}/definition | `Authorization: Bearer zwrk_…`; run resolved through the token tenant | foreign/unknown run = 404 | Run_report_endpoints_refuse_a_foreign_token_with_a_bare_404 |
| POST | /api/workers/runs/{runId:guid}/steps | `Authorization: Bearer zwrk_…`; run resolved through the token tenant | foreign/unknown run = 404 | Run_report_endpoints_refuse_a_foreign_token_with_a_bare_404 |
| POST | /api/workers/runs/{runId:guid}/completed | `Authorization: Bearer zwrk_…`; run resolved through the token tenant | foreign/unknown run = 404 | Run_report_endpoints_refuse_a_foreign_token_with_a_bare_404 |
| POST | /api/workers/runs/{runId:guid}/canceled | `Authorization: Bearer zwrk_…`; run resolved through the token tenant | foreign/unknown run = 404 | Run_report_endpoints_refuse_a_foreign_token_with_a_bare_404 |
| POST | /api/workers/runs/{runId:guid}/secrets | `Authorization: Bearer zwrk_…`; run resolved through the token tenant; run must be this worker's and non-terminal | every miss (foreign run, foreign name, other worker's run, ended run) = one bodyless 404; value travels only to the executing worker with `Cache-Control: no-store` | The_run_secret_delivery_never_crosses_the_tenant_boundary |
| GET | /api/workers/runs/{runId:guid}/git-credential | `Authorization: Bearer zwrk_…`; run resolved through the token tenant; run must be this worker's and non-terminal | every miss = one bodyless 404; material travels only to the executing worker with `Cache-Control: no-store` | The_git_credential_delivery_never_crosses_the_tenant_boundary |
| POST | /api/workers/runs/{runId:guid}/artifacts | `Authorization: Bearer zwrk_…`; run resolved through the token tenant (must be Running, this worker's) | foreign/unknown run = 404 | The_artifact_worker_api_refuses_a_foreign_run_or_artifact |
| POST | /api/workers/runs/{runId:guid}/artifacts/{artifactId:guid}/files | `Authorization: Bearer zwrk_…`; run+artifact resolved through the token tenant | foreign/unknown ids = 404; path-traversal spellings = 400 (F-016 rules) | Artifact_paths_cannot_escape_or_enter_another_artifacts_tree |
| POST | /api/workers/runs/{runId:guid}/artifacts/{artifactId:guid}/finalize | `Authorization: Bearer zwrk_…`; run+artifact resolved through the token tenant | foreign/unknown ids = 404 | The_artifact_worker_api_refuses_a_foreign_run_or_artifact |
| POST | /webhooks/{webhookId:guid} | anonymous + HMAC signature; the resolved webhook row selects the tenant | unknown or disabled id = bare 404; a valid id with a wrong signature = bare 401; neither names a tenant. Behaviour, mitigation, rationale of the accepted residual: the 401-vs-404 split means a prober can distinguish "id exists and is enabled" (existence + enabled are resolved before signature verification, `WebhookDeliveryService.cs:119-131`) from "id unknown or disabled"; mitigated by the id's 128-bit v4-Guid entropy (nothing sequential to enumerate) and the per-id rate-limit budget that answers 429 before any lookup; and the one toggle that moves an id between the 404 and 401 answers (enable/disable) is Owner-only inside the owning organization, so any actor who could flip it already knows the id. The URL remains the capability handed to the git host; the signature stays the only delivery authority | A_delivery_signed_with_another_organizations_secret_is_refused_and_queues_nothing |

## SignalR hubs

| Hub | Route | Hub method | Tenant selection | Refusal contract | Probe |
|---|---|---|---|---|---|
| RunLogHub | /hubs/run-logs | — (transport endpoint) | per-message groups; joined only after method-level authorization | unauthenticated negotiate = bare 401 (ticket scheme, no cookie fallback) | The_browser_hub_authenticates_the_connection_with_the_ticket_scheme |
| RunLogHub | /hubs/run-logs/negotiate | — (negotiate endpoint) | subscription-ticket bearer token (data-protected `v1\|user\|org\|expiry`) | invalid/expired/absent ticket = bare 401 | The_browser_hub_authenticates_the_connection_with_the_ticket_scheme |
| RunLogHub | — | JoinRunAsync(Guid runId) | ticket's organization + database membership re-check; the run must belong to that organization | foreign/unknown run and revoked membership = one generic `HubException`, no existence oracle; no line ever crosses the boundary | The_browser_hub_refuses_a_foreign_run_id_at_the_hub_method |
| WorkerLogHub | /hubs/worker-logs | — (transport endpoint) | per-worker groups joined from the authenticated claim | unauthenticated negotiate = bare 401 (worker-token scheme, no cookie fallback) | The_browser_hub_authenticates_the_connection_with_the_ticket_scheme |
| WorkerLogHub | /hubs/worker-logs/negotiate | — (negotiate endpoint) | `Authorization: Bearer zwrk_…` (or `access_token`); the token row selects the tenant | invalid/absent token = bare 401 | The_browser_hub_authenticates_the_connection_with_the_ticket_scheme |
| WorkerLogHub | — | AppendLinesAsync(Guid runId, LogLineMessage[] lines) | token-selected tenant; the run must exist there and be assigned to this worker | foreign/unknown run, another worker's run, dead token = `HubException` carrying the `zenkins:rejected:` prefix; nothing is persisted | The_worker_hub_refuses_appends_to_another_organizations_run_and_persists_nothing |

## Non-tenant infrastructure

Enumerated so the diff stays total: these routes are registered but carry no tenant-owned rows.
Static assets are classified by metadata (every endpoint carrying `StaticAssetDescriptor`), not by
route, so fingerprinted filenames cannot rot the inventory; the test additionally proves they are
all GET/HEAD and none carries an `[Authorize]` attribute.

| Route | Purpose | Probe |
|---|---|---|
| (metadata) every endpoint carrying StaticAssetDescriptor metadata | fingerprinted static assets (framework scripts, app styles, theme) | — |
| {**path:file} | GET fallback rendering the not-found page (no tenant data) | — |
| /_blazor | Blazor Server circuit transport | — |
| /_blazor/negotiate | Blazor Server circuit negotiate | — |
| /_blazor/disconnect/ | Blazor Server circuit disconnect | — |
| /_blazor/initializers/ | Blazor Server initializers | — |
| /_framework/opaque-redirect | framework asset redirect | — |

## Stores, ids and credentials

| Surface | Location / shape | Isolation rule | Probe |
|---|---|---|---|
| Artifact store | `{Artifacts:RootPath}/{organizationId}/{runId}/{artifactId}/{relativePath}` (default `App_Data/artifacts`) | the (org, run, artifact) key is server-built from the tenant-filtered row; no client path is stored; relative paths are normalised by `ArtifactPathRules` (no `..`, no absolute) and re-resolved inside the artifact's own directory on download | The_artifact_store_layout_keeps_organizations_in_their_own_subtrees |
| Artifact bytes | streamed from the artifact's own directory only (zip entries recomputed through the shared rules) | org A's download never enumerates or streams org B's files; cross-tenant id = 404 | A_foreign_organizations_artifact_is_not_downloadable |
| Secrets | database table `Secrets`, `Ciphertext` via Data Protection (F-012); no value-returning user route | values decrypt only for the executing worker of a tenant-owned run; plaintext never in another tenant's responses, logs, artifacts or pages | No_other_tenants_credential_or_secret_material_appears_in_ones_own_surfaces |
| Git credentials | database table `GitCredentials`, `Ciphertext` via Data Protection (F-013); no value-returning user route | material decrypts only for the executing worker of a tenant-owned run | The_git_credential_delivery_never_crosses_the_tenant_boundary |
| Webhook secrets | database table `Webhooks`, `SecretCiphertext`; plaintext shown exactly once on create/rotate, response `no-store` | the signature is the delivery credential; a wrong-tenant signature is refused | A_delivery_signed_with_another_organizations_secret_is_refused_and_queues_nothing |
| Worker tokens | database table `WorkerTokens`, SHA-256 `TokenHash` (plaintext never stored); hash lookup through the system scope, the row selects the tenant | a token authorizes exactly its own organization's runs | The_claim_never_crosses_the_tenant_boundary |
| Data Protection key ring | `{DataProtection:KeysDirectory}`, or `keys/` next to the SQLite file, or `App_Data/keys` | one process-wide ring encrypting at rest; the ring is never tenant-reachable and never part of any response | No_other_tenants_credential_or_secret_material_appears_in_ones_own_surfaces |
| Database | one shared store (SQLite file / PostgreSQL); isolation by global query filters + `SaveChanges` guards (Q-001) | no org-B row readable under org A's filters; plaintext credentials/secrets never at rest | No_other_tenants_credential_or_secret_material_appears_in_ones_own_surfaces |
| Run log group id | `run:{runId:N}` (browser hub) | join validated against the ticket organization + run ownership | The_browser_hub_refuses_a_foreign_run_id_at_the_hub_method |
| Worker group id | `worker:{workerId:N}` (cancel signals, F-009) | joined from the authenticated claim only | The_heartbeat_cancel_channel_never_carries_another_organizations_run |
| Subscription ticket | data-protected `v1\|{userId}\|{organizationId}\|{expiry}` (F-008) | minted by the run detail page for the session only; join re-validates membership | The_browser_hub_refuses_a_foreign_run_id_at_the_hub_method |
| Build log rows | database table `BuildLogs`, direct `OrganizationId` column, tenant-filtered | org A's log download and hub history never contain org B's lines | Log_download_refuses_a_foreign_run_without_bytes_or_oracle |

## Documented invariants

Three composition-level facts the audit relies on but no single endpoint row expresses. Each pin
names what the safety depends on, where it lives in the source, and the conditions under which the
invariant holds — so a future change that breaks one of them is a reviewable diff, not a silent gap.

| Invariant | Depends on | Pinned at | Holds while | Probe |
|---|---|---|---|---|
| Artifact download gate tenant safety (`OpenDownloadAsync` refuses foreign ids, `GetRunArtifactsAsync` lists only own rows, the download endpoint only ever streams the gated row's files) | 100% on the `Artifact` global query filter — there is no secondary id check on these paths | `ArtifactEndpoints.cs:46-47` (the download route), `ArtifactService.cs:553` + `ArtifactService.cs:584` (the gated reads), `ArtifactConfiguration.cs:59` (the filter) | the `Artifact` query filter stays in the model, and `IgnoreQueryFilters`/`SystemQueryAsync` on tenant-owned entities remain forbidden outside an `ISystemDataScope` scope | A_foreign_organizations_artifact_is_not_downloadable |
| Download/log routes establish the tenant from the session because `TenantResolutionMiddleware` treats them as page requests | the `IsPageRequest` no-extension heuristic (`/_` prefix and file-extension paths skip tenant resolution) | `TenantResolutionMiddleware.cs:29` (the call), `TenantResolutionMiddleware.cs:46-49` (the heuristic) | the download and log URLs keep carrying no file extension — any added extension in their route templates would silently skip tenant resolution for authenticated page requests | Log_download_refuses_a_foreign_run_without_bytes_or_oracle |
| The hubs' in-method `TenantContext` re-pointing is correct per invocation | SignalR creating one DI scope per hub-method invocation, and the hub reading data only after the re-pointing call (join check before history read; token authentication before the run lookup) | `RunLogAccess.cs:104` (the ticket's organization re-points the scoped tenant), `WorkerService.cs:800` (the token row selects the tenant), `RunLogHub.cs:52-93` (join authorized before the history read) | the per-invocation scope semantics stay (a scoped `TenantContext` shared by the hub's services within one invocation, never reused across tenants) | The_browser_hub_refuses_a_foreign_run_id_at_the_hub_method |

Scope note: the worker side of the secret/redaction chain — `src/Worker`'s `GitLogFilter`
registration and the `GitCredentialFiles` temporary-file lifecycle — belongs to card F-015's
surface and is deliberately outside this server-side inventory's enumeration.
