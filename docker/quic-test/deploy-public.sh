#!/usr/bin/env bash
# Public test grid deployment script
# Run this on the target server as root or with sudo
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()  { echo -e "${GREEN}[INFO]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*"; exit 1; }

# ---- Config ----
DOMAIN="${DOMAIN:-}"                # e.g. grid.yourdomain.com
CERT_EMAIL="${CERT_EMAIL:-}"        # email for Let's Encrypt
DB_PASS="${DB_PASS:-opensim}"       # MariaDB password
CERT_PASS="${CERT_PASS:-}"          # PKCS12 cert password
REPO_URL="https://code.is-on.click/ai-grid/igrid_server_cod.git"
BRANCH="tasia-rc-quic"
INSTALL_DIR="${INSTALL_DIR:-/opt/igrid}"

[ -z "$DOMAIN" ] && error "Set DOMAIN=grid.yourdomain.com"
[ -z "$CERT_EMAIL" ] && error "Set CERT_EMAIL=you@email.com"
[ -z "$CERT_PASS" ] && [ -z "${CERT_PASS+}" ] && CERT_PASS="$(openssl rand -base64 18)"

# ---- 1. Install dependencies ----
info "Installing system dependencies..."
apt-get update -qq
apt-get install -y -qq git curl docker.io docker-compose-v2 certbot openssl

# ---- 2. Clone repo ----
info "Cloning $BRANCH from $REPO_URL ..."
git clone --branch "$BRANCH" "$REPO_URL" "$INSTALL_DIR"
cd "$INSTALL_DIR"

mkdir -p SSL/quic docker/quic-test/config-override

# ---- 3. Get TLS certificates ----
info "Requesting Let's Encrypt certificate for $DOMAIN ..."
certbot certonly --standalone --non-interactive --agree-tos \
  --email "$CERT_EMAIL" --domains "$DOMAIN" \
  || warn "certbot failed, check firewall (port 80 must be open)"

LETSENCRYPT_DIR="/etc/letsencrypt/live/$DOMAIN"
if [ -f "$LETSENCRYPT_DIR/fullchain.pem" ]; then
  info "Converting certificates..."
  openssl pkcs12 -export \
    -in "$LETSENCRYPT_DIR/fullchain.pem" \
    -inkey "$LETSENCRYPT_DIR/privkey.pem" \
    -out "$INSTALL_DIR/SSL/quic/quic-cert.p12" \
    -passout "pass:$CERT_PASS"
  cp "$LETSENCRYPT_DIR/fullchain.pem" "$INSTALL_DIR/SSL/quic/quic-cert.pem"
  cp "$LETSENCRYPT_DIR/privkey.pem" "$INSTALL_DIR/SSL/quic/quic-key.pem"
  chmod 600 "$INSTALL_DIR/SSL/quic/quic-key.pem"
else
  error "Let's Encrypt failed — no certificate at $LETSENCRYPT_DIR"
fi

# ---- 4. Create .env ----
info "Creating .env ..."
cat > .env <<EOF
GRID_HOST=$DOMAIN
DB_NAME=opensim
DB_USER=opensim
DB_PASS=$DB_PASS
TEST_FIRST=Test
TEST_LAST=User
TEST_PASS=test123
TEST_EMAIL=test@example.com
TEST_UUID=11111111-1111-1111-1111-111111111111
REGION_NAME=Test Region
REGION_PORT=9000
REGION_SSL_PORT=9002
QUIC_PORT=9001
PUBLIC_HTTPS_PORT=8443
HTTPS_CERT_PASS=$CERT_PASS
EOF

# ---- 5. Build and run ----
info "Building Docker image..."
docker compose -f docker-compose.quic-test.yml build opensim-all-in-one

info "Starting container..."
docker compose -f docker-compose.quic-test.yml up -d opensim-all-in-one

# ---- 6. Verify ----
info "Waiting 30s for startup..."
sleep 30

echo ""
info "=== DEPLOYMENT COMPLETE ==="
echo "Domain:     https://$DOMAIN:8002"
echo "Login URI:  https://$DOMAIN:8002"
echo "Account:    Test User / test123"
echo ""
echo "Verify with:"
echo "  curl https://$DOMAIN:8002/"
echo "  openssl s_client -connect $DOMAIN:8002 -servername $DOMAIN"
echo ""
echo "View logs:  docker logs -f opensim-all-in-one"
