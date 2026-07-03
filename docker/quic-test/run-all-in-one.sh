#!/usr/bin/env bash
set -euo pipefail

cd /src

GRID_HOST="${GRID_HOST:-127.0.0.1}"
DB_NAME="${DB_NAME:-opensim}"
DB_USER="${DB_USER:-opensim}"
DB_PASS="${DB_PASS:-opensim}"
TEST_FIRST="${TEST_FIRST:-Test}"
TEST_LAST="${TEST_LAST:-User}"
TEST_PASS="${TEST_PASS:-test123}"
TEST_EMAIL="${TEST_EMAIL:-test@example.com}"
TEST_UUID="${TEST_UUID:-11111111-1111-1111-1111-111111111111}"
REGION_NAME="${REGION_NAME:-Test Region}"
REGION_UUID="${REGION_UUID:-11111111-2222-3333-4444-555555555555}"
REGION_X="${REGION_X:-1000}"
REGION_Y="${REGION_Y:-1000}"
REGION_PORT="${REGION_PORT:-9000}"
REGION_SSL_PORT="${REGION_SSL_PORT:-9002}"
QUIC_PORT="${QUIC_PORT:-9001}"
QUIC_ADVERTISE_LOGIN="${QUIC_ADVERTISE_LOGIN:-true}"
PUBLIC_HTTPS_PORT="${PUBLIC_HTTPS_PORT:-8443}"
HTTPS_CERT_PASS="${HTTPS_CERT_PASS:-opensim-quic-test}"
CONFIGURATION="${CONFIGURATION:-Release}"
FORCE_CONFIG="${FORCE_CONFIG:-0}"
INIT_USER="${INIT_USER:-1}"

echo "[all-in-one] GRID_HOST=${GRID_HOST}"
echo "[all-in-one] DB=${DB_NAME} user=${DB_USER}"

start_mysql() {
  mkdir -p /run/mysqld
  chown mysql:mysql /run/mysqld /var/lib/mysql

  if [ ! -d /var/lib/mysql/mysql ]; then
    echo "[all-in-one] Initializing MariaDB datadir"
    mariadb-install-db --user=mysql --datadir=/var/lib/mysql >/dev/null
  fi

  echo "[all-in-one] Starting MariaDB"
  mariadbd --user=mysql --datadir=/var/lib/mysql --bind-address=127.0.0.1 --port=3306 &
  MYSQL_PID=$!

  for i in $(seq 1 60); do
    if mysqladmin ping -uroot --silent >/dev/null 2>&1; then
      echo "[all-in-one] MariaDB ready"
      return 0
    fi
    sleep 1
  done

  echo "[all-in-one] MariaDB failed to start"
  exit 1
}

setup_database() {
  mysql -uroot <<SQL
CREATE DATABASE IF NOT EXISTS \`${DB_NAME}\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS '${DB_USER}'@'localhost' IDENTIFIED BY '${DB_PASS}';
GRANT ALL PRIVILEGES ON \`${DB_NAME}\`.* TO '${DB_USER}'@'localhost';
FLUSH PRIVILEGES;
SQL
}

generate_quic_cert() {
  mkdir -p bin/SSL/quic

  local cert_source=""
  local key_source=""
  local provided_cert=0

  # Prefer Let's Encrypt certificates if installed on this host
  local le_path="/etc/letsencrypt/live/${GRID_HOST}"
  if [ -f "${le_path}/fullchain.pem" ] && [ -f "${le_path}/privkey.pem" ]; then
    echo "[all-in-one] Using Let's Encrypt certificate for ${GRID_HOST}"
    cert_source="${le_path}/fullchain.pem"
    key_source="${le_path}/privkey.pem"
    provided_cert=1
  elif [ -f "docker/quic-test/certs/quic-cert.pem" ] && [ -f "docker/quic-test/certs/quic-key.pem" ]; then
    echo "[all-in-one] Using provided QUIC TLS certificate from docker/quic-test/certs/"
    cert_source="docker/quic-test/certs/quic-cert.pem"
    key_source="docker/quic-test/certs/quic-key.pem"
    provided_cert=1
  fi

  if [ "$provided_cert" = "1" ]; then
    cp "$cert_source" bin/SSL/quic/quic-cert.pem
    cp "$key_source" bin/SSL/quic/quic-key.pem
  fi

  if [ "$provided_cert" = "0" ] && { [ "$FORCE_CONFIG" = "1" ] || [ ! -f bin/SSL/quic/quic-cert.pem ] || [ ! -f bin/SSL/quic/quic-key.pem ]; }; then
    echo "[all-in-one] Generating self-signed QUIC certificate"
    echo "[all-in-one] For trusted local testing, install mkcert on the host and place certs in docker/quic-test/certs/"
    openssl req -x509 -newkey rsa:2048 -nodes \
      -keyout bin/SSL/quic/quic-key.pem \
      -out bin/SSL/quic/quic-cert.pem \
      -days 3650 \
      -subj "/CN=${GRID_HOST}" \
      -addext "subjectAltName=DNS:localhost,DNS:${GRID_HOST},IP:127.0.0.1" >/dev/null 2>&1
  fi

  echo "[all-in-one] Creating PKCS#12 certificate for HTTPS listeners"
  openssl pkcs12 -export \
    -in bin/SSL/quic/quic-cert.pem \
    -inkey bin/SSL/quic/quic-key.pem \
    -out bin/SSL/quic/quic-cert.p12 \
    -passout "pass:${HTTPS_CERT_PASS}" >/dev/null 2>&1
}

write_configs() {
  mkdir -p bin/config-include bin/Regions

  if [ "$FORCE_CONFIG" = "1" ] || [ ! -f bin/Robust.ini ]; then
    echo "[all-in-one] Writing bin/Robust.ini"
    cp bin/Robust.ini.example bin/Robust.ini
    sed -i \
      -e "/^\[Startup\]/a console = basic" \
      -e "s/BaseHostname = \"127.0.0.1\"/BaseHostname = \"${GRID_HOST}\"/" \
      -e 's#BaseURL = "http://${Const|BaseHostname}"#BaseURL = "https://${Const|BaseHostname}"#' \
      -e "s/ConnectionString = \"Data Source=localhost;Database=opensim;User ID=opensim;Password=\*\*\*\*\*;Old Guids=true;SslMode=None;\"/ConnectionString = \"Data Source=localhost;Database=${DB_NAME};User ID=${DB_USER};Password=${DB_PASS};Old Guids=true;SslMode=None;\"/" \
      -e "s/EnableRobustSelfsignedCertSupport = false/EnableRobustSelfsignedCertSupport = true/" \
      -e "s/RobustCertHostName = .*/RobustCertHostName = ${GRID_HOST}/" \
      -e "s/RobustCertHostIp = \"127.0.0.1\"/RobustCertHostIp = \"127.0.0.1\"/" \
      bin/Robust.ini
    sed -i "/^\[Network\]/,/^\[/ {
      s/^    port = .*/    port = \${Const|PublicPort}/
      s/^    ; https_main = False/    https_main = True/
      s/^    ; https_listener = False/    https_listener = True/
      s/^    ; https_port = 0/    https_port = ${PUBLIC_HTTPS_PORT}/
      s|^    ; cert_path = .*|    cert_path = \"SSL/quic/quic-cert.p12\"|
      s/^    ; cert_pass = .*/    cert_pass = \"${HTTPS_CERT_PASS}\"/
    }" bin/Robust.ini
    local region_key
    region_key="Region_${REGION_NAME// /_}"
    sed -i "/^\[GridService\]/a\    ${region_key} = \"DefaultRegion, FallbackRegion\"" bin/Robust.ini
    cat >> bin/Robust.ini <<EOF

[ClientStack.Quic]
Enabled = true
AdvertiseInLoginResponse = ${QUIC_ADVERTISE_LOGIN}
AdvertiseHost = "${GRID_HOST}"
AdvertisePort = ${QUIC_PORT}
Port = ${QUIC_PORT}
EOF

    # === Hypergrid additions to Robust.ini ===
    # Add HG service connectors to ServiceList (after MuteListConnector)
    sed -i '/^    MuteListConnector/a\    ;; Additions for Hypergrid\n    GatekeeperServiceInConnector = "${Const|PublicPort}/OpenSim.Server.Handlers.dll:GatekeeperServiceInConnector"\n    UserAgentServerConnector = "${Const|PublicPort}/OpenSim.Server.Handlers.dll:UserAgentServerConnector"\n    HeloServiceInConnector = "${Const|PublicPort}/OpenSim.Server.Handlers.dll:HeloServiceInConnector"\n    HGFriendsServerConnector = "${Const|PublicPort}/OpenSim.Server.Handlers.dll:HGFriendsServerConnector"\n    InstantMessageServerConnector = "${Const|PublicPort}/OpenSim.Server.Handlers.dll:InstantMessageServerConnector"\n    HGInventoryServiceConnector = "HGInventoryService@${Const|PublicPort}/OpenSim.Server.Handlers.dll:XInventoryInConnector"\n    HGAssetServiceConnector = "HGAssetService@${Const|PublicPort}/OpenSim.Server.Handlers.dll:AssetServiceConnector"' bin/Robust.ini

    # Add HypergridLinker and GatekeeperURI to [GridService]
    sed -i '/^\[GridService\]/a\    HypergridLinker = true\n    GatekeeperURI = "${Const|BaseURL}:${Const|PublicPort}"' bin/Robust.ini

    # Enable Groups V2 in ServiceList (uncomment the GroupsServiceConnector)
    sed -i 's/^    ; GroupsServiceConnector = /    GroupsServiceConnector = /' bin/Robust.ini
    # Enable OfflineIM V2 in ServiceList
    sed -i 's/^    ; OfflineIMServiceConnector = /    OfflineIMServiceConnector = /' bin/Robust.ini
    # Enable UserProfiles service in ServiceList (use PrivatePort for internal)
    sed -i 's/^    ; UserProfilesServiceConnector = .*/    UserProfilesServiceConnector = "${Const|PrivatePort}\/OpenSim.Server.Handlers.dll:UserProfilesConnector"/' bin/Robust.ini
    # Enable UserProfilesService handlers (must be true for the connector to register JSON-RPC handlers)
    sed -i '/^\[UserProfilesService\]/,/^\[/ s/^    Enabled = false/    Enabled = true/' bin/Robust.ini

    # Add HG refs to [LoginService]
    sed -i '/^\[LoginService\]/a\    UserAgentService = "OpenSim.Services.HypergridService.dll:UserAgentService"\n    HGInventoryServicePlugin = "HGInventoryService@OpenSim.Services.HypergridService.dll:HGSuitcaseInventoryService"' bin/Robust.ini

    cat >> bin/Robust.ini <<'INIEOF'

[Hypergrid]
    HomeURI = "${Const|BaseURL}:${Const|PublicPort}"
    GatekeeperURI = "${Const|BaseURL}:${Const|PublicPort}"

[GatekeeperService]
    LocalServiceModule = "OpenSim.Services.HypergridService.dll:GatekeeperService"
    UserAccountService = "OpenSim.Services.UserAccountService.dll:UserAccountService"
    UserAgentService = "OpenSim.Services.HypergridService.dll:UserAgentService"
    PresenceService = "OpenSim.Services.PresenceService.dll:PresenceService"
    GridUserService = "OpenSim.Services.UserAccountService.dll:GridUserService"
    GridService = "OpenSim.Services.GridService.dll:GridService"
    AuthenticationService = "OpenSim.Services.Connectors.dll:AuthenticationServicesConnector"
    SimulationService ="OpenSim.Services.Connectors.dll:SimulationServiceConnector"
    ExternalName = "${Const|BaseURL}:${Const|PublicPort}"
    AllowTeleportsToAnyRegion = true
    ForeignAgentsAllowed = true

[UserAgentService]
    LocalServiceModule = "OpenSim.Services.HypergridService.dll:UserAgentService"
    GridUserService     = "OpenSim.Services.UserAccountService.dll:GridUserService"
    GridService         = "OpenSim.Services.GridService.dll:GridService"
    GatekeeperService   = "OpenSim.Services.HypergridService.dll:GatekeeperService"
    PresenceService     = "OpenSim.Services.PresenceService.dll:PresenceService"
    FriendsService      = "OpenSim.Services.FriendsService.dll:FriendsService"
    UserAccountService  = "OpenSim.Services.UserAccountService.dll:UserAccountService"

[HGInventoryService]
    LocalServiceModule    = "OpenSim.Services.HypergridService.dll:HGInventoryService"
    UserAccountsService = "OpenSim.Services.UserAccountService.dll:UserAccountService"
    AvatarService = "OpenSim.Services.AvatarService.dll:AvatarService"
    AuthType = None
    HomeURI = "${Const|BaseURL}:${Const|PublicPort}"

[HGAssetService]
    LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGAssetService"
    UserAccountsService = "OpenSim.Services.UserAccountService.dll:UserAccountService"
    HomeURI = "${Const|BaseURL}:${Const|PublicPort}"
    BackingService = "OpenSim.Services.AssetService.dll:AssetService"

[HGFriendsService]
    LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGFriendsService"
    UserAgentService = "OpenSim.Services.HypergridService.dll:UserAgentService"
    FriendsService = "OpenSim.Services.FriendsService.dll:FriendsService"
    UserAccountService = "OpenSim.Services.UserAccountService.dll:UserAccountService"
    GridService = "OpenSim.Services.GridService.dll:GridService"
    PresenceService = "OpenSim.Services.PresenceService.dll:PresenceService"

[HGInstantMessageService]
    LocalServiceModule  = "OpenSim.Services.HypergridService.dll:HGInstantMessageService"
    GridService         = "OpenSim.Services.GridService.dll:GridService"
    PresenceService     = "OpenSim.Services.PresenceService.dll:PresenceService"
    UserAgentService    = "OpenSim.Services.HypergridService.dll:UserAgentService"
    InGatekeeper = True

INIEOF
  fi

  if [ "$FORCE_CONFIG" = "1" ] || [ ! -f bin/OpenSim.ini ]; then
    if [ -f Grid_Welcome/Opensim.ini ]; then
      echo "[all-in-one] Adapting production Grid_Welcome config for bin/OpenSim.ini"
      cp Grid_Welcome/Opensim.ini bin/OpenSim.ini
      # Domain: i.let-us.cyou -> GRID_HOST
      sed -i \
        -e "s/i\\.let-us\\.cyou/${GRID_HOST}/g" \
        -e "s/CHANGE_ME_HOST/${GRID_HOST}/g" \
        bin/OpenSim.ini
      # Port: adapt internal listener port from 2001 to REGION_PORT
      sed -i \
        -e "s/http_listener_port = .*/http_listener_port = ${REGION_PORT}/" \
        -e "s/console_port = .*/console_port = ${REGION_PORT}/" \
        bin/OpenSim.ini
      # Add SSL/HTTPS listener settings after [Network]
      sed -i "/^\[Network\]/a\\
http_listener_ssl = true\\
http_listener_sslport = ${REGION_SSL_PORT}\\
http_listener_cn = \"${GRID_HOST}\"\\
http_listener_cert_path = \"SSL/quic/quic-cert.p12\"\\
http_listener_cert_pass = \"${HTTPS_CERT_PASS}\"" \
        bin/OpenSim.ini
      # Fix PrivURL to use localhost (all-in-one has ROBUST locally, not on public host)
      sed -i 's|^\s*PrivURL =.*|    PrivURL = "http://127.0.0.1"|' bin/OpenSim.ini
      # Add console = rest so OpenSim doesn't need stdin (container runs without TTY)
      sed -i '/^\[Startup\]/a\    console = rest' bin/OpenSim.ini
      # Fix console user/pass
      sed -i \
        -e "s/ConsoleUser = .*/ConsoleUser = \"Console User\"/" \
        -e "s/ConsolePass = .*/ConsolePass = \"${TEST_PASS}\"/" \
        bin/OpenSim.ini
      # Replace [Architecture] include to use GridQuicTest.ini (which includes QUIC service routing)
      sed -i \
        -e 's|Include-Architecture = .*|Include-Architecture = "config-include/GridQuicTest.ini"|' \
        bin/OpenSim.ini
      # Remove Economy currency server reference (uses port 1026 which we don't run)
      sed -i '/CurrencyServer = /d' bin/OpenSim.ini
      # Ensure estate settings are set (production config has them all commented out)
      cat >> bin/OpenSim.ini <<EOF

[Estates]
DefaultEstateName = "Test Estate"
DefaultEstateOwnerName = "${TEST_FIRST} ${TEST_LAST}"
DefaultEstateOwnerUUID = "${TEST_UUID}"
DefaultEstateOwnerEMail = "${TEST_EMAIL}"
DefaultEstateOwnerPassword = "${TEST_PASS}"
EOF
      # Append QUIC client stack section
      cat >> bin/OpenSim.ini <<EOF

[ClientStack.Quic]
Enabled = true
Port = ${QUIC_PORT}
AdvertiseInEventQueue = true
AdvertiseInLoginResponse = ${QUIC_ADVERTISE_LOGIN}
AdvertiseHost = "${GRID_HOST}"
AdvertisePort = ${QUIC_PORT}
ALPN = opensim-ll/1
CertificatePath = "SSL/quic/quic-cert.pem"
PrivateKeyPath = "SSL/quic/quic-key.pem"
EOF
    else
      echo "[all-in-one] Grid_Welcome config not found, generating minimal OpenSim.ini"
      cat > bin/OpenSim.ini <<EOF
[Const]
BaseHostname = "${GRID_HOST}"
BaseURL = "https://\${Const|BaseHostname}"
PrivURL = "http://127.0.0.1"
PublicPort = "8002"
PrivatePort = "8003"

[Startup]
console = rest
ConsolePrompt = "Region (\\R) "
ConsoleHistoryFileEnabled = true
ConsoleHistoryFile = "OpenSimConsoleHistory.txt"
region_info_source = "filesystem"
regionload_regionsdir = "Regions"
allow_regionless = false
physics = BulletSim
meshing = Meshmerizer

[Network]
http_listener_port = ${REGION_PORT}
http_listener_ssl = true
http_listener_sslport = ${REGION_SSL_PORT}
http_listener_cn = "${GRID_HOST}"
http_listener_cert_path = "SSL/quic/quic-cert.p12"
http_listener_cert_pass = "${HTTPS_CERT_PASS}"
ExternalHostNameForLSL = \${Const|BaseHostname}

[Estates]
DefaultEstateName = "Test Estate"
DefaultEstateOwnerName = "${TEST_FIRST} ${TEST_LAST}"
DefaultEstateOwnerUUID = "${TEST_UUID}"
DefaultEstateOwnerEMail = "${TEST_EMAIL}"
DefaultEstateOwnerPassword = "${TEST_PASS}"

[ClientStack.Quic]
Enabled = true
Port = ${QUIC_PORT}
AdvertiseInEventQueue = true
AdvertiseInLoginResponse = ${QUIC_ADVERTISE_LOGIN}
AdvertiseHost = "${GRID_HOST}"
AdvertisePort = ${QUIC_PORT}
ALPN = opensim-ll/1
CertificatePath = "SSL/quic/quic-cert.pem"
PrivateKeyPath = "SSL/quic/quic-key.pem"

[Architecture]
Include-Architecture = "config-include/GridQuicTest.ini"
EOF

      # Append profile module + addon modules config
      cat >> bin/OpenSim.ini <<'OSINI'

[ClientStack.LindenCaps]
    Cap_AttachmentResources = "localhost"
    Cap_CopyInventoryFromNotecard = "localhost"
    Cap_EstateAccess = "localhost"
    Cap_EnvironmentSettings = "localhost"
    Cap_EventQueueGet = "localhost"
    Cap_FetchInventory = "localhost"
    Cap_FetchLib = "localhost"
    Cap_FetchLibDescendents = "localhost"
    Cap_GetDisplayNames = "localhost"
    Cap_GetTexture = "localhost"
    Cap_GetMesh = "localhost"
    Cap_GetMesh2 = "localhost"
    Cap_GetAsset = "localhost"
    Cap_GroupMemberData = "localhost"
    Cap_HomeLocation = "localhost"
    Cap_LandResources = "localhost"
    Cap_MapLayer = "localhost"
    Cap_NewFileAgentInventory = "localhost"
    Cap_ObjectAdd = "localhost"
    Cap_ParcelPropertiesUpdate = "localhost"
    Cap_RemoteParcelRequest = "localhost"
    Cap_ServerReleaseNotes = "localhost"
    Cap_UpdateNotecardAgentInventory = "localhost"
    Cap_UpdateScriptAgent = "localhost"
    Cap_UpdateNotecardTaskInventory = "localhost"
    Cap_UpdateScriptTask = "localhost"
    Cap_UploadBakedTexture = "localhost"
    Cap_UploadObjectAsset = "localhost"
    Cap_WebFetchInventoryDescendents = "localhost"
    Cap_FetchInventoryDescendents2 = "localhost"
    Cap_FetchInventory2 = "localhost"
    Cap_FetchLib2 = "localhost"
    Cap_FetchLibDescendents2 = "localhost"
    Cap_AvatarPickerSearch = "localhost"

[Modules]
Setup_UserProfileModule = OpenSim.Region.CoreModules.Avatar.UserProfiles.UserProfileModule
Setup_RestartModule = TasiaAddons.RestartModule.RestartModule
Setup_ChatAuditModule = TasiaAddons.ChatAudit.ChatAuditModule
Setup_MarketplaceRegionModule = TasiaAddons.Marketplace.MarketplaceRegionModule
Setup_FriendConference = TasiaAddons.FriendConference.FriendConferenceModule
Setup_MacAuditRegionModule = TasiaAddons.MACAudit.MacAuditRegionModule

[UserProfiles]
    ProfileServiceURL = "${Const|PrivURL}:${Const|PrivatePort}/"

[NGC.Welcome]
    Enable = true
    FallbackMessage = Welcome to I-Grid Test, <USERNAME>!

[RestartModule]
    ApiEnabled = false
    DefaultDelaySeconds = 30

[ChatAudit]
    Enabled = false
    LogFile = Data/chat-audit.log

[FriendConference]
    Enabled = true
    RequireFriendship = false
    MaxParticipants = 10
    SessionIdleSeconds = 3600

[Marketplace]
    Enabled = true
    Password = test-marketplace-key

[SharedInventory]
    Enabled = false

[Groups]
    Enabled = true
    Module = "Groups Module V2"
    ServicesConnectorModule = "Groups Remote Service Connector"
    MessagingModule = "Groups Messaging Module"
    GroupsServerURI = "${Const|PrivURL}:${Const|PrivatePort}/"
    ; Groups module V2 - uses Groups DB tables (created automatically)

OSINI
    fi
  fi

  if [ "$FORCE_CONFIG" = "1" ] || [ ! -f bin/config-include/GridQuicTest.ini ]; then
    cp bin/config-include/Grid.ini bin/config-include/GridQuicTest.ini
    sed -i '/ExperienceServices[[:space:]]*=/d' bin/config-include/GridQuicTest.ini
    # Replace standard modules with Hypergrid variants
    sed -i 's/EntityTransferModule    = "BasicEntityTransferModule"/EntityTransferModule    = "HGEntityTransferModule"/' bin/config-include/GridQuicTest.ini
    sed -i 's/InventoryAccessModule   = "BasicInventoryAccessModule"/InventoryAccessModule   = "HGInventoryAccessModule"/' bin/config-include/GridQuicTest.ini
    sed -i '/^\[Modules\]/a\    FriendsModule           = "HGFriendsModule"\n    UserManagementModule    = "HGUserManagementModule"' bin/config-include/GridQuicTest.ini
    # Add message transfer modules
    sed -i '/^\[Modules\]/a\    MessageTransferModule   = "HGMessageTransferModule"\n    LureModule              = "HGLureModule"' bin/config-include/GridQuicTest.ini
    # Add WorldMap for Hypergrid
    sed -i '/^\[Startup\]/a\    WorldMapModule = "HGWorldMap"' bin/config-include/GridQuicTest.ini
    # Fix: HGMessageTransferModule reads from [Messaging] section, not [Modules]
    # Without this, HG message transfer (needed for object transfer between grids) is disabled
    if ! grep -q '^\[Messaging\]' bin/config-include/GridQuicTest.ini; then
      echo -e '\n[Messaging]\n    MessageTransferModule = HGMessageTransferModule' >> bin/config-include/GridQuicTest.ini
    fi
  fi

  if [ "$FORCE_CONFIG" = "1" ] || [ ! -f bin/config-include/GridCommon.ini ]; then
    echo "[all-in-one] Writing bin/config-include/GridCommon.ini"
    cat > bin/config-include/GridCommon.ini <<EOF
[DatabaseService]
StorageProvider = "OpenSim.Data.MySQL.dll"
ConnectionString = "Data Source=localhost;Database=${DB_NAME};User ID=${DB_USER};Password=${DB_PASS};Old Guids=true;SslMode=None;"

[Hypergrid]
HomeURI = "\${Const|BaseURL}:\${Const|PublicPort}"
GatekeeperURI = "\${Const|BaseURL}:\${Const|PublicPort}"

[Modules]
AssetCaching = "FlotsamAssetCache"
Include-FlotsamCache = "config-include/FlotsamCache.ini"

[AssetService]
DefaultAssetLoader = "OpenSim.Framework.AssetLoader.Filesystem.dll"
AssetLoaderArgs = "assets/AssetSets.xml"
AssetServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"
HypergridAssetService = "OpenSim.Services.Connectors.dll:HGAssetServiceConnector"

[InventoryService]
InventoryServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[GridInfo]
GridInfoURI = "\${Const|BaseURL}:\${Const|PublicPort}"

[GridService]
GridServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"
Gatekeeper = "\${Const|BaseURL}:\${Const|PublicPort}"
HypergridLinker = true
AllowHypergridMapSearch = true

[EstateService]
EstateServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[Messaging]
Gatekeeper = "\${Const|BaseURL}:\${Const|PublicPort}"

[AvatarService]
AvatarServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[AgentPreferencesService]
AgentPreferencesServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[PresenceService]
PresenceServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[UserAccountService]
UserAccountServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[UserAliasService]
UserAliasServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[GridUserService]
GridUserServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[AuthenticationService]
AuthenticationServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[FriendsService]
FriendsServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[MuteListService]
MuteListServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[MapImageService]
MapImageServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[SimulationService]
SimulationServerURI = "\${Const|PrivURL}:\${Const|PrivatePort}"

[LibraryService]
LibraryName = "OpenSim Library"
DefaultLibrary = "./inventory/Libraries.xml"

[HGInventoryAccessModule]
RestrictInventoryAccessAbroad = False

[HGFriendsModule]
LevelHGFriends = 0

[HGInstantMessageService]
LocalServiceModule = "OpenSim.Services.HypergridService.dll:HGInstantMessageService"
GridService = "OpenSim.Services.Connectors.dll:GridServicesConnector"
PresenceService = "OpenSim.Services.Connectors.dll:PresenceServicesConnector"
UserAgentService = "OpenSim.Services.Connectors.dll:UserAgentServiceConnector"
EOF
  fi

  # Ensure FlotsamCache.ini exists (needed by AssetCaching = FlotsamAssetCache)
  if [ ! -f bin/config-include/FlotsamCache.ini ] && [ -f bin/config-include/FlotsamCache.ini.example ]; then
    cp bin/config-include/FlotsamCache.ini.example bin/config-include/FlotsamCache.ini
    echo "[all-in-one] Created FlotsamCache.ini from example"
  fi

  if [ "$FORCE_CONFIG" = "1" ] || [ ! -f bin/Regions/Regions.ini ]; then
    if [ -f Grid_Welcome/Region/grid_welcome.ini ]; then
      echo "[all-in-one] Adapting production region config for bin/Regions"
      local region_dir="bin/Regions/Grid_Welcome/Region"
      mkdir -p "${region_dir}"
      cp Grid_Welcome/Region/grid_welcome.ini "${region_dir}/grid_welcome.ini"
      # Domain substitution
      sed -i "s/i\\.let-us\\.cyou/${GRID_HOST}/g" "${region_dir}/grid_welcome.ini"
      # Port: adapt from 2001 to REGION_PORT
      sed -i "s/InternalPort = .*/InternalPort = ${REGION_PORT}/" "${region_dir}/grid_welcome.ini"
      # UUID: use REGION_UUID
      sed -i "s/RegionUUID=.*/RegionUUID=${REGION_UUID}/" "${region_dir}/grid_welcome.ini"
      # Location: use REGION_X,REGION_Y
      sed -i "s/Location = .*/Location = ${REGION_X},${REGION_Y}/" "${region_dir}/grid_welcome.ini"
      # Region name: change section header from [Grid_Welcome] to [${REGION_NAME}]
      sed -i "s/^\[Grid_Welcome\]/[${REGION_NAME}]/" "${region_dir}/grid_welcome.ini"
      # Add region type and estate for test grid
      echo "RegionType = DefaultRegion, FallbackRegion" >> "${region_dir}/grid_welcome.ini"
    else
      echo "[all-in-one] Production region config not found, generating minimal Regions.ini"
      cat > bin/Regions/Regions.ini <<EOF
[${REGION_NAME}]
RegionUUID = ${REGION_UUID}
Location = ${REGION_X},${REGION_Y}
InternalAddress = 0.0.0.0
InternalPort = ${REGION_PORT}
AllowAlternatePorts = False
ExternalHostName = "${GRID_HOST}"
RegionType = DefaultRegion, FallbackRegion
TargetEstate = "Test Estate"
EOF
    fi
  fi

  # Copy region-specific config-include files from deployment examples
  if [ -d docs/deployment-examples/config-include/regions ]; then
    mkdir -p bin/config-include/regions
    cp docs/deployment-examples/config-include/regions/*.ini bin/config-include/regions/ 2>/dev/null || true
    # Substitute placeholder hosts in the include files
    sed -i "s/CHANGE_ME_HOST/${GRID_HOST}/g" bin/config-include/regions/*.ini 2>/dev/null || true
    echo "[all-in-one] Copied $(ls bin/config-include/regions/*.ini 2>/dev/null | wc -l) region include files"
  else
    echo "[all-in-one] Warning: docs/deployment-examples/config-include/regions/ not found"
  fi
}

build_binaries() {
  echo "[all-in-one] Building Linden UDP/QUIC client stack"
  dotnet build OpenSim/Region/ClientStack/Linden/UDP/OpenSim.Region.ClientStack.LindenUDP.csproj -c "${CONFIGURATION}"

  if [ ! -f "build/${CONFIGURATION}/OpenSim.Services.AssetService.dll" ] || [ ! -f "build/${CONFIGURATION}/OpenSim.Services.InventoryService.dll" ]; then
    echo "[all-in-one] Building ROBUST service plugins"
    local service_projects=(
      OpenSim/Services/AssetService/OpenSim.Services.AssetService.csproj
      OpenSim/Services/InventoryService/OpenSim.Services.InventoryService.csproj
      OpenSim/Services/GridService/OpenSim.Services.GridService.csproj
      OpenSim/Services/AuthenticationService/OpenSim.Services.AuthenticationService.csproj
      OpenSim/Services/AvatarService/OpenSim.Services.AvatarService.csproj
      OpenSim/Services/LLLoginService/OpenSim.Services.LLLoginService.csproj
      OpenSim/Services/PresenceService/OpenSim.Services.PresenceService.csproj
      OpenSim/Services/EstateService/OpenSim.Services.EstateService.csproj
      OpenSim/Services/Friends/OpenSim.Services.FriendsService.csproj
      OpenSim/Services/MapImageService/OpenSim.Services.MapImageService.csproj
      OpenSim/Services/MuteListService/OpenSim.Services.MuteListService.csproj
      OpenSim/Services/SimulationService/OpenSim.Services.SimulationService.csproj
      OpenSim/Services/UserProfilesService/OpenSim.Services.UserProfilesService.csproj
    )
    for project in "${service_projects[@]}"; do
      dotnet build "$project" -c "${CONFIGURATION}"
    done
  fi
  if [ ! -f "build/${CONFIGURATION}/Robust.dll" ]; then
    echo "[all-in-one] Building ROBUST"
    dotnet build OpenSim/Server/Robust.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.dll" ]; then
    echo "[all-in-one] Building OpenSim"
    dotnet build OpenSim/Region/Application/OpenSim.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Data.Null.dll" ]; then
    echo "[all-in-one] Building OpenSim.Data provider modules"
    dotnet build OpenSim/Data/Null/OpenSim.Data.Null.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.ApplicationPlugins.LoadRegions.dll" ]; then
    echo "[all-in-one] Building LoadRegions plugin"
    dotnet build OpenSim/ApplicationPlugins/LoadRegions/OpenSim.ApplicationPlugins.LoadRegions.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.ApplicationPlugins.RegionModulesController.dll" ]; then
    echo "[all-in-one] Building RegionModulesController plugin"
    dotnet build OpenSim/ApplicationPlugins/RegionModulesController/OpenSim.ApplicationPlugins.RegionModulesController.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Region.OptionalModules.dll" ]; then
    echo "[all-in-one] Building optional region modules (PrimLimitsModule etc.)"
    dotnet build OpenSim/Region/OptionalModules/OpenSim.Region.OptionalModules.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Framework.AssetLoader.Filesystem.dll" ]; then
    echo "[all-in-one] Building AssetLoader.Filesystem"
    dotnet build OpenSim/Framework/AssetLoader/Filesystem/OpenSim.Framework.AssetLoader.Filesystem.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Region.ClientStack.LindenCaps.dll" ]; then
    echo "[all-in-one] Building Linden CAPS handlers (BunchOfCaps etc.)"
    dotnet build OpenSim/Region/ClientStack/Linden/Caps/OpenSim.Region.ClientStack.LindenCaps.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Capabilities.Handlers.dll" ]; then
    echo "[all-in-one] Building Capabilities Handlers"
    dotnet build OpenSim/Capabilities/Handlers/OpenSim.Capabilities.Handlers.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Region.PhysicsModule.BulletS.dll" ]; then
    echo "[all-in-one] Building BulletSim physics module"
    dotnet build OpenSim/Region/PhysicsModules/BulletS/OpenSim.Region.PhysicsModule.BulletS.csproj -c "${CONFIGURATION}"
  fi

  # === Tasia Addons ===
  local tasia_addon_projects=(
    addon-modules/TasiaAddons.Abstractions/TasiaAddons.Abstractions.csproj
    addon-modules/TasiaAddons.AbuseReports/TasiaAddons.AbuseReports.csproj
    addon-modules/TasiaAddons.AccessLogger/TasiaAddons.AccessLogger.csproj
    addon-modules/TasiaAddons.AlertNotifications/TasiaAddons.AlertNotifications.csproj
    addon-modules/TasiaAddons.ChatAudit/TasiaAddons.ChatAudit.csproj
    addon-modules/TasiaAddons.FriendConference/TasiaAddons.FriendConference.csproj
    addon-modules/TasiaAddons.LoginSecurity/TasiaAddons.LoginSecurity.csproj
    addon-modules/TasiaAddons.MACAudit/TasiaAddons.MACAudit.csproj
    addon-modules/TasiaAddons.Marketplace/TasiaAddons.Marketplace.csproj
    addon-modules/TasiaAddons.RemoteSound/TasiaAddons.RemoteSound.csproj
    addon-modules/TasiaAddons.RestartModule/TasiaAddons.RestartModule.csproj
    addon-modules/TasiaAddons.SharedInventory/TasiaAddons.SharedInventory.csproj
    addon-modules/TasiaAddons.WoWonder/TasiaAddons.WoWonder.csproj
  )
  for project in "${tasia_addon_projects[@]}"; do
    local dll_name
    dll_name="$(basename "$(dirname "$project")").dll"
    # OutputPath includes $(AssemblyName)/ subdirectory, so check both locations
    if [ ! -f "build/${CONFIGURATION}/${dll_name}" ] && [ ! -f "build/${CONFIGURATION}/$(basename "$(dirname "$project")")/${dll_name}" ]; then
      echo "[all-in-one] Building ${dll_name}"
      dotnet build "$project" -c "${CONFIGURATION}"
    fi
  done

  # === Tasia Extensions ===
  local tasia_ext_projects=(
    Source/Tasia.Extensions.Host.Robust/Tasia.Extensions.Host.Robust.csproj
    Source/Tasia.Extensions.Host.Sim/Tasia.Extensions.Host.Sim.csproj
    Source/Tasia.Extensions.Loader/Tasia.Extensions.Loader.csproj
    Source/Tasia.Extensions.SDK/Tasia.Extensions.SDK.csproj
  )
  for project in "${tasia_ext_projects[@]}"; do
    local dll_name
    dll_name="$(basename "$(dirname "$project")").dll"
    if [ ! -f "build/${CONFIGURATION}/${dll_name}" ]; then
      echo "[all-in-one] Building ${dll_name}"
      dotnet build "$project" -c "${CONFIGURATION}"
    fi
  done

  # === OpenSim Addons (Groups, OfflineIM) ===
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Addons.Groups.dll" ]; then
    echo "[all-in-one] Building OpenSim.Addons.Groups"
    dotnet build OpenSim/Addons/Groups/OpenSim.Addons.Groups.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Addons.OfflineIM.dll" ]; then
    echo "[all-in-one] Building OpenSim.Addons.OfflineIM"
    dotnet build OpenSim/Addons/OfflineIM/OpenSim.Addons.OfflineIM.csproj -c "${CONFIGURATION}"
  fi

  # === RemoteController plugin ===
  if [ ! -f "build/${CONFIGURATION}/OpenSim.ApplicationPlugins.RemoteController.dll" ]; then
    echo "[all-in-one] Building RemoteController plugin"
    dotnet build OpenSim/ApplicationPlugins/RemoteController/OpenSim.ApplicationPlugins.RemoteController.csproj -c "${CONFIGURATION}"
  fi

  # === Console Client ===
  if [ ! -f "build/${CONFIGURATION}/OpenSim.ConsoleClient.dll" ]; then
    echo "[all-in-one] Building OpenSim.ConsoleClient"
    dotnet build OpenSim/ConsoleClient/OpenSim.ConsoleClient.csproj -c "${CONFIGURATION}"
  fi

  # === Data providers ===
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Data.PGSQL.dll" ]; then
    echo "[all-in-one] Building OpenSim.Data.PGSQL"
    dotnet build OpenSim/Data/PGSQL/OpenSim.Data.PGSQL.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Data.SQLite.dll" ]; then
    echo "[all-in-one] Building OpenSim.Data.SQLite"
    dotnet build OpenSim/Data/SQLite/OpenSim.Data.SQLite.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Data.Model.dll" ]; then
    echo "[all-in-one] Building OpenSim.Data.Model"
    dotnet build Source/OpenSim.Data.Model/OpenSim.Data.Model.csproj -c "${CONFIGURATION}"
  fi

  # === Addon-modules: MoneyData, Currency, MoneyServer ===
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Data.MySQL.MoneyData.dll" ]; then
    echo "[all-in-one] Building OpenSim.Data.MySQL.MoneyData"
    dotnet build addon-modules/OpenSim.Data.MySQL.MoneyData/OpenSim.Data.MySQL.MoneyData/OpenSim.Data.MySQL.MoneyData.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Region.OptionalModules.Currency.dll" ]; then
    echo "[all-in-one] Building OptionalModules.Currency"
    dotnet build addon-modules/OpenSim.Region.OptionalModules.Currency/OpenSim.Region.OptionalModules.Currency/OpenSim.Region.OptionalModules.Currency.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Server.MoneyServer.dll" ]; then
    echo "[all-in-one] Building MoneyServer"
    dotnet build addon-modules/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj -c "${CONFIGURATION}"
  fi

  # === Remaining physics modules ===
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Region.PhysicsModule.BasicPhysics.dll" ]; then
    echo "[all-in-one] Building BasicPhysics module"
    dotnet build OpenSim/Region/PhysicsModules/BasicPhysics/OpenSim.Region.PhysicsModule.BasicPhysics.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Region.PhysicsModule.POS.dll" ]; then
    echo "[all-in-one] Building POS physics module"
    dotnet build OpenSim/Region/PhysicsModules/POS/OpenSim.Region.PhysicsModule.POS.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Region.PhysicsModule.ubOde.dll" ]; then
    echo "[all-in-one] Building ubODE physics module"
    dotnet build OpenSim/Region/PhysicsModules/ubOde/OpenSim.Region.PhysicsModule.ubOde.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSim.Region.PhysicsModule.ubOdeMeshing.dll" ]; then
    echo "[all-in-one] Building ubODE meshing module"
    dotnet build OpenSim/Region/PhysicsModules/ubOdeMeshing/OpenSim.Region.PhysicsModule.ubOdeMeshing.csproj -c "${CONFIGURATION}"
  fi

  # === Script Engine modules ===
  local script_projects=(
    OpenSim/Region/ScriptEngine/Shared/OpenSim.Region.ScriptEngine.Shared.csproj
    OpenSim/Region/ScriptEngine/Shared/Api/Implementation/OpenSim.Region.ScriptEngine.Shared.Api.csproj
    OpenSim/Region/ScriptEngine/Shared/Api/Runtime/OpenSim.Region.ScriptEngine.Shared.Api.Runtime.csproj
    OpenSim/Region/ScriptEngine/Shared/CodeTools/OpenSim.Region.ScriptEngine.Shared.CodeTools.csproj
    OpenSim/Region/ScriptEngine/Shared/Instance/OpenSim.Region.ScriptEngine.Shared.Instance.csproj
    OpenSim/Region/ScriptEngine/YEngine/OpenSim.Region.ScriptEngine.YEngine.csproj
  )
  for project in "${script_projects[@]}"; do
    local dll_name
    dll_name="$(basename "$(dirname "$project")" .csproj | sed 's/^OpenSim\./OpenSim./' || basename "$project" .csproj).dll"
    # Derive DLL name from project directory path (last dir name + .dll is not always correct)
    case "$project" in
      */Shared/OpenSim.Region.ScriptEngine.Shared.csproj)
        dll_name="OpenSim.Region.ScriptEngine.Shared.dll" ;;
      */Implementation/OpenSim.Region.ScriptEngine.Shared.Api.csproj)
        dll_name="OpenSim.Region.ScriptEngine.Shared.Api.dll" ;;
      */Runtime/OpenSim.Region.ScriptEngine.Shared.Api.Runtime.csproj)
        dll_name="OpenSim.Region.ScriptEngine.Shared.Api.Runtime.dll" ;;
      */CodeTools/OpenSim.Region.ScriptEngine.Shared.CodeTools.csproj)
        dll_name="OpenSim.Region.ScriptEngine.Shared.CodeTools.dll" ;;
      */Instance/OpenSim.Region.ScriptEngine.Shared.Instance.csproj)
        dll_name="OpenSim.Region.ScriptEngine.Shared.Instance.dll" ;;
      */YEngine/OpenSim.Region.ScriptEngine.YEngine.csproj)
        dll_name="OpenSim.Region.ScriptEngine.YEngine.dll" ;;
    esac
    if [ ! -f "build/${CONFIGURATION}/${dll_name}" ]; then
      echo "[all-in-one] Building ${dll_name}"
      dotnet build "$project" -c "${CONFIGURATION}"
    fi
  done

  # === Remaining services ===
  local more_service_projects=(
    OpenSim/Services/AuthorizationService/OpenSim.Services.AuthorizationService.csproj
    OpenSim/Services/ExperienceService/OpenSim.Services.ExperienceService.csproj
    OpenSim/Services/FSAssetService/OpenSim.Services.FSAssetService.csproj
    OpenSim/Services/FreeswitchService/OpenSim.Services.FreeswitchService.csproj
    OpenSim/Services/HypergridService/OpenSim.Services.HypergridService.csproj
  )
  for project in "${more_service_projects[@]}"; do
    local dll_name
    dll_name="$(basename "$(dirname "$project")").dll"
    if [ ! -f "build/${CONFIGURATION}/${dll_name}" ]; then
      echo "[all-in-one] Building ${dll_name}"
      dotnet build "$project" -c "${CONFIGURATION}"
    fi
  done

  # === Mutelist & Search modules ===
  if [ ! -f "build/${CONFIGURATION}/OpenSimMutelist.Modules.dll" ]; then
    echo "[all-in-one] Building OpenSimMutelist.Modules"
    dotnet build addon-modules/OpenSimMutelist/Modules/OpenSimMutelist.Modules.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/OpenSimSearch.Modules.dll" ]; then
    echo "[all-in-one] Building OpenSimSearch.Modules"
    dotnet build addon-modules/OpenSimSearch/Modules/OpenSimSearch.Modules.csproj -c "${CONFIGURATION}"
  fi

  # === WebRTC modules ===
  if [ ! -f "build/${CONFIGURATION}/WebRtcJanusService.dll" ]; then
    echo "[all-in-one] Building WebRtcJanusService"
    dotnet build addon-modules/os-webrtc-janus/Janus/WebRtcJanusService.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/WebRtcVoice.dll" ]; then
    echo "[all-in-one] Building WebRtcVoice"
    dotnet build addon-modules/os-webrtc-janus/WebRtcVoice/WebRtcVoice.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/WebRtcVoiceRegionModule.dll" ]; then
    echo "[all-in-one] Building WebRtcVoiceRegionModule"
    dotnet build addon-modules/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.csproj -c "${CONFIGURATION}"
  fi
  if [ ! -f "build/${CONFIGURATION}/WebRtcVoiceServiceModule.dll" ]; then
    echo "[all-in-one] Building WebRtcVoiceServiceModule"
    dotnet build addon-modules/os-webrtc-janus/WebRtcVoiceServiceModule/WebRtcVoiceServiceModule.csproj -c "${CONFIGURATION}"
  fi

  # === WebViewerHub ===
  if [ ! -f "build/${CONFIGURATION}/WebViewerHub.dll" ]; then
    echo "[all-in-one] Building WebViewerHub"
    dotnet build WebViewerHub/WebViewerHub.csproj -c "${CONFIGURATION}"
  fi

  # === Gloebit payment module ===
  if [ ! -f "build/${CONFIGURATION}/Gloebit.dll" ]; then
    echo "[all-in-one] Building Gloebit payment module"
    dotnet build addon-modules/Gloebit/GloebitMoneyModule/Gloebit.csproj -c "${CONFIGURATION}"
  fi

  # === TasiaAddons.DisplayNameCaps (pre-built binary, source not in repo) ===
  if [ ! -f "build/${CONFIGURATION}/TasiaAddons.DisplayNameCaps.dll" ]; then
    if [ -f "bin/TasiaAddons.DisplayNameCaps.dll" ]; then
      echo "[all-in-one] Copying pre-built TasiaAddons.DisplayNameCaps.dll from bin/"
      cp -a bin/TasiaAddons.DisplayNameCaps.dll "build/${CONFIGURATION}/"
    else
      echo "[all-in-one] WARNING: TasiaAddons.DisplayNameCaps.dll not found - skipping"
    fi
  fi
}

sync_runtime_to_bin() {
  echo "[all-in-one] Syncing .NET runtime output into bin"
  # Copy DLLs from root build dir (most projects) and subdirs (TasiaAddons etc.)
  find "build/${CONFIGURATION}/" -maxdepth 2 -name '*.dll' ! -path '*/Resources/*' -exec cp -an {} bin/ \;
  cp -a "build/${CONFIGURATION}/"*.runtimeconfig.json bin/ 2>/dev/null || true
  cp -a "build/${CONFIGURATION}/"*.dll.config bin/ 2>/dev/null || true
  cp -a "build/${CONFIGURATION}/Resources" bin/ 2>/dev/null || true
  # Native libraries (e.g. OpenJPEG J2K decoder)
  if [ -d "build/${CONFIGURATION}/lib64" ]; then
    mkdir -p bin/lib64
    cp -a "build/${CONFIGURATION}/lib64/." bin/lib64/
    # Fixup: ensure libBulletSim.so is the 3.26 version (Bugfix: old 2.86 lib segfaults in PhysicsStep2)
    if [ -f bin/lib64/libBulletSim-3.26-20231207-x86_64.so ]; then
      cp -a "bin/lib64/libBulletSim-3.26-20231207-x86_64.so" bin/lib64/libBulletSim.so
      echo "[all-in-one] Fixed libBulletSim.so -> 3.26-20231207 (old 2.86 version segfaults)"
    fi
  fi
  if [ -f bin/OpenSim.dll.config ]; then
    cp bin/OpenSim.dll.config bin/OpenSim.exe.config
  fi
}

run_robust() {
  local inifile="${1:-Robust.ini}"
  shift || true
  (cd bin && dotnet Robust.dll -inifile="${inifile}" "$@")
}

run_opensim() {
  (cd bin && dotnet OpenSim.dll -inifile=OpenSim.ini "$@")
}

init_grid_user() {
  if [ "${INIT_USER}" != "1" ]; then
    return 0
  fi

  if [ -f /tmp/opensim-user-created ]; then
    return 0
  fi

  echo "[all-in-one] Starting temporary ROBUST to create ${TEST_FIRST} ${TEST_LAST}"
  (
    sleep 20
    printf 'create user %s %s %s %s %s ""\n' "${TEST_FIRST}" "${TEST_LAST}" "${TEST_PASS}" "${TEST_EMAIL}" "${TEST_UUID}"
    sleep 5
    printf 'quit\n'
  ) | timeout 120 bash -lc 'cd bin && dotnet Robust.dll -inifile=Robust.ini' || true

  # Fix ServiceURLs: the "create user" command creates UserAccounts with empty HomeURI,
  # but GatekeeperService.VerifyAgent() needs HomeURI to match the gatekeeper URL
  echo "[all-in-one] Updating ServiceURLs for ${TEST_FIRST} ${TEST_LAST}"
  local HOME_URL="https://${GRID_HOST}:8002"
  local PRIV_URL="http://127.0.0.1:8003"
  # ServiceURLs format required by UserAccountService.MakeUserAccount():
  #   space-separated key=value pairs (NOT URL query format with &)
  #   values are URL-decoded by the parser, so no encoding needed
  mysql -uroot ${DB_NAME} -e "UPDATE UserAccounts SET ServiceURLs = CONCAT('HomeURI=${HOME_URL}/ InventoryServerURI=${PRIV_URL}/ AssetServerURI=${PRIV_URL}/ GatekeeperURI=${HOME_URL}/') WHERE PrincipalID = '${TEST_UUID}';"

  touch /tmp/opensim-user-created
}

seed_terrain_textures() {
  # Insert the 4 standard SL terrain texture UUIDs into the assets table.
  # These UUIDs are hardcoded in viewers for terrain rendering and must exist
  # in the asset service, otherwise terrain renders without visible texture.
  # The data is COPIED from existing OpenSim terrain textures (dirt, grass,
  # mountain, rock) which are loaded from AssetSets.xml and are already valid
  # JPEG2000 images.
  #
  # Mapping: existing AssetSet texture → standard SL terrain UUID:
  #   Terrain Dirt   (b8d3965a-...)  → TerrainDetailMaterial0 (b8d8798a-...)
  #   Terrain Grass  (abb783e6-...)  → TerrainDetailMaterial1 (bbc22e26-...)
  #   Terrain Mountain (179cdabd-...) → TerrainDetailMaterial2 (abb02a94-...)
  #   Terrain Rock   (beb169c7-...)  → TerrainDetailMaterial3 (6a9b2a94-...)
  echo "[all-in-one] Seeding standard SL terrain textures into asset DB"

  # INSERT IGNORE ... SELECT copies the binary data from the existing
  # terrain texture that was loaded from AssetSets.xml/TexturesAssetSet.
  mysql -uroot "${DB_NAME}" -e "
    INSERT IGNORE INTO assets (name, description, assetType, local, temporary, data, id, create_time, access_time, asset_flags, CreatorID)
    SELECT 'TerrainDetailMaterial0', 'Terrain texture - dirt', 0, 0, 0, data, 'b8d8798a-367e-4dc3-9cf6-911a9b75cf14', UNIX_TIMESTAMP(), UNIX_TIMESTAMP(), 0, ''
    FROM assets WHERE id = 'b8d3965a-ad78-bf43-699b-bff8eca6c975';
  " 2>/dev/null && echo "[all-in-one]   Inserted TerrainDetailMaterial0 (dirt)" || echo "[all-in-one]   WARNING: Could not insert TerrainDetailMaterial0"

  mysql -uroot "${DB_NAME}" -e "
    INSERT IGNORE INTO assets (name, description, assetType, local, temporary, data, id, create_time, access_time, asset_flags, CreatorID)
    SELECT 'TerrainDetailMaterial1', 'Terrain texture - grass', 0, 0, 0, data, 'bbc22e26-0e4a-4e95-beee-d727d0f7dcb4', UNIX_TIMESTAMP(), UNIX_TIMESTAMP(), 0, ''
    FROM assets WHERE id = 'abb783e6-3e93-26c0-248a-247666855da3';
  " 2>/dev/null && echo "[all-in-one]   Inserted TerrainDetailMaterial1 (grass)" || echo "[all-in-one]   WARNING: Could not insert TerrainDetailMaterial1"

  mysql -uroot "${DB_NAME}" -e "
    INSERT IGNORE INTO assets (name, description, assetType, local, temporary, data, id, create_time, access_time, asset_flags, CreatorID)
    SELECT 'TerrainDetailMaterial2', 'Terrain texture - mountain', 0, 0, 0, data, 'abb02a94-2cc2-47a4-aa5c-7e318200d86d', UNIX_TIMESTAMP(), UNIX_TIMESTAMP(), 0, ''
    FROM assets WHERE id = '179cdabd-398a-9b6b-1391-4dc333ba321f';
  " 2>/dev/null && echo "[all-in-one]   Inserted TerrainDetailMaterial2 (mountain)" || echo "[all-in-one]   WARNING: Could not insert TerrainDetailMaterial2"

  mysql -uroot "${DB_NAME}" -e "
    INSERT IGNORE INTO assets (name, description, assetType, local, temporary, data, id, create_time, access_time, asset_flags, CreatorID)
    SELECT 'TerrainDetailMaterial3', 'Terrain texture - rock', 0, 0, 0, data, '6a9b2a94-2cc2-47a4-aa5c-7e318200d86d', UNIX_TIMESTAMP(), UNIX_TIMESTAMP(), 0, ''
    FROM assets WHERE id = 'beb169c7-11ea-fff2-efe5-0f24dc881df2';
  " 2>/dev/null && echo "[all-in-one]   Inserted TerrainDetailMaterial3 (rock)" || echo "[all-in-one]   WARNING: Could not insert TerrainDetailMaterial3"
}

start_services() {
  # Custom 404 page (loaded by BaseHttpServer at startup from ./http_404.html)
  if [ -f /src/http_404.html ]; then
    cp /src/http_404.html bin/http_404.html
    echo "[all-in-one] Copied custom 404 page"
  fi

  cp bin/Robust.ini bin/Robust.run.ini
  sed -i 's/^console = basic$/console = rest/' bin/Robust.run.ini

  echo "[all-in-one] Starting ROBUST in background"
  run_robust Robust.run.ini < /dev/null &
  ROBUST_PID=$!

  echo "[all-in-one] Waiting for ROBUST public HTTPS port 8002"
  for i in $(seq 1 60); do
    if curl -k -sS -o /dev/null "https://127.0.0.1:8002/" >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done

  if ! kill -0 "${ROBUST_PID}" 2>/dev/null; then
    echo "[all-in-one] ROBUST exited before OpenSim startup"
    exit 1
  fi

  # Seed standard SL terrain textures (copy data from existing AssetSet textures)
  # Must happen after ROBUST is running because source textures are loaded
  # from AssetSets.xml by the asset service on startup.
  seed_terrain_textures

  echo "[all-in-one] Starting OpenSim region foreground"
  cd bin
  exec dotnet OpenSim.dll -inifile=OpenSim.ini
}

trap 'echo "[all-in-one] stopping"; kill ${ROBUST_PID:-0} ${MYSQL_PID:-0} 2>/dev/null || true' EXIT

start_mysql
setup_database
generate_quic_cert
write_configs
build_binaries
sync_runtime_to_bin
init_grid_user
start_services
