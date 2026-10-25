#!/usr/bin/env bash
# End-to-end ACME test.
#
# Runs a Technitium DNS Server image (this build) and Pebble, Let's Encrypt's official ACME test CA,
# on a private Docker network. Pebble validates dns-01 challenges by querying the Technitium container.
#
# Scenarios, each ending with an HTTPS request that verifies the full chain and host name:
#   1. Zone on this server: dns.acme.test + *.acme.test (two TXT values at one name)
#   2. CNAME delegation: _acme-challenge.dns.other.test -> a name in acme.test
#   3. RFC 2136 with a TSIG key scoped to one record: dns.rfc.test
#      (reuses the ACME account from scenario 1, i.e. the renewal code path)
#
# usage: run.sh <technitium-image>
set -euo pipefail

IMAGE="$1"
PEBBLE_IMAGE="${PEBBLE_IMAGE:-ghcr.io/letsencrypt/pebble:2.10.1}"
PEBBLE_CA_URL="${PEBBLE_CA_URL:-https://raw.githubusercontent.com/letsencrypt/pebble/v2.10.1/test/certs/pebble.minica.pem}"

ID="acme-e2e-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-$$"
NET="$ID"
DNS="$ID-dns"
PEB="$ID-pebble"
WORK="$(mktemp -d)"
ADMIN_PASS="$(head -c 18 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')" #throwaway credential for this ephemeral container
TOKEN=""

log() { echo "::group::$*" >/dev/null 2>&1 || true; echo "== $*"; }

cleanup() {
    local code=$?
    if [ $code -ne 0 ]; then
        echo "== FAILED, diagnostics follow"
        if [ -n "$TOKEN" ]; then
            echo "-- ACME status/log:"
            docker exec "$DNS" curl -s "http://127.0.0.1:5380/api/settings/acme/get?token=$TOKEN" | python3 -c '
import sys, json
r = json.load(sys.stdin).get("response", {})
print("lastError:", r.get("lastError"))
print("certificate:", json.dumps(r.get("certificate")))
print("\n".join(r.get("log", [])))' || true
        fi
        echo "-- Technitium container log (tail):"; docker logs --tail 80 "$DNS" 2>&1 || true
        echo "-- Pebble container log (tail):"; docker logs --tail 80 "$PEB" 2>&1 || true
    fi
    docker rm -f "$DNS" "$PEB" >/dev/null 2>&1 || true
    docker network rm "$NET" >/dev/null 2>&1 || true
    rm -rf "$WORK"
    exit $code
}
trap cleanup EXIT

# call the Technitium API from inside its container; fail on API errors; print the "response" object as JSON
api() {
    local path="$1"; shift
    local query
    query="$(python3 -c 'import sys, urllib.parse; print(urllib.parse.urlencode(list(zip(sys.argv[1::2], sys.argv[2::2]))))' "$@")"
    local sep="?"; [[ "$path" == *"?"* ]] && sep="&"
    docker exec "$DNS" curl -sS --max-time 900 "http://127.0.0.1:5380/api/${path}${sep}token=${TOKEN}&${query}" | python3 -c '
import sys, json
raw = sys.stdin.read()
try:
    d = json.loads(raw)
except Exception:
    sys.exit("API returned non-JSON: " + raw[:300])
if d.get("status") != "ok":
    sys.exit("API error: " + str(d.get("errorMessage")) + " " + str(d.get("innerErrorMessage") or ""))
print(json.dumps(d.get("response", {})))'
}

# api call with a JSON body (settings with arrays such as tsigKeys)
api_json() {
    local path="$1" body="$2"
    docker exec -i "$DNS" curl -sS --max-time 120 -H "Content-Type: application/json" --data-binary @- "http://127.0.0.1:5380/api/${path}?token=${TOKEN}" <<<"$body" | python3 -c '
import sys, json
d = json.load(sys.stdin)
if d.get("status") != "ok":
    sys.exit("API error: " + str(d.get("errorMessage")))'
}

jq_py() { python3 -c "import sys, json; d = json.load(sys.stdin); print($1)"; }

wait_for_api() {
    for _ in $(seq 1 60); do
        if docker exec "$DNS" curl -sf -o /dev/null http://127.0.0.1:5380/api/status 2>/dev/null; then return 0; fi
        sleep 2
    done
    echo "Technitium web API did not come up"; return 1
}

# request a certificate and wait for the run to finish; asserts success and the expected names
issue_and_check() {
    local names_csv="$1"

    api settings/acme/request >/dev/null

    local status running lasterror
    for _ in $(seq 1 120); do
        status="$(api settings/acme/get)"
        running="$(jq_py 'd["running"]' <<<"$status")"
        [ "$running" = "False" ] && break
        sleep 3
    done

    lasterror="$(jq_py 'd.get("lastError")' <<<"$status")"
    if [ "$running" != "False" ]; then echo "ACME run did not finish"; return 1; fi
    if [ "$lasterror" != "None" ]; then echo "ACME run failed: $lasterror"; return 1; fi

    local issuer names
    issuer="$(jq_py 'd["certificate"]["issuer"]' <<<"$status")"
    names="$(jq_py '",".join(sorted(d["certificate"]["dnsNames"]))' <<<"$status")"
    echo "   issued by: $issuer"
    echo "   names:     $names"

    [[ "$issuer" == *"Pebble"* ]] || { echo "unexpected issuer"; return 1; }

    local expected
    expected="$(python3 -c 'import sys; print(",".join(sorted(sys.argv[1].split(","))))' "$names_csv")"
    [ "$names" = "$expected" ] || { echo "expected names $expected"; return 1; }
}

# HTTPS request to the web service, verifying the chain against Pebble's root and the host name
check_https() {
    local host="$1"
    for _ in $(seq 1 35); do
        if docker exec "$DNS" curl -sS -o /dev/null --max-time 10 --cacert /tmp/pebble-root.pem \
            --resolve "$host:53443:127.0.0.1" "https://$host:53443/" 2>"$WORK/curl.err"; then
            echo "   HTTPS OK for $host (chain and host name verified)"
            return 0
        fi
        sleep 2
    done
    echo "HTTPS verification failed for $host: $(cat "$WORK/curl.err")"; return 1
}

refresh_pebble_root() {
    # Pebble generates a new root at start; its management API serves it (TLS signed by minica, which the container trusts)
    docker exec "$DNS" sh -c 'curl -sSf https://pebble:15000/roots/0 > /tmp/pebble-root.pem'
}

########################################################################################################

log "Setup"
curl -sSfL "$PEBBLE_CA_URL" -o "$WORK/pebble-minica.pem"
docker pull -q "$PEBBLE_IMAGE"
docker network create "$NET" >/dev/null

# Technitium trusts Pebble's API certificate (minica) so the ACME client can talk to it over HTTPS
docker create --name "$DNS" --network "$NET" --network-alias dns --user root \
    -e DNS_SERVER_DOMAIN=ns1.acme.test -e DNS_SERVER_ADMIN_PASSWORD="$ADMIN_PASS" \
    --entrypoint /bin/sh "$IMAGE" -c \
    'cp /pebble-minica.pem /usr/local/share/ca-certificates/pebble-minica.crt && update-ca-certificates >/dev/null && exec /usr/bin/dotnet /opt/technitium/dns/DnsServerApp.dll /etc/dns' >/dev/null
docker cp "$WORK/pebble-minica.pem" "$DNS:/pebble-minica.pem"
docker start "$DNS" >/dev/null
wait_for_api

DNS_IP="$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$DNS")"
echo "   Technitium at $DNS_IP"

# Pebble resolves challenge records through Technitium; default settings also reject 5% of nonces (exercises badNonce retry)
docker run -d --name "$PEB" --network "$NET" --network-alias pebble -e PEBBLE_VA_NOSLEEP=1 \
    "$PEBBLE_IMAGE" -config test/config/pebble-config.json -dnsserver "$DNS_IP:53" >/dev/null

TOKEN="$(docker exec "$DNS" curl -sS "http://127.0.0.1:5380/api/user/login?user=admin&pass=$ADMIN_PASS" | jq_py 'd["token"]')"

for _ in $(seq 1 30); do
    docker exec "$DNS" curl -sf -o /dev/null https://pebble:14000/dir && break
    sleep 1
done
docker exec "$DNS" curl -sSf -o /dev/null https://pebble:14000/dir
echo "   Pebble directory reachable over trusted HTTPS"

# zones: acme.test (local zone + CNAME target), other.test (CNAME delegation), rfc.test (dynamic updates)
api zones/create zone acme.test type Primary >/dev/null
api zones/records/add zone acme.test domain ns1.acme.test type A ipAddress "$DNS_IP" ttl 60 >/dev/null
api zones/create zone other.test type Primary >/dev/null
api zones/records/add zone other.test domain _acme-challenge.dns.other.test type CNAME cname delegated.acme.test ttl 60 >/dev/null
api zones/create zone rfc.test type Primary >/dev/null

# web service HTTPS on 53443, starting with the self-signed certificate until ACME installs its own
api settings/set webServiceEnableTls true webServiceUseSelfSignedTlsCertificate true webServiceTlsPort 53443 >/dev/null
sleep 5
wait_for_api

########################################################################################################

log "Scenario 1: zone on this server, wildcard"
api settings/acme/set enabled true directoryUrl https://pebble:14000/dir termsOfServiceAgreed true \
    domains "dns.acme.test,*.acme.test" dns01Provider LocalZone publicResolvers 127.0.0.1 \
    useForWebService true useForDnsTls false propagationTimeoutSeconds 60 >/dev/null

echo "   Test DNS-01: $(api settings/acme/testDns01 | jq_py 'd["result"]')"
issue_and_check "dns.acme.test,*.acme.test"
refresh_pebble_root
check_https dns.acme.test
check_https www.acme.test

log "Scenario 2: CNAME delegation"
api settings/acme/set domains "dns.other.test" >/dev/null
issue_and_check "dns.other.test"
check_https dns.other.test

log "Scenario 3: RFC 2136 with TSIG, existing account"
api_json settings/set '{"tsigKeys":[{"keyName":"acme-e2e","sharedSecret":"","algorithmName":"hmac-sha256"}]}'
api zones/options/set zone rfc.test update UseSpecifiedNetworkACL updateNetworkACL 127.0.0.1 \
    updateSecurityPolicies "acme-e2e|_acme-challenge.dns.rfc.test|TXT" >/dev/null
api settings/acme/set domains "dns.rfc.test" dns01Provider Rfc2136 rfc2136Server 127.0.0.1 rfc2136TsigKeyName acme-e2e >/dev/null
issue_and_check "dns.rfc.test"
check_https dns.rfc.test

# challenge records must be cleaned up by every provider
for name in _acme-challenge.dns.acme.test delegated.acme.test _acme-challenge.dns.rfc.test; do
    left="$(docker exec "$DNS" sh -c "command -v dig >/dev/null && dig +short @127.0.0.1 $name TXT" || true)"
    [ -z "$left" ] || { echo "leftover TXT at $name: $left"; exit 1; }
done
echo "   No leftover challenge TXT records"

log "All ACME end-to-end scenarios passed"
