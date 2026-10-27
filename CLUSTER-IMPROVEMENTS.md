# Cluster improvements

These changes add three things to the DNS Cluster.

## 1. Missed-notify catch-up (faster, more reliable sync)

Previously, a Secondary picked up a change when the Primary's notify reached it (about 10 s). A Secondary that missed the notify, for example because it was briefly unreachable, waited for the next config refresh (900 s by default).

Now the Primary records when its config last changed and reports this as `configLastModified` in `/api/admin/cluster/state`. Each Secondary compares it with its own last sync on every heartbeat to the Primary (every 30 s by default) and syncs if it is behind. A missed change now arrives within one heartbeat interval.

## 2. Cluster health view

The Administration > Cluster table now shows:

- **Last synced** for every Secondary, not only the node you are viewing from. Viewed from the Primary, a **Sync Pending** badge marks a Secondary that hasn't synced the latest change yet.
- A **Version Mismatch** badge on nodes running a different version than the node you are viewing from.
- A red **fenced** banner on a former Primary that was replaced by auto failover (see below).

## 3. Automatic Primary failover

This is set in Cluster Options on the Primary: **Enable Auto Failover**, a **Successor Node** (a Secondary) and a **Failover Timeout** (default 120 s).

1. Only the successor acts. Every 10 s it checks whether the Primary has been unreachable for longer than the timeout.
2. It then tries plain TCP connects to the Primary's DNS port (53) and web service port. If either accepts, the Primary is still running (only its API heartbeat is failing) and nothing happens.
3. **2-node clusters:** there is nobody else to vote, so the successor checks a **witness** instead. A witness is an always-on device both nodes can reach, normally your router.
   - Leave the witness empty and the successor uses its own default gateway.
   - It pings the witness, or failing that tries a TCP connect on port 53, 80 or 443. Even a refused connection counts as reachable.
   - If the successor can reach the witness but not the Primary, the Primary is the one that's gone, and the successor promotes.
   - If it can reach neither, the successor is probably the isolated node, and it does nothing.
4. **3+ node clusters:** the successor asks every other Secondary (`/api/admin/cluster/primaryReachability`) and promotes only if a **majority of all Cluster nodes** agree that the Primary is down. The Primary is included in the count and the successor counts itself. A 3-node cluster needs 2 votes and a 5-node cluster needs 3. The witness is not used.
   - A node votes "down" only for the candidate that **it** considers the successor.
   - It gives its vote to at most one candidate per timeout window, and the successor's vote for itself follows the same rule.
   - This means two nodes can't both win a majority, even if they disagree about who the successor is right after you change it. In that case no promotion happens until their views agree again.
5. While a maintenance pause is active (see below), the successor never promotes and other nodes don't vote. A pause can be set with `/api/admin/cluster/primary/pauseAutoFailover?minutes=N`, where `N=0` resumes.
6. Promotion uses the existing forced "Promote To Primary" path. Afterwards auto failover is turned off until you pick a new successor.

**Fencing.** When the old Primary comes back, it still thinks it is the Primary. Every promotion increments a **Primary term**, which Secondaries copy from the Primary on each heartbeat. The old Primary fences itself only when the new Primary itself answers its heartbeat as Primary with a higher term than its own. A Secondary merely reporting a different Primary is logged but never enough, so a stale or compromised Secondary can't stop the real Primary. When it fences:

- it stops sending notifies,
- it refuses config transfers,
- its Cluster page shows a banner.

The fenced state is saved in `cluster.config`, so it survives a restart. Secondaries already reject its notifies because they removed its node ID during the promotion. To bring it back, **delete the Cluster on it and join it to the new Primary as a Secondary**. Any changes made on it while it was cut off are not merged.

The new Primary also re-notifies any Secondary that still follows the old Primary, for example one that missed the promotion notify.

### 2-node trade-offs

- **Planned reboots.** Before rebooting the Primary, click **Pause Failover** on the Primary's Cluster page and enter a number of minutes (1–1440).
  - The Primary pushes the pause to the other nodes right away.
  - Wait until the successor shows a **Failover Paused** badge, which can take up to about a minute, then reboot.
  - The pause ends on its own when the time is up, or click **Resume Failover**.
  - Nodes receive the remaining time rather than a clock time, so clock differences between nodes don't matter.
  - Without a pause, a reboot that takes longer than the failover timeout makes the Secondary take over, and you then have to rejoin the old Primary by hand.
- **Split brain is still possible in one case.** If only the path between the two nodes breaks while both can still reach the router, the successor promotes while the Primary is still running. A host firewall rule could cause this, or two VMs whose virtual switch drops traffic between them. When the path comes back, the old Primary fences itself, so nothing is silently overwritten, but any changes made on it in between are lost. A witness on the same switch as both nodes makes this unlikely.

### Limits

- There is a single designated successor. If the successor is down too, there is no automatic failover.
- The successor is matched by node name. If you rename or remove it, auto failover never fires, even though it still shows as enabled. Pick a new successor in Cluster Options; a missing successor is cleared when you save with auto failover disabled.
- If the Primary fails right after you change the successor, nodes that haven't picked up the change disagree about the candidate, and no promotion happens. This is safe, but there is no failover until their views agree again.
- The **Sync Pending** badge uses each Secondary's last-synced time as of the Primary's last heartbeat to it. It can linger for up to one heartbeat interval after a change. After a Primary restart it shows for every node, and on older-version nodes it stays until their next scheduled config refresh, because they don't run the catch-up check.
- Votes reflect each node's own heartbeat view of the Primary. Default heartbeats run every 30 s with 10 s retries, so detection takes roughly the timeout plus one check interval.
- The Cluster config file format is now version 3 (it still reads versions 1 and 2). Older builds can't read it, so downgrading needs a re-initialized cluster.
- None of this has been tested on a live multi-node cluster yet.
