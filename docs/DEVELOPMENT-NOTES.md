# Development notes: DHCP failover, cluster improvements, fork build

A complete record of what was changed in this fork, why, the bugs found along the way, and how the pieces fit together. For user-facing setup, see [DHCP-FAILOVER.md](../DHCP-FAILOVER.md), [CLUSTER-IMPROVEMENTS.md](../CLUSTER-IMPROVEMENTS.md), [ACME.md](../ACME.md) and [FORK.md](../FORK.md). Ready-to-paste upstream PR descriptions are in [docs/upstream-prs/](upstream-prs/).

Work done 2026-10-24 to 2026-10-25. Section 4b (ACME) was added on 2026-10-25.

---

## 1. Goals and constraints

- **Goal:** redundant DHCP for Technitium DNS Server. Upstream has none; the Cluster feature only replicates DNS.
- **Then:** better clustering. That means:
  - automatic Primary failover;
  - faster, more reliable config sync;
  - a health view;
  - a web UI for DHCP failover.
- **Constraint: homelab scale.** Assume **2 servers** and limited hardware. Nothing should *require* 3 nodes or extra infrastructure. Where safety needs a tie-breaker, use something already present, such as the router.
- **Constraint: upstreamable.** DHCP failover and cluster improvements must be submittable to TechnitiumSoftware as **independent** pull requests. Fork-only additions (CI, installer, versioning) stay out of them.
- **Constraint: installed with the official one-line installer** (`curl … install.sh | sudo bash`, systemd), not Docker.
- **CI** runs on the user's self-hosted runners in the `optimusprime-nn` org (labels `self-hosted, Linux, X64, big`).

## 2. Branch layout

| Branch | Base | Content |
|---|---|---|
| `upstream/dhcp-failover` | upstream `develop` | 1 commit: DHCP failover only |
| `upstream/cluster-improvements` | upstream `develop` | 1 commit: cluster improvements only |
| `fork/main` | upstream `master` = v15.6.2 release | the two feature commits (cherry-picked) + fork-only commits |

- **Why `develop`:** upstream merges outside PRs into `develop`; all their recent merged PRs target it.
- **Why the fork is based on the release:** `develop` needs an unreleased TechnitiumLibrary and contains unreleased upstream changes. Homelab servers should run released code.
- **Independence check:** each PR branch builds alone, contains no code from the other feature or from the fork, and the two merge cleanly in **either order** with a passing combined build.
  - To get there, the DHCP branch's additions in `HttpApiClient.cs` and `ClusterNode.cs` sit before `ProxyRequest`, and its server-to-server routes sit with the other DHCP routes. Both branches had originally inserted code at the same anchors, which conflicted.
- **Workflow:** change a feature on its `upstream/*` branch, then cherry-pick onto `fork/main`.
- **History:** the original mixed branch `feature/dhcp-failover` was deleted. Its final state is the local-only tag `fork-premerge-backup` (`63a2af04`).
- **Local working folders:** `H:\14.Claude\DnsServer-dhcp`, `-cluster` and `-fork`. The main checkout `H:\14.Claude\DnsServer` is on `master`.

---

## 3. DHCP failover (`upstream/dhcp-failover`)

### 3.1 Model

Hot-standby (active/passive), similar in spirit to Kea HA hot-standby and the DHCP failover RFC draft (MCLT, partner-down), but over Technitium's own HTTP API instead of a new protocol. Active/active load balancing was considered and deferred: hot-standby fits the existing Primary/Secondary model and is far simpler to get right.

### 3.2 Components

- **`Dhcp/IDhcpFailoverHandler`:** keeps the `Dhcp` namespace independent of `Cluster`. DhcpServer only knows three things:
  - `IsServing`;
  - `LeaseTimeLimit`;
  - `OnLeaseChanged(scope, lease, removed)`.
- **`Dhcp/DhcpServer`:**
  - If a handler is set and not serving, `ProcessDhcpMessageAsync` returns `null` immediately (the passive Standby).
  - Snapshot format `"DS"` v1: a count, then each scope's `Scope.WriteTo()` bytes, length-prefixed.
  - Lease update format `"DL"` v1: a count, then (scope name, removed flag, `Lease.WriteTo()`) per update.
  - `ApplySnapshotAsync(configAuthority)`: **mirror** mode replaces, adds or deletes scopes and mirrors leases; **merge** mode updates leases only, and the later expiry wins.
  - `GetSettingsHash()`: SHA-256 over the scopes' settings-only bytes.
- **`Dhcp/Scope`:**
  - `ApplyReplicatedLease`: if another client holds the same address, the later expiry wins and the conflict is logged. It also updates DNS entries.
  - `RemoveReplicatedLease`.
  - `SyncLeasesFrom(source, mirror)`.
  - `GetSettingsBytes()`, built on `WriteTo(s, includeLeases:false)`.
  - `OnLeaseChanged` hooks in `CommitLease`, `ReleaseLease`, `RemoveLease` and the reserved/dynamic conversions.
  - `GetLeaseTime()` honors the failover lease cap.
- **`Cluster/DhcpFailoverManager`:** config file `dhcp-failover.config` (`"DF"` v2), the heartbeat timer and state machine, snapshot pulls, and the lease push queue.
- **`Cluster/DhcpFailoverPartner`:** `IDhcpFailoverPartner`, with the three partner calls. It's implemented by `ClusterNode` (Cluster mode) and by `DirectDhcpFailoverPartner` (`HttpApiClient` with URL and token; direct mode).
- **`WebServiceDhcpFailoverApi`:**
  - user endpoints `/api/dhcp/failover/get|set`;
  - server-to-server endpoints `/api/admin/cluster/dhcp/status|snapshot|leaseUpdates`.
- **`DnsWebService`:** creates the manager **before** `DhcpServer.Start()`, so a Standby never answers even the first packet after boot.

### 3.3 Decisions and why

| Decision | Why |
|---|---|
| Reuse `Scope.WriteTo()` for snapshots instead of a new format or splitting the scope file into settings and leases | No file format change, and a smaller upstream diff. The original plan was to split the scope file; it was dropped for this. |
| Detect settings changes by comparing the Primary's reported hash with the hash **recorded at the last sync**, not with a hash of local scopes | `ConcurrentDictionary` iteration order can differ between servers, so identical settings could hash differently and cause endless resyncs. |
| Mirror sync also every 5 minutes | Safety net for any lost push. |
| Primary merges the Standby's leases on startup and whenever the Standby serves | Leases the Standby issued during an outage must survive. |
| Standby pushes **all** its leases before handing back control | Covers a split brain where the Primary never saw the Standby serving, and so would never pull a merge. |
| Incoming update applied only if not older than the local copy (`LeaseExpires`) | Reordering and retries are safe. Release messages carry the lease's expiry, so a stale release can't remove a renewed lease. |
| Lease time capped at max client lead time (default 1800 s) in PartnerDown | Limits the conflict window if both servers end up serving. |
| Partner answering with **any** error counts as alive | Without it, a revoked token or a half-configured pair would make the Standby start serving next to a live Primary. |
| Standby rejects scope and lease edits | They'd be overwritten by the next mirror. The message tells the admin to edit on the Primary. |
| Direct mode (URL + API token), default in the UI when no Cluster exists | The user asked to run DHCP failover without clustering. Two-node homelabs shouldn't need a Cluster just for DHCP. |
| UI warns before saving a plain-HTTP partner URL | The admin token would cross the network unencrypted. HTTP is allowed because many homelab installs have TLS off. |
| API token never returned by `get` (`partnerApiTokenSet` flag instead) | Avoids leaking an admin token to the browser. |
| Server-to-server endpoints require Administration: Delete | Same level as the existing `transferConfig`; the Cluster token has it. |
| Exactly two servers | A pair protocol. Several Standbys would need ordering and their own "who wins" rules. Several independent pairs, each owning different scopes, already work. |

### 3.4 Defaults

| Setting | Value |
|---|---|
| Heartbeat | 5 s |
| Partner-down delay | 30 s (≥ 2× heartbeat) |
| Max client lead time | 1800 s (≥ 60 s) |
| Push batching | 0.5 s, 1000 updates per request |
| Push queue cap | 10,000 updates |
| Full mirror | every 300 s |
| Startup heartbeat timeout | max(5, 2× heartbeat) s |

---

## 4. Cluster improvements (`upstream/cluster-improvements`)

### 4.1 Missed-notify catch-up

- The Primary records `_configLastModified` on every `TriggerNotifyAllSecondaryNodes()`. Its initial value is the process start time, so Secondaries check once after a Primary restart.
- The Primary reports it as `configLastModified` in the cluster state.
- A Secondary's heartbeat to the Primary calls `CheckConfigFreshnessFromPrimaryNode()`. It compares the value, truncated to seconds, with `_configLastSynced`, which comes from the HTTP `Last-Modified` header and so has seconds precision. Both timestamps are on the Primary's clock, so no clock-skew issue.
- **Why:** previously a missed notify meant waiting for the 900 s config refresh. Now it's caught within one 30 s heartbeat.

### 4.2 Health view

- Every node's heartbeat response now carries its `configLastSynced`, so the Primary can show last-synced for every Secondary.
- Badges: Sync Pending, Never Synced, Version Mismatch, and Failover Paused.
- A red fenced banner appears on a fenced former Primary.

### 4.3 Automatic Primary failover

**Design questions, and the answers the user chose:**
- *Quorum or not?* Majority quorum.
- *What happens to the old Primary?* It fences itself and waits for a manual rejoin; it doesn't auto-rejoin.
- *Then* the user required **2-node support**, because "a homelabber wouldn't have the room for three". The answer is a witness, which defaults to the successor's default gateway, plus a TCP liveness probe.
- *Then* the user asked for a **maintenance pause**, because planned reboots of the Primary would otherwise trigger failover.

**Decision procedure** (successor only, every 10 s):
1. Primary unreachable for longer than the timeout. If it was never seen, the node's start time is used instead.
2. Not paused.
3. The Primary doesn't accept TCP on port 53 or its web port (3 s each).
4. Its own vote is granted.
5. Tie-breaker: 3+ nodes need a majority of **all** nodes (`n/2 + 1`, Primary included); 2 nodes need a reachable witness (ping ×3, then TCP 53/80/443, where a refused connection counts as reachable).
6. Promote via the existing `PromoteToPrimaryNodeAsync(true)`, then disable auto failover and clear the successor.

**Voting rules:**
- A node grants its vote only to the successor in its own view.
- It grants at most one candidate per timeout window, and not while paused.
- The successor's self-vote follows the same rule.

**Fencing:**
- A Primary that sees a remote reporting a different Primary that's in its own node list fences itself: no notifies, `transferConfig` refused, a banner, persisted.
- If the remote reports a Primary this node removed, the remote is stale, and this node re-notifies it at most every 5 minutes.

**Pause:**
- `/api/admin/cluster/primary/pauseAutoFailover?minutes=0..1440`.
- Pushed immediately and propagated as remaining seconds, which is skew-proof.
- Persisted. Voters refuse votes while it's active.

**Config:** `cluster.config` v3 (reads v1–v3).
- v2 adds enabled, successor, timeout, fenced and fenced-by.
- v3 adds the witness list and paused-until.

### 4.4 Defaults

| Setting | Value |
|---|---|
| Failover timeout | 120 s (60–3600, ≥ 2× heartbeat refresh) |
| Check interval | 10 s |
| Probe timeout | 3 s |
| Stale-node re-notify interval | 300 s |

---

## 5. Fork-only additions (`fork/main` only)

| Commit | What | Why |
|---|---|---|
| `fae3cb9c` | GitHub Actions build: TechnitiumLibrary pinned at `4a5a530`, publish, JS syntax check, Docker build, artifact | The user asked for CI. The library is a separate repo referenced by `../TechnitiumLibrary/bin`, and it's pinned for reproducible builds. |
| `4bd74253` | Push a multi-arch (amd64 + arm64) image to `ghcr.io/<owner>/dnsserver` | The user wanted to pull images on homelab servers; arm64 covers Raspberry Pis. |
| `8a77075b` | Run on the self-hosted `big` runners. PRs from outside forks run on GitHub-hosted runners. | The user's own CI servers. A public repo must not run untrusted PR code on them. |
| `1e0cf9b0` | `DOTNET_INSTALL_DIR` = runner tool cache | `setup-dotnet` defaults to `/usr/share/dotnet`, and the runner user `gha` has no root. |
| `3962cf1e` | `fork-latest` release with `DnsServerPortable.tar.gz` and a generated `install.sh` | The user installs with the official installer. The fork's installer is generated from upstream's `DnsServerApp/install.sh`, with only the URL and banner changed, so it never drifts. |
| `423cc667` | Version `<upstream>.<run number>` (e.g. 15.6.2.8), with the commit in the informational version. Numbered releases `v<version>` (last 20 kept). Docker version tag. Web console update check reads `fork-latest/update.json`. | Before this, fork builds reported plain 15.6.2: they were indistinguishable from official, the Cluster version-mismatch badge was blind, and the update check offered the official download, which would remove the fork's features. The version is set with `-p:` at publish time so project files stay identical to upstream. The update URL is injected as `AssemblyMetadata("UpdateCheckUri")`; without it, the official feed is used. |
| `87dab0e3` | Installer: `curl -L`, verify the archive before extracting, abort on extraction failure | **Bug found in the field:** GitHub release downloads redirect, and upstream's `curl -o … --fail` has no `-L`, so it saved an empty file. tar failed, and the installer restarted the old version and printed "installed successfully". |
| `3c63fcc9` | DHCP failover role/state per node in the Cluster table. CI builds `fork/**` and releases from `fork/main`. FORK.md branch-layout section. | The badge needs both features, so it can't be in either upstream PR. |

**Release flow:** each push to `fork/main` triggers:
1. a build;
2. numbered release `v<version>`;
3. `update.json`;
4. replacement of the rolling `fork-latest`;
5. pruning of old numbered releases.

Push builds are never cancelled mid-release (`cancel-in-progress` is on for PRs only), so `fork-latest` can't be left missing.

**Repository move:** `Optimusprimeums/DnsServer` was transferred to `optimusprime-nn/DnsServer`. Org runners only serve org repos. The image path changed from `ghcr.io/optimusprimeums/dnsserver` (the old images can be deleted) to `ghcr.io/optimusprime-nn/dnsserver`.

---

## 4b. ACME certificates (`upstream/acme`)

**Request:** the server should get its own certificates via ACME + DNS-01, set up in the GUI so rollout is easier.

**Scope decisions (from the user):**
- certificates for **this server only**: the web console plus DoT/DoH/DoQ;
- the domain's public DNS is currently at **Cloudflare**, but the PR shouldn't be limited to one provider, so providers are pluggable.

**Design:**
- **`AcmeClient`:** an in-house RFC 8555 client. ES256 account key; JWS with `jwk` only for newAccount and `kid` everywhere else; Replay-Nonce handling with badNonce retry x3; EAB (HS256); order, authorization, dns-01, finalize polling, PEM chain download. No new NuGet dependency.
- **`IDns01Provider`** with three providers:
  - **LocalZone:** a Primary zone on this server; uses `AuthZoneManager.AddRecord`/`DeleteRecord`, which bump the SOA serial and send NOTIFY.
  - **Rfc2136:** UPDATE with the zone in the question section and updates in the authority section; delete-specific-RR uses class NONE / TTL 0; TSIG via `TsigResolveAsync`; the zone is found by asking the update server for the SOA.
  - **Cloudflare:** API token; zone found by walking up the name; TXT content quoted.
- **`Dns01Resolver`:**
  - follows `_acme-challenge` CNAMEs through public resolvers (default 1.1.1.1, 8.8.8.8, 9.9.9.9; configurable);
  - finds the zone's public NS and queries them directly, non-recursively;
  - skips servers that don't respond (no IPv6);
  - if none responds within 30 seconds (no NAT hairpin), continues with a warning.
- **`AcmeManager`:**
  - `config/acme/acme.config` (binary `"AC"` v1) and `config/acme/certificate.pfx` (random password, saved before the file is written);
  - renewal check 2 minutes after start, then every 6 hours; renews 30 days before expiry, when the names change, or when there's no certificate;
  - re-registers if the CA reports `accountDoesNotExist`/`unauthorized`;
  - refuses settings changes during a run;
  - cancels a run on shutdown.
- **Install:** new `DnsWebService.InstallWebServiceTlsCertificate` (plus Cluster `UpdateSelfNodeUrlAndCertificate` and config save) and `InstallDnsTlsCertificate` (restarts the encrypted listeners only when the path changes). The DNS TLS path isn't overwritten by Cluster config transfer (`DnsServer.cs` applies it only when `!isConfigTransfer`), so each node keeps its own.
- **API** `/api/settings/acme/{get,set,directoryInfo,request,testDns01}`:
  - secrets are never returned;
  - agreeing to the terms is explicit and resets when the CA changes;
  - `directoryInfo` needs Modify, since it fetches an admin-supplied URL, and only returns http(s) terms links.
- **GUI:** a Settings > Certificates (ACME) tab with its own Save button; the page-wide Save Settings button is hidden on this tab. It's per node: with "Cluster" selected it asks you to pick a node, and refuses to save if the node changed since loading.
- **Insertion anchors:** chosen so this PR doesn't conflict with the other two. The field and init go after `_settingsApi`; the dispose goes after the cluster manager's.

**Testing:** `.github/acme-e2e/run.sh` (fork-only), run by the `acme-e2e` CI job; releases depend on it.
- **Setup:** Technitium (this build) and Pebble 2.10.1 on a private Docker network. Pebble validates through Technitium, and Technitium trusts Pebble's minica root. The CA name `pebble` is published in a local zone, because Technitium resolves outbound names through itself.
- **Scenarios:**
  1. local zone with a wildcard;
  2. CNAME delegation;
  3. RFC 2136 with a TSIG policy scoped to `_acme-challenge.dns.rfc.test|TXT`, reusing the account (the renewal path).
- **Checks:** the HTTPS chain and host name verified against Pebble's root, and no leftover TXT records.
- **Result:** green on run #12 (ci-build-5).

---

## 6. Bugs and mistakes found, and how they were fixed

| # | Problem | Found by | Fix |
|---|---|---|---|
| 1 | Two successors could both win a majority and fence each other, leaving no Primary | independent review | Candidate-specific, one-per-window votes |
| 2 | `setOptions` half-applied when auto failover validation failed, even with failover off | independent review | Validate first; validate only when enabled |
| 3 | Fenced state lost on restart | independent review | Persisted in `cluster.config` |
| 4 | Re-notify of a stale node on every heartbeat | independent review | Rate-limited to once per 5 minutes per node |
| 5 | Config refresh timer retrying forever on a newly promoted Primary | independent review | Don't re-arm when self is Primary |
| 6 | My first fix for #5 held `_configRefreshLock` around promotion, but `StopConfigRefreshTimer` takes the same non-reentrant semaphore, so it would deadlock | caught before build | Reverted; the existing Stop already waits for an in-flight refresh |
| 7 | A revoked or wrong partner token would make the DHCP Standby take over next to a live Primary | self-review while documenting | Any partner response = alive |
| 8 | Auto failover required 3 nodes | user feedback | Witness plus TCP liveness probe for 2 nodes |
| 9 | CI failed on the self-hosted runners: no root for the .NET install | first self-hosted run | Tool-cache install dir |
| 10 | Fork reported the same version as upstream; update check offered the official download | user question | Fork versioning and update feed |
| 11 | Installer saved an empty tarball (no `curl -L`) and reported success | install on ns2 (stayed on 15.6.0) | Patched installer lines; verified against the live release |
| 12 | Everything on one branch, so it couldn't be submitted as separate PRs | user feedback | Split into `upstream/*` + `fork/main` |
| 13 | The two PR branches conflicted when combined (same insertion anchors) | merge test | Moved the DHCP branch's insertions to their own anchors |
| 14 | ACME: every run after the first failed, because `NewOrderAsync` read the directory before it was loaded, so renewals would never work | independent review | Load the directory in `NewOrderAsync`; covered by e2e scenario 3 |
| 15 | ACME: agreement to the CA's terms carried over to a different CA | independent review | Reset on CA change (API default + UI untick) |
| 16 | ACME: saved fields (email, TSIG key, update server) couldn't be cleared | independent review | Missing = keep, empty = clear |
| 17 | ACME: changing node on the tab showed stale data and could save to the wrong node | independent review | Reload on node change; refuse to save if the node changed |
| 18 | ACME: settings save racing a running issuance (shared tmp file, CSR/order mismatch, PFX password not saved) | independent review | Run lock in `SetConfig`; config write lock; domain snapshot per run; password saved first |
| 19 | ACME: propagation check failed when the zone's NS is the network's own public IP and there's no NAT hairpin | independent review | Skip with a warning if no server responds |
| 20 | ACME: low findings (deleted account, shutdown, SSRF via directoryInfo, `javascript:` terms link, selector injection, test timeout, restart rule) | independent review | All fixed |
| 21 | ACME PR initially conflicted with the DHCP PR (same anchors in DnsWebService) | cherry-pick onto fork/main | Moved ACME anchors; all three PR branches verified to merge in two orders and build |
| 22 | ACME e2e first run: Technitium couldn't resolve the Docker name `pebble` (it resolves outbound names via itself) | first CI run | Publish `pebble` in a local zone in the test |

## 7. Verification status

- ✔ All branches build: PR branches against TechnitiumLibrary `develop`, `fork/main` against the pinned release.
- ✔ JS syntax checks pass.
- ✔ The PR branches merge in either order, and the combined build passes.
- ✔ CI on the self-hosted runners is green.
- ✔ Release assets were downloaded and checked: version strings, update URL, installer patches, tarball contents.
- ✔ The installer download and verify lines were run against the live release.
- ✔ ACME end-to-end against Pebble (three providers/paths, HTTPS verified) passes in CI on every push to `fork/main`.
- ✔ **Running on two real servers** (ns1/ns2) with DHCP failover on and real clients.
- ✔ **DHCP failover tested live** on ns1/ns2: stop Primary, hand back, split brain, Standby restart. and invalid token.
- ☐ **Cluster scenarios not yet exercised live** (witness promotion, pause, missed notify). See the test plan in the cluster PR description.
- ✔ **ACME verified against a real CA:** Let's Encrypt with Cloudflare DNS-01; certificates issued on ns1 and ns2.

## 8. Open items

- Test on the two homelab servers (ns2 + partner). Then fill in the "Testing" rows of the PR descriptions.
- ACME on ns2: Let's Encrypt staging with the Cloudflare provider, then production.
- Optional: delete the `v15.6.2.6` release (broken installer from before fix #11; it will be pruned eventually) and the old `ghcr.io/optimusprimeums/dnsserver` images.
- Possible follow-ups:
  - a second heartbeat path (direct link) for 2-node clusters;
  - an ordered successor list;
  - several DHCP Standbys;
  - writing `cluster.config` v1 when the new options are unused, to keep downgrades possible (asked of the maintainers in the PR).
- Upstream CLA: submitting transfers copyright of the contribution to the project owner. The commits carry a `Co-Authored-By: Claude` trailer; the submitter should be comfortable with both.
