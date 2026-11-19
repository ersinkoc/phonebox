#!/usr/bin/env bash
# Creates PhoneBox's own macOS code-signing certificate: self-signed, valid for 30 years, for code signing only.
#
# Why: an ad hoc signature changes with every build, and macOS ties the Accessibility permission (shortcuts that press
# keys) to it, so every update lost the permission (#269). Signed with one fixed certificate, every release has the same
# designated requirement (identifier + certificate), and the permission carries over. It is not an Apple Developer ID:
# Gatekeeper treats the agent as before (setup.sh and the self-update never add the quarantine flag).
#
# Run it once, keep the result safe, and never make a new one without need: a new certificate means every Mac user
# grants Accessibility once more.
#
#   tools/make-macos-signing-cert.sh [--out DIR] [--set-secrets]
#
# Writes DIR/phonebox-macos-signing.p12 and DIR/phonebox-macos-signing.password (DIR defaults to a new temporary
# folder). With --set-secrets it also stores both as the repository secrets MACOS_SIGNING_P12_BASE64 and
# MACOS_SIGNING_P12_PASSWORD with the GitHub CLI (gh), which release.yml uses. Put the two files in a password manager,
# then delete them. Needs OpenSSL 1.1.1 or later (Git Bash, Homebrew's openssl@3, or LibreSSL on macOS).
set -euo pipefail

out=""
set_secrets=0
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out="$2"; shift 2 ;;
    --set-secrets) set_secrets=1; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done
if [ -z "$out" ]; then out="$(mktemp -d)"; fi
mkdir -p "$out"
p12="$out/phonebox-macos-signing.p12"
pwfile="$out/phonebox-macos-signing.password"
if [ -e "$p12" ]; then echo "$p12 already exists; not overwriting it." >&2; exit 1; fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
umask 077

# the subject and extensions in a config file, not -subj: Git Bash would turn "/CN=..." into a Windows path
cat > "$work/req.cnf" <<'CNF'
[req]
prompt = no
distinguished_name = dn
x509_extensions = ext
[dn]
CN = PhoneBox Agent (self-signed)
[ext]
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
subjectKeyIdentifier = hash
CNF
openssl req -x509 -newkey rsa:3072 -sha256 -days 10950 -nodes -config "$work/req.cnf" \
  -keyout "$work/key.pem" -out "$work/cert.pem" 2>"$work/req.log" || { cat "$work/req.log" >&2; exit 1; }

PHONEBOX_P12_PASSWORD="$(openssl rand -base64 24 | tr -d '\r\n')"
export PHONEBOX_P12_PASSWORD
printf '%s' "$PHONEBOX_P12_PASSWORD" > "$pwfile"
# SHA1-3DES: the format macOS's "security import" reads (OpenSSL 3's default AES one is often refused)
openssl pkcs12 -export -inkey "$work/key.pem" -in "$work/cert.pem" -name "PhoneBox Agent" \
  -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 \
  -passout env:PHONEBOX_P12_PASSWORD -out "$p12"

echo "Certificate: $(openssl x509 -in "$work/cert.pem" -noout -subject -enddate | tr '\n' ' ')"
echo "Written: $p12"
echo "         $pwfile"

if [ "$set_secrets" = 1 ]; then
  command -v gh >/dev/null || { echo "gh (GitHub CLI) is not installed; set the secrets by hand." >&2; exit 1; }
  openssl base64 -A -in "$p12" | gh secret set MACOS_SIGNING_P12_BASE64
  printf '%s' "$PHONEBOX_P12_PASSWORD" | gh secret set MACOS_SIGNING_P12_PASSWORD
  echo "Repository secrets MACOS_SIGNING_P12_BASE64 and MACOS_SIGNING_P12_PASSWORD are set."
fi
echo "Keep both files in a password manager, then delete them."
