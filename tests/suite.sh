#!/bin/bash
B=http://localhost:8090
P=0; F=0
ok(){ if [ "$1" = "$2" ]; then P=$((P+1)); echo "  PASS $3"; else F=$((F+1)); echo "  FAIL $3 (got $1, want $2)"; fi; }

# ---- extraction (mock) ----
code=$(curl -s -o /tmp/rb1.json -w "%{http_code}" -m 20 -X POST $B/api/extract -H "Content-Type: application/json" -d '{"text":"CEYLINCO GENERAL INSURANCE. Motor policy for Toyota Aqua. Period of cover ends 14 March 2027."}')
ok "$code" "200" "extract returns 200"
python3 - <<'EOF'
import json, datetime
d = json.load(open('/tmp/rb1.json'))
exp = datetime.date.fromisoformat(d['expiresOn'])
act = datetime.date.fromisoformat(d['actByOn'])
checks = [
  (d['type'] == 'vehicle-insurance', "type identified"),
  (d['category'] == 'Vehicle', "category identified"),
  (isinstance(d['leadDays'], int) and d['leadDays'] == 30, "lead days applied for insurance (30)"),
  ((exp - act).days == d['leadDays'], "actByOn = expiresOn - leadDays"),
  ('privacy' in d and 'discarded' in d['privacy'], "privacy receipt present"),
  (0 <= d['confidence'] <= 100, "confidence in range"),
]
for c,n in checks: print(("  PASS " if c else "  FAIL ")+n)
open('/tmp/pyres','w').write(f"{sum(1 for c,_ in checks if c)} {sum(1 for c,_ in checks if not c)}")
EOF
read pp pf < /tmp/pyres; P=$((P+pp)); F=$((F+pf))

code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 -X POST $B/api/extract -H "Content-Type: application/json" -d '{"text":"hi"}')
ok "$code" "400" "extract rejects empty/short input"

# ---- items CRUD + act-by intelligence ----
iid=$(curl -s -m 5 -X POST $B/api/items -H "Content-Type: application/json" -d '{"title":"Passport — Test","type":"passport","category":"Travel","person":"Kalaru","expiresOn":"2027-06-15"}' | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['id'])")
[ -n "$iid" ] && ok "yes" "yes" "item created with id" || ok "no" "yes" "item created with id"
actby=$(curl -s -m 5 $B/api/items | python3 -c "
import json,sys,datetime
items=json.load(sys.stdin)
it=[i for i in items if i['title'].startswith('Passport')][0]
e=datetime.date.fromisoformat(it['expiresOn']); a=datetime.date.fromisoformat(it['actByOn'])
print((e-a).days)")
ok "$actby" "180" "passport act-by computed 180 days before expiry"

# privacy scrub: a policy-number-looking string in title gets masked
scrub=$(curl -s -m 5 -X POST $B/api/items -H "Content-Type: application/json" -d '{"title":"Policy VP4429918822X renewal","type":"vehicle-insurance","expiresOn":"2027-01-10"}' | python3 -c "import json,sys; print('masked' if '•••' in json.load(sys.stdin)['title'] else 'leaked')")
ok "$scrub" "masked" "long identifiers scrubbed from stored fields"

code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 -X POST $B/api/items -H "Content-Type: application/json" -d '{"title":"","expiresOn":"not-a-date"}')
ok "$code" "400" "item rejects missing title / bad date"

# PATCH: change expiry -> actBy recomputed
newact=$(curl -s -m 5 -X PATCH $B/api/items/$iid -H "Content-Type: application/json" -d '{"expiresOn":"2028-06-15"}' | python3 -c "
import json,sys,datetime
d=json.load(sys.stdin)
e=datetime.date.fromisoformat(d['expiresOn']); a=datetime.date.fromisoformat(d['actByOn'])
print((e-a).days, d['expiresOn'])")
ok "$newact" "180 2028-06-15" "PATCH expiry recomputes act-by"

# renewed flow: lastRenewedOn persists
lr=$(curl -s -m 5 -X PATCH $B/api/items/$iid -H "Content-Type: application/json" -d '{"lastRenewedOn":"2026-08-01"}' | python3 -c "import json,sys; print(json.load(sys.stdin)['lastRenewedOn'])")
ok "$lr" "2026-08-01" "mark-renewed persists lastRenewedOn"

cnt=$(curl -s -m 5 $B/api/items | python3 -c "import json,sys; print(len(json.load(sys.stdin)))")
ok "$cnt" "2" "items list reflects both"

# export/import round trip
curl -s -m 5 $B/api/items/export -o /tmp/rbexp.json
ecnt=$(python3 -c "import json; print(len(json.load(open('/tmp/rbexp.json'))))")
ok "$ecnt" "2" "export contains all items"
code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 -X POST $B/api/items/import -H "Content-Type: application/json" --data-binary @/tmp/rbexp.json)
ok "$code" "200" "import round-trip accepted"

code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 -X DELETE $B/api/items/$iid)
ok "$code" "200" "item delete works"
code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 -X DELETE $B/api/items/$iid)
ok "$code" "404" "double delete returns 404"

# ---- playbooks ----
t=$(curl -s -m 5 "$B/api/playbook?type=passport&country=LK" | python3 -c "import json,sys; d=json.load(sys.stdin); print('LK' if 'Sri Lankan' in d['title'] else 'wrong')")
ok "$t" "LK" "country-specific playbook matched (passport LK)"
t=$(curl -s -m 5 "$B/api/playbook?type=passport&country=DE" | python3 -c "import json,sys; d=json.load(sys.stdin); print('generic' if d['country']=='*' else 'wrong')")
ok "$t" "generic" "falls back to generic playbook for unseeded country"
code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 "$B/api/playbook?type=spaceship&country=*")
ok "$code" "404" "unknown type returns 404"
pcnt=$(curl -s -m 5 $B/api/playbooks | python3 -c "import json,sys; print(len(json.load(sys.stdin)))")
ok "$pcnt" "13" "all seeded playbooks listed"

# ---- static + health ----
code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 $B/)
ok "$code" "200" "frontend served at /"
h=$(curl -s -m 5 $B/api/health | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['app'], d['provider'], d['playbooks'])")
ok "$h" "renewalbrain mock 13" "health reports app + provider + playbooks"

echo "========================"
echo "FINAL: $P passed, $F failed"
exit $F
