# PR: Automatic TLS certificates with ACME (DNS-01)

**Branch:** `optimusprime-nn/DnsServer:upstream/acme` → **`TechnitiumSoftware/DnsServer:develop`**
**Commits:** 5 commits, 11 files, about +3000 lines. It's independent of the DHCP failover and cluster improvements PRs; all three merge cleanly in any order.

---

## Summary

Today the web console's HTTPS and DNS-over-TLS/HTTPS/QUIC need a PKCS #12 file that the admin obtains, copies onto the server and renews by hand. This PR lets the server **obtain, install and renew its own certificate** from any RFC 8555 ACME certificate authority, configured in a new **Settings > Certificates (ACME)** tab.

It uses **DNS-01** validation, so the server doesn't have to be reachable from the internet, and wildcard names work. Since Technitium is itself a DNS server, it can publish the challenge record in a zone it hosts. For domains hosted elsewhere, it supports RFC 2136 dynamic updates and Cloudflare, behind a small provider interface so more providers can be added.

It's off by default, and nothing changes until an admin enables it.

## Features

- **Certificate authorities:**
  - Let's Encrypt (staging is the default while you set up), ZeroSSL and Google Trust Services. The latter two need External Account Binding (EAB) credentials.
  - Any custom RFC 8555 directory, for example step-ca.
- **Terms of service:** the CA's terms link is shown and the admin must tick the agreement. Changing the CA resets the agreement.
- **DNS-01 providers:**
  1. **Zone on this server:** a Primary zone; no credentials needed.
  2. **RFC 2136:** dynamic update, signed with a TSIG key chosen from Settings > TSIG. Works with BIND, PowerDNS, Knot, Windows DNS or another Technitium server, including a Cluster Primary.
  3. **Cloudflare:** an API token with Zone > DNS > Edit.
- **CNAME delegation:** `_acme-challenge` CNAMEs are followed and the record is published at the target. A domain hosted at any provider can delegate just its challenge name to a zone this server controls.
- **Propagation check:** the server asks the zone's **public** authoritative name servers directly before asking the CA to validate. It finds them through configurable public resolvers, so a local split-horizon copy of the zone doesn't hide the public view.
  - Servers that don't respond at all, such as IPv6 servers on a network without IPv6, are skipped.
  - If none respond, which happens when there's no NAT hairpin, it logs a warning and lets the CA validate.
- **Key type:** ECDSA P-256 (the default) or RSA 2048.
- **Install:** the certificate goes to the web service and/or DNS-over-TLS/HTTPS/QUIC, through the same code paths as the settings API:
  - On a Cluster node, the node's certificate is republished to the Cluster.
  - The encrypted DNS listeners restart only when the certificate path changes.
  - Renewals replace the file in place.
- **HTTPS before the first certificate:** once ACME manages the web certificate, HTTPS can be enabled immediately, from the Web Service tab or with **Enable HTTPS now** on the ACME tab. A temporary self-signed certificate is used until the first ACME certificate replaces it. This also fixes the existing behavior where enabling HTTPS with no certificate path and self-signed off was silently undone on save; without ACME, that case now shows an explanation.
- **Renewal:** checked 2 minutes after start and then every 6 hours. A certificate is renewed 30 days before expiry (configurable), when the names change, or when none has been issued yet.
- **Cluster:** each node manages its own certificate, since every node has its own name. ACME settings aren't part of the Cluster config transfer.
- **Testing and visibility:** a **Test DNS-01** button publishes, verifies and removes a test record without contacting the CA, and a log shows each step.

## Design decisions

| Decision | Why |
|---|---|
| In-house ACME client (`AcmeClient`, ~500 lines) rather than a NuGet package | No new dependency for the project, and only the parts needed (dns-01, ES256 account key) are implemented. It uses `System.Security.Cryptography` only. |
| DNS-01 only | No inbound port 80/443 needed, wildcards supported, and it fits a DNS server naturally. |
| Provider interface `IDns01Provider` | Adding a provider is one class plus a GUI option. |
| Propagation via public resolvers + direct authoritative queries | Matches what the CA sees. Using this server's own resolver would return the local zone and pass falsely. |
| Outbound HTTP through `HttpClientNetworkHandler` with this server as resolver and proxy | The same as the existing update check: it honors the configured proxy and IPv6 mode. |
| Renewals replace the same PFX path | The existing 60-second certificate reload timers and the Cluster certificate update keep working unchanged. |
| Settings stored in `config/acme/acme.config` (binary, versioned) | Matches the project's config conventions, and keeps ACME state out of `dns.config` so it isn't transferred in a Cluster. |
| Settings changes refused while a request is running | Guarantees the order and the CSR use the same names. |
| Secrets never returned by the API (`*Set` flags only); empty secret fields keep the saved value | Avoids leaking the Cloudflare token or the EAB key to the browser. |

## Files

| File | Change |
|---|---|
| `Acme/AcmeClient.cs` (new) | RFC 8555: directory, nonces (badNonce retry), ES256 JWS, account with EAB, order, dns-01 authorization, finalize, download |
| `Acme/Dns01Providers.cs` (new) | `IDns01Provider`; local zone, RFC 2136, Cloudflare providers; `Dns01Resolver` (CNAME following, propagation check) |
| `Acme/AcmeManager.cs` (new) | Settings and storage, account handling (including re-registration if the CA account is gone), issuance workflow, renewal timer, install, status and log |
| `WebServiceAcmeApi.cs` (new) | `/api/settings/acme/{get,set,directoryInfo,request,testDns01}` |
| `DnsWebService.cs` | Manager lifetime and routes; `InstallWebServiceTlsCertificate` / `InstallDnsTlsCertificate`; temporary self-signed fallback while ACME manages the web certificate |
| `WebServiceSettingsApi.cs` | Keeps HTTPS enabled when ACME manages the web certificate; reports which certificates ACME manages |
| `DnsServerCore.csproj` | Includes `acme.js` in the build output |
| `www/index.html`, `www/js/acme.js` (new), `www/js/main.js` | Settings tab; the page-wide Save Settings button is hidden on the ACME tab, which has its own |
| `ACME.md` (new) | User documentation: setup, providers, CNAME delegation, Cluster, troubleshooting, limitations, API |

## API

| Endpoint | Permission |
|---|---|
| `/api/settings/acme/get` | Settings: View |
| `/api/settings/acme/set` | Settings: Modify |
| `/api/settings/acme/directoryInfo` | Settings: Modify (the server fetches an admin-supplied URL) |
| `/api/settings/acme/request` | Settings: Modify |
| `/api/settings/acme/testDns01` | Settings: Modify |
| `/api/settings/acme/enableHttps` | Settings: Modify |

All accept `node=`. Parameters are documented in `ACME.md`.

## Testing

| What | Result |
|---|---|
| Builds on `develop` (with TechnitiumLibrary `develop`) and on `v15.6.2`, no new warnings | ✔ |
| Merges cleanly with the DHCP failover and cluster improvements PRs in either order; combined build | ✔ |
| Independent code review. It found 1 High, 5 Medium and 9 Low issues; all fixed or documented before this commit. The High one: renewals with an existing account failed because the directory wasn't loaded. | ✔ |
| **End-to-end against Pebble**, Let's Encrypt's test CA (v2.10.1), on Docker with Pebble validating through Technitium. Scenarios: zone on this server with a wildcard (two TXT values at one name); CNAME delegation; RFC 2136 with a TSIG key scoped to one record, reusing the existing account. After each, the certificate served on HTTPS was verified against Pebble's root, including the host name. Pebble's 5% nonce rejection exercised the badNonce retry, and no challenge records were left behind. The script runs on every push in the fork's CI. | ✔ |
| Real CA: Let's Encrypt with Cloudflare DNS-01, on two servers behind a split-horizon local copy of the zone (staging first, then production) | ✔ Certificates issued and served on the web console of both servers |

The end-to-end script (`.github/acme-e2e/run.sh` in the fork) can be added to this PR if you'd like it in the repository.

## Limitations / questions for maintainers

- **Backups:** `config/acme/` isn't included in settings backups. Should it be part of the "DNS settings" backup option?
- **Permissions:** with the local-zone provider, a user with Settings: Modify can publish challenge TXT records in any Primary zone, regardless of per-zone permissions. That role is effectively an administrator already. Should it require Zones: Modify as well?
- **Name resolution for the CA:** outbound requests resolve through this DNS server, as the update check does. An internal CA's name must therefore resolve on this server, which in practice it does.
- **Providers:** only three are built in. Happy to add others, such as Route 53, deSEC, Hetzner or DigitalOcean, either here or in follow-ups.
