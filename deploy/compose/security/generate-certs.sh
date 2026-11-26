#!/bin/sh
# Development certificates (A5): a local CA and server certificates for OpenSearch and RabbitMQ,
# written to the "certs" volume once and renewed when less than 30 days are left. Each service
# mounts only its own folder: ca/ (the CA certificate, for clients), opensearch/, rabbitmq/,
# and in the hardened install (F6) postgres/ and valkey/.
# Installs use certificates from their own CA (docs/architecture/security-baseline.md).
set -eu

out=/certs
days=825
subject_org="/O=Zelvo Development"
mkdir -p "$out/ca" "$out/opensearch" "$out/rabbitmq" "$out/postgres" "$out/valkey" "$out/.ca-private"
chmod 700 "$out/.ca-private"

valid() { [ -f "$1" ] && openssl x509 -checkend 2592000 -noout -in "$1" >/dev/null 2>&1; }

if ! valid "$out/ca/ca.crt"; then
  echo "Creating the development CA"
  openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$out/.ca-private/ca.key"
  openssl req -x509 -new -key "$out/.ca-private/ca.key" -sha256 -days "$days" \
    -subj "/CN=Zelvo Development CA$subject_org" -out "$out/ca/ca.crt" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"
  rm -f "$out"/opensearch/*.crt "$out"/rabbitmq/*.crt "$out"/postgres/*.crt "$out"/valkey/*.crt
fi

# server_cert <folder> <file name> <common name> <extended key usage> <subject alt names>
server_cert() {
  dir="$out/$1"; name="$2"
  if valid "$dir/$name.crt"; then return; fi
  echo "Creating $1/$name.crt"
  # PKCS#8 keys (OpenSearch needs this format).
  openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$dir/$name.key"
  openssl req -new -key "$dir/$name.key" -subj "/CN=$3$subject_org" -out "$dir/$name.csr"
  printf 'basicConstraints=CA:FALSE\nkeyUsage=critical,digitalSignature,keyAgreement\nextendedKeyUsage=%s\n%s\n' "$4" "$5" > "$dir/$name.ext"
  openssl x509 -req -in "$dir/$name.csr" -CA "$out/ca/ca.crt" -CAkey "$out/.ca-private/ca.key" -CAcreateserial \
    -CAserial "$out/.ca-private/ca.srl" -days "$days" -sha256 -extfile "$dir/$name.ext" -out "$dir/$name.crt"
  rm -f "$dir/$name.csr" "$dir/$name.ext"
}

server_cert opensearch node opensearch serverAuth,clientAuth "subjectAltName=DNS:opensearch,DNS:localhost,IP:127.0.0.1"
# The security plugin's administrator certificate (applies security configuration changes).
server_cert opensearch admin zelvo-opensearch-admin clientAuth ""
server_cert rabbitmq server rabbitmq serverAuth "subjectAltName=DNS:rabbitmq,DNS:localhost,IP:127.0.0.1"
# Task F6: TLS for PostgreSQL and Valkey in the hardened install (development leaves them plain).
server_cert postgres server postgres serverAuth "subjectAltName=DNS:postgres,DNS:localhost,IP:127.0.0.1"
server_cert valkey server redis serverAuth "subjectAltName=DNS:redis,DNS:localhost,IP:127.0.0.1"

cp "$out/ca/ca.crt" "$out/opensearch/ca.crt"
cp "$out/ca/ca.crt" "$out/rabbitmq/ca.crt"
cp "$out/ca/ca.crt" "$out/valkey/ca.crt"

# Owners: OpenSearch runs as uid 1000, RabbitMQ as uid 100, PostgreSQL as 999 and the hardened
# install's Valkey as 999:1000; clients only read ca/.
chown -R 1000:1000 "$out/opensearch"
chown -R 100:101 "$out/rabbitmq"
chown -R 999:999 "$out/postgres"
chown -R 999:1000 "$out/valkey"
chmod 600 "$out"/opensearch/*.key "$out"/rabbitmq/*.key "$out"/postgres/*.key "$out"/valkey/*.key
chmod 644 "$out"/ca/ca.crt "$out"/opensearch/*.crt "$out"/rabbitmq/*.crt "$out"/postgres/*.crt "$out"/valkey/*.crt
echo "Certificates ready"
