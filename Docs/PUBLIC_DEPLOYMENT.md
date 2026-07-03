# Public Test Grid Deployment

Step-by-step guide to deploy the Tasia test grid (ROBUST + OpenSim + MariaDB + QUIC) on a public server.

## Prerequisites on target server

- Ubuntu/Debian server with root/sudo access
- Domain name (e.g. `grid.yourdomain.com`) pointing to server IP
- Ports open in firewall: 80/tcp, 443/tcp, 8002/tcp, 8443/tcp, 9000/tcp+udp, 9001/udp, 9002/tcp
- Git, Docker, Docker Compose installed

## 1. Clone the repository

```bash
git clone https://code.is-on.click/ai-grid/igrid_server_cod.git /opt/igrid
cd /opt/igrid
git checkout tasia-rc-quic
```

## 2. Set up TLS certificates with Let's Encrypt

```bash
# Install certbot
apt-get install -y certbot

# Get wildcard or domain certificate
# For a single domain:
certbot certonly --standalone -d grid.yourdomain.com

# Convert to p12 for ROBUST:
openssl pkcs12 -export \
  -in /etc/letsencrypt/live/grid.yourdomain.com/fullchain.pem \
  -inkey /etc/letsencrypt/live/grid.yourdomain.com/privkey.pem \
  -out /opt/igrid/SSL/quic/quic-cert.p12 \
  -passout pass:your-cert-pass

# Copy PEM files for QUIC:
cp /etc/letsencrypt/live/grid.yourdomain.com/fullchain.pem /opt/igrid/SSL/quic/quic-cert.pem
cp /etc/letsencrypt/live/grid.yourdomain.com/privkey.pem /opt/igrid/SSL/quic/quic-key.pem
chmod 600 /opt/igrid/SSL/quic/quic-key.pem
```

## 3. Configure environment

Create `/opt/igrid/.env`:

```bash
cat > /opt/igrid/.env << 'EOF'
GRID_HOST=grid.yourdomain.com
DB_NAME=opensim
DB_USER=opensim
DB_PASS=your-db-password
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
HTTPS_CERT_PASS=your-cert-pass
EOF
```

## 4. Override config for public URLs

Create `/opt/igrid/docker/quic-test/config-override/OpenSim.ini`:

```ini
[Const]
BaseHostname = ${GRID_HOST}
BaseURL = https://${Const|BaseHostname}

[Network]
http_listener_port = ${REGION_PORT}
http_listener_ssl = true
http_listener_sslport = ${REGION_SSL_PORT}
http_listener_cn = ${Const|BaseHostname}
http_listener_cert_path = SSL/quic/quic-cert.p12
http_listener_cert_pass = ${HTTPS_CERT_PASS}
ExternalHostNameForLSL = ${Const|BaseHostname}

[ClientStack.Quic]
Enabled = true
Port = ${QUIC_PORT}
Alpn = opensim-ll/1
CertificatePath = SSL/quic/quic-cert.pem
PrivateKeyPath = SSL/quic/quic-key.pem
AdvertiseInLoginResponse = true
AdvertiseInEventQueue = true
LogPackets = false
AdvertiseHost = ${Const|BaseHostname}
AdvertisePort = ${QUIC_PORT}
```

## 5. Build and run

```bash
cd /opt/igrid

# Build the Docker image
docker compose -f docker-compose.quic-test.yml build opensim-all-in-one

# Run
docker compose -f docker-compose.quic-test.yml up -d opensim-all-in-one

# Watch logs
docker logs -f opensim-all-in-one
```

## 6. Verify deployment

```bash
# GridInfo
curl https://grid.yourdomain.com:8002/

# TLS validation
openssl s_client -connect grid.yourdomain.com:8002 -servername grid.yourdomain.com </dev/null 2>/dev/null | openssl x509 -noout -subject -dates

# Login check (no auth needed, just verify response)
curl -sk -X POST https://grid.yourdomain.com:8002/LLSDLogin \
  -H "Content-Type: application/json" \
  -d '{"method":"login","params":[{}]}' | head -5
```

## 7. Viewer connection

- Login URI: `https://grid.yourdomain.com:8002`
- Account: Test User / test123
- First login may take 30-60s while region initializes

## Firewall reference

```bash
ufw allow 80/tcp    # Let's Encrypt HTTP challenge
ufw allow 443/tcp   # Optional: reverse proxy
ufw allow 8002/tcp  # ROBUST HTTPS login
ufw allow 8443/tcp  # ROBUST HTTPS auxiliary
ufw allow 9000/tcp  # HTTP fallback
ufw allow 9000/udp  # LLUDP legacy
ufw allow 9001/udp  # QUIC transport
ufw allow 9002/tcp  # HTTPS CAPS
```

## Port reference

| Port | Protocol | Purpose |
|------|----------|---------|
| 8002 | TCP | ROBUST HTTPS login + GridInfo |
| 8443 | TCP | ROBUST HTTPS auxiliary |
| 9000 | TCP | HTTP fallback / CAPS |
| 9000 | UDP | LLUDP legacy simulator |
| 9001 | UDP | QUIC simulator transport |
| 9002 | TCP | HTTPS CAPS |
