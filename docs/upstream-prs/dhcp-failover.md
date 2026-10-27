# PR: DHCP failover (hot-standby) between two DNS servers

**Branch:** `optimusprime-nn/DnsServer:upstream/dhcp-failover` → **`TechnitiumSoftware/DnsServer:develop`**
**Commit:** single commit, 14 files, +2135 / −6. Independent of the cluster improvements PR; the two merge cleanly in either order.

---

## Summary

Technitium's DHCP server has no redundancy. If the server running DHCP goes down, clients can't get or renew leases, and the Cluster feature only replicates DNS. This PR adds optional **active/passive DHCP failover** between two Technitium DNS Servers:

- The **Primary** serves DHCP clients and replicates every lease change to the **Standby** within about half a second.
- The **Standby** keeps the same scopes loaded and bound but stays silent, mirroring the Primary's scopes (settings and leases).
- If the Primary has been unreachable for a configurable delay, the Standby takes over. Leases it issues are capped at a short **max client lead time**.
- When the Primary returns, it merges the leases the Standby issued and resumes serving. The Standby hands back control and mirrors again.

It's off by default, and nothing changes for existing installs until it's enabled on both servers.

The two servers can be paired **directly**, by the partner's web console URL plus an API token, so **no Cluster is required**. Small setups with two machines get redundant DHCP without setting up a Cluster. If both servers are already in a Cluster, they can be paired through it instead and use the Cluster's own API token.

## How it works

### Roles and states

| State | Primary serves? | Standby serves? | Meaning |
|---|---|---|---|
| `Disabled` | yes (standalone) | yes (standalone) | Failover off: unchanged behavior |
| `Startup` | no | no | Until the first heartbeat result (bounded by the heartbeat timeout, ≤10 s by default) |
| `Recovering` | no | — | Primary merging the Standby's leases before serving |
| `Normal` | **yes** | no | Normal operation |
| `CommunicationsInterrupted` | **yes** | no | Partner unreachable; the Standby counts down to `PartnerDown` |
| `PartnerDown` | — | **yes** | Standby has taken over; leases capped at max client lead time |

### Replication: three channels, all over the existing HTTP API

1. **Heartbeat** (default every 5 s, both directions): `GET /api/admin/cluster/dhcp/status`. It returns the partner's enabled flag, role, state, whether it's serving, its configured partner, and (from the Primary) a SHA-256 hash of all scope **settings**.
2. **Lease push** (serving server → partner): every lease change is queued and sent in batches of up to 1000 every 0.5 s via `POST /api/admin/cluster/dhcp/leaseUpdates`. The body is a binary list of (scope name, removed flag, `Lease`).
   - Changes that are pushed: commit (ACK), release, decline, admin removal, and reserved/dynamic conversion.
   - Failed pushes are re-queued while the partner is reachable. The queue is cleared when the partner is unreachable, and the next full sync covers the gap.
   - The queue is capped at 10,000 updates.
3. **Full snapshot**: `GET /api/admin/cluster/dhcp/snapshot` returns every scope serialized with the existing `Scope.WriteTo()` format, including leases. It's applied in one of two modes:
   - **Mirror** (Standby pulls from Primary): at startup, after reconnecting, whenever the Primary's settings hash changes, and every 5 minutes. Scopes are added, replaced or deleted to match, and leases are made identical.
   - **Merge** (Primary pulls from Standby): at Primary startup, and whenever it sees the Standby serving. Nothing is deleted; for each client, the lease with the later expiry wins.

Expired leases aren't replicated, because each server expires them by time. In-flight offers aren't replicated either.

### Applying a replicated lease

- An incoming update is applied only if it isn't older than the local copy (by `LeaseExpires`), so duplicate or out-of-order messages can't roll back a newer lease.
- If another client already holds the same address, the lease that expires later keeps it, and the conflict is logged.
- DNS: the receiving server calls the existing `AddDnsEntries` / `RemoveDnsEntries`, so its DHCP-registered A/PTR records stay current too.

### Safety decisions (and why)

- **Short leases while the Standby serves.** In `PartnerDown`, `Scope.GetLeaseTime()` is capped at the max client lead time (default 1800 s). If the Primary was actually alive (split brain), conflicting leases expire quickly and clients renew soon after the Primary returns.
- **A partner that answers is alive.** If the partner returns *any* HTTP/API response, even an error such as an invalid token, failover disabled or a role mismatch, the Standby treats it as alive and never escalates to `PartnerDown`. Without this, a revoked token or a half-configured pair would start a second DHCP server while the Primary is still running.
- **Hand-back without losing leases.** Before leaving `PartnerDown`, the Standby pushes **all** its leases to the Primary, then switches to passive and mirrors. Leases issued during the outage survive, even if individual pushes were lost.
- **The Primary owns scope settings.** In this mode the Standby's DHCP scope and lease edit endpoints (`scopes/set`, `addReservedLease`, `removeReservedLease`, `enable`, `disable`, `delete`, `leases/remove`, `convertToReserved`, `convertToDynamic`) return an error telling the user to make the change on the Primary. Otherwise the next mirror would silently overwrite those edits.
- **Settings change detection without byte-for-byte comparison.** `ConcurrentDictionary` iteration order can differ between servers, so the Standby compares the Primary's reported settings hash with the hash it recorded at its last sync, rather than with a hash of its own scopes. That avoids endless resyncs.
- **Clients need nothing special.** At T1 a client unicasts its renewal to the server that issued its lease. If that server is down, it broadcasts a rebind at T2 without a server identifier, which the Standby answers from its replicated lease table. Scope options are evaluated locally on each server, so **Use This DNS Server** hands out each server's own address.

## Configuration

Settings are per server, stored in a new `dhcp-failover.config` file in the config folder (binary, `"DF"`, version 2). They aren't part of config backup/transfer, because the role differs per server.

| Setting | Default | Notes |
|---|---|---|
| Enabled | off | |
| Role | Primary | `Primary` / `Standby` |
| Partner | — | **Direct**: web console URL, an API token created on the partner, and an "ignore certificate errors" option. **Cluster**: the partner's Cluster node name. |
| Heartbeat interval | 5 s | |
| Partner down delay | 30 s | Must be at least 2× the heartbeat interval |
| Max client lead time | 1800 s | ≥ 60 s |

The web console has a new **DHCP > Failover** tab with live status for this server and its partner, and all of the settings. The API token is never returned to the browser; the page only shows that one is saved. The page warns before saving a plain-HTTP partner URL, because the token would cross the network unencrypted.

## API

| Endpoint | Permission | Purpose |
|---|---|---|
| `GET/POST /api/dhcp/failover/get` | DhcpServer: View | Settings, local state, partner state, available Cluster nodes |
| `GET/POST /api/dhcp/failover/set` | DhcpServer: Modify | `enabled`, `role`, `partnerMode` (`Direct`/`Cluster`), `partnerUrl`, `partnerApiToken` (empty keeps the saved token), `partnerIgnoreCertificateErrors`, `partnerNodeName`, `heartbeatIntervalSeconds`, `partnerDownDelaySeconds`, `maxClientLeadTimeSeconds` |
| `GET /api/admin/cluster/dhcp/status` | Administration: Delete | Server-to-server heartbeat |
| `GET /api/admin/cluster/dhcp/snapshot` | Administration: Delete | Binary snapshot (raw stream, same middleware path as `transferConfig`) |
| `POST /api/admin/cluster/dhcp/leaseUpdates` | Administration: Delete | Binary lease updates (`application/octet-stream`) |

The server-to-server endpoints follow the existing cluster API's convention: an administrator token, where the existing `transferConfig` uses Administration: Delete. They live under `/api/admin/cluster/` for consistency, but work without a Cluster.

## Code changes

| File | Change |
|---|---|
| `Dhcp/IDhcpFailoverHandler.cs` (new) | Small interface DhcpServer calls into: `IsServing`, `LeaseTimeLimit`, `OnLeaseChanged()`. Keeps `Dhcp` independent of `Cluster`. |
| `Dhcp/DhcpServer.cs` | Passive check at the top of `ProcessDhcpMessageAsync`. Also adds the settings hash, snapshot write/apply (mirror and merge), lease update serialize/apply, and the `FailoverHandler` and `LeaseTimeLimit` properties. |
| `Dhcp/Scope.cs` | `ApplyReplicatedLease`, `RemoveReplicatedLease`, `SyncLeasesFrom(mirror/merge)`, `GetSettingsBytes`, and `OnLeaseChanged` hooks in commit, release, removal and conversion. `GetLeaseTime()` honors `LeaseTimeLimit`. `WriteTo(Stream, bool includeLeases = true)` gains an optional parameter (default unchanged), and its lease count now comes from an array snapshot so it always matches the leases written. |
| `Cluster/DhcpFailoverManager.cs` (new) | Config load/save, heartbeat and state machine, snapshot pull, lease push queue. |
| `Cluster/DhcpFailoverPartner.cs` (new) | `IDhcpFailoverPartner` (the three partner calls), plus `DirectDhcpFailoverPartner`, which uses `HttpApiClient` with URL and token. |
| `Cluster/ClusterNode.cs` | Implements `IDhcpFailoverPartner` (three wrapper methods following the existing `ClusterNode` pattern). |
| `DnsServerCore.HttpApi/HttpApiClient.cs`, `Models/DhcpFailoverStatus.cs` (new) | Client methods for the three server-to-server endpoints. |
| `WebServiceDhcpFailoverApi.cs` (new) | The five endpoints above. |
| `WebServiceDhcpApi.cs` | Standby guard on scope and lease edits. |
| `DnsWebService.cs` | Creates the manager **before** `DhcpServer.Start()` so a Standby is passive from the first packet. Also adds routes, raw-stream passthrough for `snapshot`, disposal, and `ClusterManager` / `DhcpFailoverManager` accessors. |
| `www/index.html`, `www/js/dhcp.js` | Failover tab. |
| `DHCP-FAILOVER.md` (new) | User documentation. Happy to move it into the docs/blog or this PR's description instead if you prefer. |

## Compatibility

- **Off by default.** With failover disabled, `IsServing` is `true` and `LeaseTimeLimit` is `0`, so DHCP behaves exactly as before.
- No changes to the scope file format, the config backup format or the existing API responses.
- `Scope.WriteTo` gains an optional parameter, which is source-compatible.
- New config file `dhcp-failover.config`. Older versions ignore it.
- Pairing direct with Cluster: both modes use the same endpoints. Only how the partner is reached differs.

## Testing

| What | Result |
|---|---|
| Builds on `develop` (with TechnitiumLibrary `develop`) and on `v15.6.2`, no new warnings | ✔ |
| Web console JS syntax check | ✔ |
| Merges cleanly with the cluster improvements PR in either order; combined build | ✔ |
| Running on a live two-server network (with ACME and the cluster changes), serving real clients | ✔ |
| Live failover scenarios with real DHCP clients | ☐ **Not yet tested.** To do before marking ready: normal replication, stop Primary → Standby serves after the delay, restart Primary → merge and hand back, split brain (block traffic between the servers only), invalid token, Standby restart. |

> Fill in the last row with your own results before submitting. Reviewers will ask.

## Known limitations / questions for maintainers

- **Exactly two servers per pair.** A pair can serve many scopes, and several independent pairs can split scopes between them. One Primary with several ordered Standbys would be a follow-up.
- **DHCP DNS records in a Cluster zone.** If a scope's domain zone is a Cluster catalog member (so it's a secondary zone on the Standby), the Standby's `AddDnsEntries` fails and is logged. Recommended setup: keep each server's DHCP zone local. Is there a better approach you'd prefer?
- **Partner token storage.** The partner's API token is stored in plain text in `dhcp-failover.config`, like other secrets in the config folder.
- **Endpoint location.** Is `/api/admin/cluster/dhcp/*` the right place for the server-to-server endpoints, given they also work without a Cluster? Moving them is easy.
