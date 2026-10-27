# PR: Cluster improvements: automatic Primary failover, health view, missed-notify catch-up

**Branch:** `optimusprime-nn/DnsServer:upstream/cluster-improvements` → **`TechnitiumSoftware/DnsServer:develop`**
**Commits:** 2 commits (the feature, then a fencing fix from a security review), 10 files. Independent of the DHCP failover PR; the two merge cleanly in either order.

---

## Summary

This PR makes three improvements to the Cluster:

1. **Missed-notify catch-up.** A Secondary that misses the Primary's change notify picks up the change within one heartbeat, instead of waiting for the next config refresh (900 s by default).
2. **Health view.** The Cluster page shows, for every node:
   - when it last synced;
   - whether it has a sync pending;
   - whether it runs a different version;
   - whether it has been fenced or has auto failover paused.
3. **Automatic Primary failover (opt-in).** A designated successor Secondary promotes itself when the Primary is really down. "Really down" means unreachable for a timeout, with the DNS and web ports no longer accepting connections, and either confirmed by a **majority** of nodes (3+ nodes) or with a **witness** proving the successor isn't the isolated one (2 nodes). A former Primary that comes back **fences** itself instead of fighting the new one. A **Pause Failover** switch covers planned reboots.

Today, promoting a Secondary is a manual step (`Promote To Primary`), so if the Primary dies overnight, config changes are blocked until someone intervenes. Auto failover is off by default.

## 1. Missed-notify catch-up

**Problem.** Secondaries only learn about Primary changes through the notify sent after a change, or through the periodic config refresh. If a Secondary is briefly unreachable when the notify goes out, it stays stale until the refresh interval.

**Change.**
- `TriggerNotifyAllSecondaryNodes()` records `_configLastModified`. It starts as the process start time, so after a Primary restart every Secondary checks once.
- The Primary reports it as `configLastModified` in `/api/admin/cluster/state`.
- On every heartbeat to the Primary, a Secondary calls `CheckConfigFreshnessFromPrimaryNode()`. If the Primary's `configLastModified`, truncated to seconds, is ≥ its `_configLastSynced`, it schedules the normal config refresh.

**Why the seconds truncation.** `_configLastSynced` comes from the `Last-Modified` header of `transferConfig`, which has one-second precision. Both timestamps are on the Primary's clock, so clock differences between nodes don't matter. At worst a change in the same second as a sync triggers one extra, harmless incremental sync, never a loop.

**Effect.** A missed change arrives within one heartbeat interval (30 s by default). This applies to all clusters and needs no setting.

## 2. Health view

- Each node's heartbeat response includes its `configLastSynced`. Previously only the Secondary viewing its own page saw this. Now the Cluster table shows **last synced** for every Secondary.
- Viewed from the Primary:
  - a **Sync Pending** badge appears when a Secondary's last sync is older than `configLastModified`;
  - a **Never Synced** badge appears for a connected same-version node with no sync time.
- A **Version Mismatch** badge appears when a node's version differs from the version of the node you're viewing from.
- A **Failover Paused until HH:mm** badge appears while a pause is active, and a red **fenced** banner appears on a fenced former Primary.

## 3. Automatic Primary failover

### Settings (Cluster Options on the Primary, propagated to all nodes)

| Setting | Default | Validation |
|---|---|---|
| Enable Auto Failover | off | |
| Successor Node | — | Must be an existing Secondary when enabled. A missing successor is cleared, not rejected, when auto failover is disabled. |
| Failover Timeout | 120 s | 60–3600 s, and ≥ 2× heartbeat refresh. Only validated when enabled. |
| Witness Addresses | empty (= successor's default gateway) | Up to 10 IPs. Can't be loopback, any, or a Cluster node's address. Only used in 2-node clusters. |

Validation runs **before** anything is applied in `setOptions`, so a rejected value never leaves the options half-saved.

### Decision procedure

Every 10 s, on the node whose name matches the successor and whose type is Secondary:

1. **Primary unreachable?** The Primary node's state is `Unreachable` and its `LastSeen` is older than the timeout. If it was never seen since startup, this node's start time is used instead, so a node that just started doesn't act on no data.
2. **Not paused** (see below).
3. **Is the process really down?** TCP-connect (3 s) to each Primary IP on port 53 and on its web service port. If anything accepts, only the API heartbeat is failing; it doesn't promote, and logs why.
4. **Its own vote.** It follows the same one-candidate-per-window rule as the other voters.
5. **Tie-breaker:**
   - **3+ nodes:** it asks each other Secondary via `GET /api/admin/cluster/primaryReachability?candidateNodeName=<self>`. A vote counts only if the voter has the same Primary ID and reports it unreachable. Promotion needs a **majority of all nodes, Primary included** (`n/2 + 1`): 2 of 3, 3 of 4, 3 of 5.
   - **2 nodes:** there's no third voter, so it checks a **witness**: the configured addresses, or else its default gateway(s).
     - The check is ICMP ping ×3, falling back to TCP connect on 53/80/443, where even a refused connection proves the host is up.
     - Witness reachable but Primary not: the Primary is the one that's gone, so it promotes.
     - Neither reachable: this node is probably the isolated one, so it does nothing.
6. **Promote** using the existing `PromoteToPrimaryNodeAsync(forceDeletePrimary: true)`. Then it turns auto failover off and clears the successor, so an administrator has to choose the next successor deliberately. It saves and notifies.

### Voting rules (preventing two Primaries)

A node grants a "Primary is down" vote only if all of these hold:
- auto failover is enabled;
- it isn't paused;
- the candidate is the successor **in its own view**;
- it hasn't granted a vote to a **different** candidate within the last timeout window.

The successor's vote for itself follows the same rule.

So if an administrator changes the successor just before the Primary fails, nodes that disagree about the candidate don't produce two majorities. The cost is that no promotion happens until their views agree again: safe, but not live.

### Fencing (the old Primary coming back)

`CheckPrimaryConsistency()` runs on every heartbeat response:

- **This node is Primary, and the remote reports a different Primary that *is* in this node's node list:** this node was replaced while it was unreachable. It **fences** itself:
  - it stops sending notifies;
  - `transferConfig` refuses;
  - the Cluster page shows a banner;
  - the state is persisted, so it survives a restart.

  The admin deletes the Cluster on it and rejoins it as a Secondary. Secondaries already reject its notifies, because they removed its node ID during the promotion (existing `UpdatePrimaryNodeAsync` behavior).
- **The remote reports a Primary that this node removed:** the remote missed the promotion notify, so this node re-notifies it. This is rate-limited to once per 5 minutes per node.

### Maintenance pause

- `Pause Failover` on the Primary's Cluster page, or `/api/admin/cluster/primary/pauseAutoFailover?minutes=N` (1–1440; 0 resumes, Administration: Modify).
- The Primary sends the pause to all Secondaries immediately (`TriggerNotifyAllSecondaryNodes(0)`).
- It's propagated as **remaining seconds** rather than a timestamp, so clock differences between nodes don't matter.
- It's persisted on each node. While it's active, the successor doesn't promote and voters don't grant votes.
- Each node reports its remaining pause, so the badge confirms the successor received it before the Primary is rebooted.

### Related fix

`ConfigRefreshTimerCallbackAsync`'s `finally` no longer re-arms the refresh timer once this node is the Primary. Previously, a refresh callback already queued when a promotion happened would throw on the new Primary and then retry every retry interval indefinitely.

## Data model / API changes

- **`/api/admin/cluster/state`**, top level:
  - `configLastModified` (Primary only);
  - `autoFailoverEnabled`, `autoFailoverSuccessorNodeName`, `autoFailoverTimeoutSeconds`, `autoFailoverWitnessAddresses[]`, `autoFailoverPauseRemainingSeconds`;
  - `fenced`, `fencedByNodeName`.
- **`/api/admin/cluster/state`**, per node: `configLastSynced` (any Secondary), `autoFailoverPauseRemainingSeconds` (when paused).
- **`/api/admin/cluster/primary/setOptions`:** new optional parameters `autoFailoverEnabled`, `autoFailoverSuccessorNodeName`, `autoFailoverTimeoutSeconds`, `autoFailoverWitnessAddresses` (comma or space separated; an empty value means the default gateway). Omitted parameters keep their current values.
- **New endpoints:**
  - `GET /api/admin/cluster/primaryReachability?candidateNodeName=` (Administration: Delete; used between nodes);
  - `GET/POST /api/admin/cluster/primary/pauseAutoFailover?minutes=` (Administration: Modify).
- **HttpApi models:** `ClusterInfo` gains the fields above. New `ClusterPrimaryReachability`.
- **`cluster.config` format version 4** (reads 1 to 4):
  - v2 adds auto failover enabled, successor, timeout, fenced and fenced-by;
  - v3 adds the witness address list and the paused-until time;
  - v4 adds the Primary term.
- **Primary term.** Every promotion increments a term, which Secondaries copy from the Primary on each heartbeat (`primaryTerm` in the cluster state). A Primary fences itself only when the other Primary itself answers its heartbeat with a higher term. A Secondary reporting a different Primary is logged but never enough, so one stale, misled or compromised node can't stop the real Primary.

## Code changes

| File | Change |
|---|---|
| `Cluster/ClusterManager.cs` | Everything above: options and validation, config v3, the freshness check, the failover timer and decision procedure, voting, liveness and witness probes, fencing, pause, and the refresh timer fix. |
| `Cluster/ClusterNode.cs` | Records each remote node's `configLastSynced` and pause state from heartbeats. Calls the freshness check (Primary heartbeat) and `CheckPrimaryConsistency` (all heartbeats). Adds `GetPrimaryReachabilityAsync`. |
| `WebServiceClusterApi.cs` | New state fields, `setOptions` parameters (validated first), and the `primaryReachability` and `pauseAutoFailover` endpoints. |
| `DnsWebService.cs` | Two routes. |
| `DnsServerCore.HttpApi/*` | Client method and models. |
| `www/index.html`, `www/js/cluster.js` | Options fields, Pause/Resume buttons, badges, fenced banner. |
| `CLUSTER-IMPROVEMENTS.md` (new) | Documentation. Can move into the PR text or the docs if you prefer. |

## Compatibility

- **Auto failover is off by default.** Without it, the only behavior changes are the catch-up sync and the extra status fields.
- **Mixed versions:**
  - Older nodes ignore the new JSON fields.
  - A new Secondary under an old Primary sees `configLastModified` missing and skips the check. Auto failover is reported as disabled, so it never activates.
  - Old nodes don't run the catch-up check and won't answer votes. Unreachable voters don't count, so in a mixed cluster the majority is harder to reach, never easier.
- **Downgrade:** `cluster.config` is written as v4, which older builds can't read. ⚠ **Question for maintainers:** should it keep writing v1 while none of the new options are set, so downgrades keep working?

## Testing

| What | Result |
|---|---|
| Builds on `develop` (with TechnitiumLibrary `develop`) and on `v15.6.2`, no new warnings | ✔ |
| Web console JS syntax check | ✔ |
| Merges cleanly with the DHCP failover PR in either order; combined build | ✔ |
| Independent code review of the failover, voting and fencing logic. It found 5 issues, all fixed before this commit: (1) double promotion when the successor changed just before failure, (2) a half-applied `setOptions` on a validation error, (3) fencing not persisted, (4) re-notify on every heartbeat, (5) a refresh timer retry loop after promotion. | ✔ |
| Live clusters | ☐ **Not yet tested.** To do: missed notify (block a Secondary during a change); 3-node Primary loss → promotion and old Primary fenced on return; 3-node partition where the successor is isolated → no promotion; 2-node Primary loss with router witness → promotion; 2-node with the successor's uplink unplugged → no promotion; pause during a Primary reboot → no promotion. |

> Fill in the last row with your own results before submitting.

## Known limitations / questions for maintainers

- **Single designated successor.** If it's also down, there's no automatic failover. An ordered successor list would be a follow-up.
- **One 2-node split-brain case remains.** If only the path between the two nodes breaks while both still reach the witness, the successor promotes while the Primary is still running. A host firewall rule, or a virtual switch dropping traffic between two VMs, could cause this. The TCP liveness probe doesn't help here, because it uses the same broken path. When the path comes back, the old Primary fences itself; changes made on it in the meantime are not merged. A second independent heartbeat path, such as a direct link between the nodes, would close this and could come in a follow-up.
- **Matching by node name.** The successor is matched by name. Renaming or removing it leaves auto failover enabled but inert until a new one is picked.
- **No automatic rejoin.** A fenced former Primary has to be rejoined by hand. Doing it automatically would mean discarding its divergent changes, so I kept that a manual decision.
