<?php

declare(strict_types=1);

final class MarketplaceApp
{
    private array $config;
    private mysqli $wpDb;
    private mysqli $identityDb;
    private mysqli $moneyDb;
    private string $deliveryApiUrl = '';
    private array $regionDeliveryUrls = [];
    private string $textureProxySource = '';
    private string $runtimeSettingsFile = '';

    public function __construct(array $config)
    {
        $this->config = $config;
        $this->wpDb = $this->connectDb($config['wordpress']);
        $identityCfg = $config['identity'] ?? $config['opensim'] ?? null;
        if (!is_array($identityCfg)) {
            throw new RuntimeException('Missing identity database configuration');
        }
        $this->identityDb = $this->connectDb($identityCfg);
        $this->moneyDb = $this->connectDb($config['money']);
        $this->runtimeSettingsFile = (string)($this->config['app']['runtime_settings_file'] ?? (__DIR__ . '/../storage/admin_settings.json'));
        $this->loadRuntimeSettings();
        $this->textureProxySource = trim((string)($this->config['texture']['proxy_source'] ?? ''));
        $this->loadDeliveryRoutingFromWordPress();
    }

    public function getContext(): array
    {
        $uuid = $_SESSION['avatar_uuid'] ?? '';
        if (!$this->isValidUuid($uuid)) {
            return ['authenticated' => false];
        }

        return [
            'authenticated' => true,
            'uuid' => $uuid,
            'name' => $this->getAvatarName($uuid) ?? $uuid,
            'balance' => $this->getBalance($uuid),
            'is_admin' => $this->isAdminUuid($uuid),
        ];
    }

    public function fetchTexture(string $uuid): array
    {
        $uuid = strtolower(trim($uuid));
        if (!$this->isValidUuid($uuid)) {
            return ['ok' => false, 'status' => 400, 'message' => 'Invalid texture UUID'];
        }

        $template = trim($this->textureProxySource);
        if ($template === '') {
            $template = 'http://i.let-us.cyou:8002/index.php?method=GridTexture&uuid={uuid}';
        }

        $url = strpos($template, '{uuid}') !== false
            ? str_replace('{uuid}', $uuid, $template)
            : rtrim($template, '/') . '/' . $uuid;

        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CONNECTTIMEOUT => 5,
            CURLOPT_TIMEOUT => 20,
            CURLOPT_FOLLOWLOCATION => true,
            CURLOPT_HTTPHEADER => ['Accept: image/*,*/*;q=0.8'],
        ]);
        $body = curl_exec($ch);
        $err = curl_error($ch);
        $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        $contentType = (string)curl_getinfo($ch, CURLINFO_CONTENT_TYPE);
        curl_close($ch);

        if ($err !== '') {
            return ['ok' => false, 'status' => 502, 'message' => 'Texture proxy error: ' . $err];
        }
        if ($status < 200 || $status >= 300 || !is_string($body) || $body === '') {
            return ['ok' => false, 'status' => 404, 'message' => 'Texture not available'];
        }

        if ($contentType === '') {
            $contentType = 'image/jpeg';
        }

        return [
            'ok' => true,
            'status' => 200,
            'content_type' => $contentType,
            'body' => $body,
        ];
    }

    public function getAdminDashboard(): array
    {
        $this->assertAdmin();

        $prefix = $this->config['wordpress']['prefix'];
        $posts = $prefix . 'posts';
        $meta = $prefix . 'postmeta';
        $ordersTable = $prefix . 'market_orders';
        $logsTable = $prefix . 'market_delivery_logs';

        $itemsForSale = 0;
        $sqlItems = "SELECT COUNT(*) c FROM {$posts} p JOIN {$meta} m ON m.post_id = p.ID AND m.meta_key = '_forsale' AND m.meta_value='1' WHERE p.post_type='opensim_item' AND p.post_status='publish'";
        if ($res = $this->wpDb->query($sqlItems)) {
            $row = $res->fetch_assoc();
            $itemsForSale = (int)($row['c'] ?? 0);
            $res->close();
        }

        $orders = null;
        if ($this->tableExists($ordersTable)) {
            $res = $this->wpDb->query("SELECT COUNT(*) c FROM {$ordersTable}");
            if ($res) {
                $row = $res->fetch_assoc();
                $orders = (int)($row['c'] ?? 0);
                $res->close();
            }
        }

        $recentFailures = $this->getAdminLogs()['logs'];

        return [
            'summary' => [
                'items_for_sale' => $itemsForSale,
                'orders_total' => $orders,
                'regions_with_overrides' => count($this->regionDeliveryUrls),
                'delivery_api_default' => $this->deliveryApiUrl,
            ],
            'region_routes' => $this->regionDeliveryUrls,
            'recent_failures' => $recentFailures,
        ];
    }

    public function getAdminLogs(): array
    {
        $this->assertAdmin();

        $prefix = $this->config['wordpress']['prefix'];
        $logsTable = $prefix . 'market_delivery_logs';
        $logs = [];

        if ($this->tableExists($logsTable)) {
            $res = $this->wpDb->query("SELECT id, order_id, status, message, created_at FROM {$logsTable} ORDER BY id DESC LIMIT 100");
            if ($res) {
                while ($row = $res->fetch_assoc()) {
                    $logs[] = $row;
                }
                $res->close();
            }
        }

        return ['logs' => $logs];
    }

    public function getAdminSettings(): array
    {
        $this->assertAdmin();

        return [
            'delivery_api_url' => $this->getWpOption('osmp_delivery_api_url', $this->deliveryApiUrl),
            'region_overrides' => $this->getWpOption('osmp_region_uuids', ''),
            'texture_proxy_template' => $this->getWpOption('osmp_texture_proxy_template', $this->textureProxySource),
            'delivery_api_password' => (string)($this->config['delivery']['api_password'] ?? ''),
            'timeout_seconds' => (int)($this->config['delivery']['timeout_seconds'] ?? 25),
            'auth_backup_enabled' => !empty($this->config['auth_backup']['enabled']),
            'auth_backup_password' => (string)($this->config['auth_backup']['password'] ?? ''),
            'auth_backup_allowed_uuid' => (string)($this->config['auth_backup']['allowed_uuid'] ?? ''),
            'admin_uuids' => implode("\n", $this->config['app']['admin_uuids'] ?? []),
        ];
    }

    public function saveAdminSettings(array $input): array
    {
        $this->assertAdmin();

        $deliveryApiUrl = trim((string)($input['delivery_api_url'] ?? ''));
        $regionOverrides = (string)($input['region_overrides'] ?? '');
        $textureProxyTemplate = trim((string)($input['texture_proxy_template'] ?? ''));
        $deliveryApiPassword = (string)($input['delivery_api_password'] ?? '');
        $timeoutSeconds = max(5, min(120, (int)($input['timeout_seconds'] ?? 25)));
        $backupEnabled = in_array((string)($input['auth_backup_enabled'] ?? ''), ['1', 'true', 'on', 'yes'], true);
        $backupPassword = (string)($input['auth_backup_password'] ?? '');
        $backupAllowedUuid = strtolower(trim((string)($input['auth_backup_allowed_uuid'] ?? '')));
        $adminUuidsRaw = (string)($input['admin_uuids'] ?? '');

        if ($deliveryApiUrl !== '') {
            $this->setWpOption('osmp_delivery_api_url', $deliveryApiUrl);
        }
        $this->setWpOption('osmp_region_uuids', trim($regionOverrides));
        if ($textureProxyTemplate !== '') {
            $this->setWpOption('osmp_texture_proxy_template', $textureProxyTemplate);
        }

        $adminUuids = [];
        foreach (preg_split('/[\r\n,]+/', $adminUuidsRaw) ?: [] as $candidate) {
            $candidate = strtolower(trim((string)$candidate));
            if ($this->isValidUuid($candidate)) {
                $adminUuids[] = $candidate;
            }
        }
        $adminUuids = array_values(array_unique($adminUuids));
        if (empty($adminUuids)) {
            $ctx = $this->getContext();
            $adminUuids[] = strtolower((string)($ctx['uuid'] ?? ''));
        }

        if ($backupAllowedUuid !== '' && !$this->isValidUuid($backupAllowedUuid)) {
            throw new RuntimeException('Invalid backup allowed UUID');
        }

        $this->config['delivery']['api_password'] = $deliveryApiPassword;
        $this->config['delivery']['timeout_seconds'] = $timeoutSeconds;
        $this->config['auth_backup']['enabled'] = $backupEnabled;
        $this->config['auth_backup']['password'] = $backupPassword;
        $this->config['auth_backup']['allowed_uuid'] = $backupAllowedUuid;
        $this->config['app']['admin_uuids'] = $adminUuids;

        $this->saveRuntimeSettings([
            'delivery_api_password' => $deliveryApiPassword,
            'timeout_seconds' => $timeoutSeconds,
            'auth_backup_enabled' => $backupEnabled,
            'auth_backup_password' => $backupPassword,
            'auth_backup_allowed_uuid' => $backupAllowedUuid,
            'admin_uuids' => $adminUuids,
        ]);

        $this->loadDeliveryRoutingFromWordPress();

        return $this->getAdminSettings();
    }

    public function loginAvatar(string $uuid, string $password): array
    {
        $this->validateCredentials($uuid, $password);
        return $this->completeLogin($uuid);
    }

    public function validateCredentials(string $uuid, string $password): void
    {
        $uuid = strtolower(trim($uuid));
        if (!$this->isValidUuid($uuid)) {
            throw new RuntimeException('Invalid avatar UUID');
        }

        if ($password === '') {
            throw new RuntimeException('Password is required');
        }

        if ($this->canUseBackupLogin($uuid, $password)) {
            return;
        }

        $auth = $this->getAuthRow($uuid);
        if (!$auth) {
            throw new RuntimeException('Invalid credentials');
        }

        $salt = (string)($auth['passwordSalt'] ?? '');
        $storedHash = strtolower((string)($auth['passwordHash'] ?? ''));
        if ($salt === '' || $storedHash === '') {
            throw new RuntimeException('Invalid credentials');
        }

        if (!$this->passwordMatches($password, $salt, $storedHash)) {
            throw new RuntimeException('Invalid credentials');
        }
    }

    public function completeLogin(string $uuid): array
    {
        $uuid = strtolower(trim($uuid));
        if (!$this->isValidUuid($uuid)) {
            throw new RuntimeException('Invalid avatar UUID');
        }
        session_regenerate_id(true);
        $_SESSION['avatar_uuid'] = $uuid;
        return $this->getContext();
    }

    public function logoutAvatar(): void
    {
        unset($_SESSION['avatar_uuid']);
    }

    public function grantDoritos(string $targetUuid, int $doritos, string $reason = 'vista-token-flow'): array
    {
        $this->assertAdmin();

        $targetUuid = strtolower(trim($targetUuid));
        if (!$this->isValidUuid($targetUuid)) {
            throw new RuntimeException('Invalid target UUID');
        }
        if ($doritos <= 0) {
            throw new RuntimeException('Dorito amount must be positive');
        }

        $tx = 'dorito-topup-' . bin2hex(random_bytes(6));
        $this->upsertBalance($targetUuid, (float)$doritos);

        return [
            'target_uuid' => $targetUuid,
            'target_name' => $this->getAvatarName($targetUuid) ?? $targetUuid,
            'granted' => $doritos,
            'new_balance' => $this->getBalance($targetUuid),
            'transaction' => $tx,
        ];
    }

    public function listItems(array $filters): array
    {
        $prefix = $this->config['wordpress']['prefix'];
        $posts = $prefix . 'posts';
        $meta = $prefix . 'postmeta';

        $where = ["p.post_type = 'opensim_item'", "p.post_status = 'publish'", "forsale.meta_value = '1'"];
        $types = '';
        $params = [];

        $q = trim((string)($filters['q'] ?? ''));
        if ($q !== '') {
            $where[] = 'p.post_title LIKE ?';
            $types .= 's';
            $params[] = '%' . $q . '%';
        }

        $region = trim((string)($filters['region'] ?? ''));
        if ($region !== '') {
            $where[] = 'region.meta_value = ?';
            $types .= 's';
            $params[] = $region;
        }

        $min = isset($filters['minp']) && $filters['minp'] !== '' ? (float)$filters['minp'] : null;
        if ($min !== null) {
            $where[] = 'CAST(price.meta_value AS DECIMAL(10,2)) >= ?';
            $types .= 'd';
            $params[] = $min;
        }

        $max = isset($filters['maxp']) && $filters['maxp'] !== '' ? (float)$filters['maxp'] : null;
        if ($max !== null) {
            $where[] = 'CAST(price.meta_value AS DECIMAL(10,2)) <= ?';
            $types .= 'd';
            $params[] = $max;
        }

        $page = max(1, (int)($filters['page'] ?? 1));
        $perPage = min(48, max(1, (int)($filters['per_page'] ?? 12)));
        $offset = ($page - 1) * $perPage;

        $baseSql = "
            FROM {$posts} p
            JOIN {$meta} forsale ON forsale.post_id = p.ID AND forsale.meta_key = '_forsale'
            LEFT JOIN {$meta} prim ON prim.post_id = p.ID AND prim.meta_key = '_prim_uuid'
            LEFT JOIN {$meta} seller ON seller.post_id = p.ID AND seller.meta_key = '_seller_uuid'
            LEFT JOIN {$meta} price ON price.post_id = p.ID AND price.meta_key = '_price'
            LEFT JOIN {$meta} region ON region.post_id = p.ID AND region.meta_key = '_region_uuid'
            LEFT JOIN {$meta} region_label ON region_label.post_id = p.ID AND region_label.meta_key = '_region_label'
            LEFT JOIN {$meta} texture ON texture.post_id = p.ID AND texture.meta_key = '_texture_url'
            LEFT JOIN {$meta} texture_uuid ON texture_uuid.post_id = p.ID AND texture_uuid.meta_key = '_texture_uuid'
            WHERE " . implode(' AND ', $where);

        $countSql = 'SELECT COUNT(DISTINCT p.ID) ' . $baseSql;
        $countStmt = $this->wpDb->prepare($countSql);
        if (!$countStmt) {
            throw new RuntimeException('Failed to prepare item count query');
        }
        if ($types !== '') {
            $countStmt->bind_param($types, ...$params);
        }
        $countStmt->execute();
        $countResult = $countStmt->get_result();
        $total = (int)($countResult->fetch_row()[0] ?? 0);
        $countStmt->close();

        $listSql = "
            SELECT DISTINCT
                p.ID,
                p.post_title,
                COALESCE(prim.meta_value, '') AS prim_uuid,
                COALESCE(seller.meta_value, '') AS seller_uuid,
                COALESCE(price.meta_value, '0') AS price,
                COALESCE(region.meta_value, '') AS region_uuid,
                COALESCE(region_label.meta_value, region.meta_value, '') AS region_label,
                COALESCE(texture.meta_value, '') AS texture_url,
                COALESCE(texture_uuid.meta_value, '') AS texture_uuid
            {$baseSql}
            ORDER BY p.post_title ASC
            LIMIT ? OFFSET ?";

        $listStmt = $this->wpDb->prepare($listSql);
        if (!$listStmt) {
            throw new RuntimeException('Failed to prepare item list query');
        }
        $listTypes = $types . 'ii';
        $listParams = $params;
        $listParams[] = $perPage;
        $listParams[] = $offset;
        $listStmt->bind_param($listTypes, ...$listParams);
        $listStmt->execute();
        $rows = $listStmt->get_result()->fetch_all(MYSQLI_ASSOC);
        $listStmt->close();

        foreach ($rows as &$row) {
            $ru = (string)($row['region_uuid'] ?? '');
            $rl = (string)($row['region_label'] ?? '');
            if ($ru !== '' && ($rl === '' || strtolower($rl) === strtolower($ru))) {
                $resolved = $this->getRegionName($ru);
                if ($resolved !== '') {
                    $row['region_label'] = $resolved;
                }
            }
        }
        unset($row);

        return [
            'items' => $rows,
            'page' => $page,
            'per_page' => $perPage,
            'total' => $total,
            'total_pages' => (int)ceil($total / max(1, $perPage)),
        ];
    }

    public function listRegions(): array
    {
        $prefix = $this->config['wordpress']['prefix'];
        $posts = $prefix . 'posts';
        $meta = $prefix . 'postmeta';

        $sql = "
            SELECT DISTINCT
                COALESCE(region.meta_value, '') AS region_uuid,
                COALESCE(region_label.meta_value, region.meta_value, '') AS region_label
            FROM {$posts} p
            JOIN {$meta} forsale ON forsale.post_id = p.ID AND forsale.meta_key = '_forsale' AND forsale.meta_value = '1'
            LEFT JOIN {$meta} region ON region.post_id = p.ID AND region.meta_key = '_region_uuid'
            LEFT JOIN {$meta} region_label ON region_label.post_id = p.ID AND region_label.meta_key = '_region_label'
            WHERE p.post_type = 'opensim_item' AND p.post_status = 'publish' AND COALESCE(region.meta_value, '') <> ''
            ORDER BY region_label ASC";

        $res = $this->wpDb->query($sql);
        if (!$res) {
            return [];
        }

        $out = [];
        while ($row = $res->fetch_assoc()) {
            $uuid = (string)($row['region_uuid'] ?? '');
            if ($uuid === '') {
                continue;
            }
            $label = (string)($row['region_label'] ?? '');
            if ($label === '' || strtolower($label) === strtolower($uuid)) {
                $resolved = $this->getRegionName($uuid);
                if ($resolved !== '') {
                    $label = $resolved;
                }
            }
            $out[] = ['uuid' => $uuid, 'label' => $label !== '' ? $label : $uuid];
        }
        $res->close();

        return $out;
    }

    public function purchase(int $itemId, string $recipientUuid = ''): array
    {
        $buyerUuid = (string)($_SESSION['avatar_uuid'] ?? '');
        if (!$this->isValidUuid($buyerUuid)) {
            throw new RuntimeException('Authentication required');
        }

        $recipientUuid = strtolower(trim($recipientUuid));
        if ($recipientUuid === '') {
            $recipientUuid = $buyerUuid;
        }
        if (!$this->isValidUuid($recipientUuid)) {
            throw new RuntimeException('Invalid recipient UUID');
        }

        $item = $this->getItemById($itemId);
        if (!$item) {
            throw new RuntimeException('Item not found');
        }

        $price = (float)$item['price'];
        $sellerUuid = (string)$item['seller_uuid'];
        $primUuid = (string)$item['prim_uuid'];
        $regionUuid = (string)$item['region_uuid'];
        $itemName = (string)$item['post_title'];

        if ($price < 0) {
            throw new RuntimeException('Invalid price');
        }
        if (!$this->isValidUuid($primUuid)) {
            throw new RuntimeException('Invalid prim UUID');
        }

        $this->moneyDb->begin_transaction();
        try {
            $buyerBalance = $this->getBalanceForUpdate($buyerUuid);
            if ($buyerBalance < $price) {
                throw new RuntimeException('Insufficient funds');
            }

            $this->changeBalance($buyerUuid, -$price);
            if ($this->isValidUuid($sellerUuid)) {
                $this->changeBalance($sellerUuid, $price);
            }

            $this->moneyDb->commit();
        } catch (Throwable $e) {
            $this->moneyDb->rollback();
            throw $e;
        }

        $recipientName = $this->getAvatarName($recipientUuid) ?? $recipientUuid;
        $delivery = $this->deliverItem($primUuid, $recipientUuid, $itemName, $regionUuid);

        return [
            'status' => $delivery['status'],
            'message' => $delivery['message'],
            'recipient_uuid' => $recipientUuid,
            'recipient' => $recipientName,
            'buyer_balance' => $this->getBalance($buyerUuid),
        ];
    }

    private function deliverItem(string $primUuid, string $recipientUuid, string $itemName, string $regionUuid): array
    {
        $query = [
            'oid' => $primUuid,
            'uid' => $recipientUuid,
            'name' => $itemName,
        ];
        if ($this->isValidUuid($regionUuid)) {
            $query['region'] = $regionUuid;
        }
        $apiPass = (string)($this->config['delivery']['api_password'] ?? '');
        if ($apiPass !== '' && $apiPass !== '[secrethere]') {
            $query['pass'] = $apiPass;
        }

        $url = rtrim($this->getDeliveryApiBase($regionUuid), '?');
        if ($url === '') {
            return ['status' => 'failed', 'message' => 'Delivery failed: API URL is not configured'];
        }
        $url .= '?' . http_build_query($query);

        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CONNECTTIMEOUT => 5,
            CURLOPT_TIMEOUT => (int)($this->config['delivery']['timeout_seconds'] ?? 25),
            CURLOPT_FOLLOWLOCATION => true,
            CURLOPT_HTTPHEADER => ['Accept: application/json, text/plain;q=0.9'],
        ]);

        $body = (string)curl_exec($ch);
        $err = curl_error($ch);
        $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);

        if ($err !== '') {
            return ['status' => 'failed', 'message' => 'Delivery failed: ' . $err];
        }
        if ($status < 200 || $status >= 300) {
            return ['status' => 'failed', 'message' => 'Delivery failed (' . $status . '): ' . ($body !== '' ? $body : 'empty response')];
        }

        $decoded = json_decode($body, true);
        if (is_array($decoded) && !empty($decoded['message'])) {
            return ['status' => 'delivered', 'message' => (string)$decoded['message']];
        }
        return ['status' => 'delivered', 'message' => $body !== '' ? $body : ('Item sent to ' . $recipientUuid)];
    }

    private function getItemById(int $itemId): ?array
    {
        $prefix = $this->config['wordpress']['prefix'];
        $posts = $prefix . 'posts';
        $meta = $prefix . 'postmeta';

        $sql = "
            SELECT
                p.ID,
                p.post_title,
                prim.meta_value AS prim_uuid,
                seller.meta_value AS seller_uuid,
                price.meta_value AS price,
                region.meta_value AS region_uuid
            FROM {$posts} p
            JOIN {$meta} forsale ON forsale.post_id = p.ID AND forsale.meta_key = '_forsale' AND forsale.meta_value = '1'
            LEFT JOIN {$meta} prim ON prim.post_id = p.ID AND prim.meta_key = '_prim_uuid'
            LEFT JOIN {$meta} seller ON seller.post_id = p.ID AND seller.meta_key = '_seller_uuid'
            LEFT JOIN {$meta} price ON price.post_id = p.ID AND price.meta_key = '_price'
            LEFT JOIN {$meta} region ON region.post_id = p.ID AND region.meta_key = '_region_uuid'
            WHERE p.ID = ? AND p.post_type = 'opensim_item' AND p.post_status = 'publish'
            LIMIT 1";

        $stmt = $this->wpDb->prepare($sql);
        if (!$stmt) {
            throw new RuntimeException('Failed to prepare item query');
        }
        $stmt->bind_param('i', $itemId);
        $stmt->execute();
        $res = $stmt->get_result()->fetch_assoc();
        $stmt->close();
        return $res ?: null;
    }

    private function getAvatarName(string $uuid): ?string
    {
        foreach (['useraccounts', 'UserAccounts'] as $table) {
            $sql = "SELECT FirstName, LastName FROM {$table} WHERE PrincipalID = ? LIMIT 1";
            $stmt = $this->identityDb->prepare($sql);
            if (!$stmt) {
                continue;
            }
            $stmt->bind_param('s', $uuid);
            $stmt->execute();
            $row = $stmt->get_result()->fetch_assoc();
            $stmt->close();
            if ($row) {
                $name = trim(($row['FirstName'] ?? '') . ' ' . ($row['LastName'] ?? ''));
                return $name !== '' ? $name : null;
            }
        }
        return null;
    }

    private function getAuthRow(string $uuid): ?array
    {
        $stmt = $this->identityDb->prepare('SELECT passwordHash, passwordSalt FROM auth WHERE UUID = ? LIMIT 1');
        if (!$stmt) {
            throw new RuntimeException('Authentication backend unavailable');
        }
        $stmt->bind_param('s', $uuid);
        $stmt->execute();
        $row = $stmt->get_result()->fetch_assoc();
        $stmt->close();
        return $row ?: null;
    }

    private function getRegionName(string $regionUuid): string
    {
        $stmt = $this->identityDb->prepare('SELECT regionName FROM regions WHERE UUID = ? LIMIT 1');
        if (!$stmt) {
            return '';
        }
        $stmt->bind_param('s', $regionUuid);
        $stmt->execute();
        $row = $stmt->get_result()->fetch_assoc();
        $stmt->close();
        return trim((string)($row['regionName'] ?? ''));
    }

    private function loadDeliveryRoutingFromWordPress(): void
    {
        $this->regionDeliveryUrls = [];
        $prefix = $this->config['wordpress']['prefix'];
        $optionsTable = $prefix . 'options';

        $stmt = $this->wpDb->prepare("SELECT option_name, option_value FROM {$optionsTable} WHERE option_name IN ('osmp_delivery_api_url','osmp_region_uuids','osmp_texture_proxy_template')");
        if (!$stmt) {
            return;
        }

        $stmt->execute();
        $res = $stmt->get_result();
        $values = [];
        while ($row = $res->fetch_assoc()) {
            $values[(string)$row['option_name']] = (string)$row['option_value'];
        }
        $stmt->close();

        $global = trim((string)($values['osmp_delivery_api_url'] ?? ''));
        if ($global !== '') {
            $this->deliveryApiUrl = $global;
        } else {
            $this->deliveryApiUrl = trim((string)($this->config['delivery']['api_url'] ?? ''));
        }

        $textureProxy = trim((string)($values['osmp_texture_proxy_template'] ?? ''));
        if ($textureProxy !== '') {
            $this->textureProxySource = $textureProxy;
        }

        $overrides = trim((string)($values['osmp_region_uuids'] ?? ''));
        if ($overrides === '') {
            return;
        }

        $lines = preg_split('/\r\n|\r|\n/', $overrides) ?: [];
        foreach ($lines as $line) {
            $line = trim($line);
            if ($line === '') {
                continue;
            }
            $parts = array_map('trim', explode('|', $line));
            $uuid = strtolower((string)($parts[0] ?? ''));
            $url = trim((string)($parts[2] ?? ''));
            if ($this->isValidUuid($uuid) && $url !== '') {
                $this->regionDeliveryUrls[$uuid] = $url;
            }
        }
    }

    private function loadRuntimeSettings(): void
    {
        $path = $this->runtimeSettingsFile;
        if ($path === '' || !is_file($path)) {
            return;
        }

        $raw = @file_get_contents($path);
        if (!is_string($raw) || trim($raw) === '') {
            return;
        }
        $data = json_decode($raw, true);
        if (!is_array($data)) {
            return;
        }

        if (isset($data['delivery_api_password']) && is_string($data['delivery_api_password'])) {
            $this->config['delivery']['api_password'] = $data['delivery_api_password'];
        }

        if (isset($data['timeout_seconds'])) {
            $timeout = max(5, min(120, (int)$data['timeout_seconds']));
            $this->config['delivery']['timeout_seconds'] = $timeout;
        }

        if (isset($data['auth_backup_enabled'])) {
            $this->config['auth_backup']['enabled'] = (bool)$data['auth_backup_enabled'];
        }
        if (isset($data['auth_backup_password']) && is_string($data['auth_backup_password'])) {
            $this->config['auth_backup']['password'] = $data['auth_backup_password'];
        }
        if (isset($data['auth_backup_allowed_uuid']) && is_string($data['auth_backup_allowed_uuid'])) {
            $this->config['auth_backup']['allowed_uuid'] = strtolower(trim($data['auth_backup_allowed_uuid']));
        }

        if (isset($data['admin_uuids']) && is_array($data['admin_uuids'])) {
            $clean = [];
            foreach ($data['admin_uuids'] as $u) {
                $u = strtolower(trim((string)$u));
                if ($this->isValidUuid($u)) {
                    $clean[] = $u;
                }
            }
            if (!empty($clean)) {
                $this->config['app']['admin_uuids'] = array_values(array_unique($clean));
            }
        }
    }

    private function saveRuntimeSettings(array $settings): void
    {
        $dir = dirname($this->runtimeSettingsFile);
        if (!is_dir($dir)) {
            @mkdir($dir, 0775, true);
        }

        $json = json_encode($settings, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES);
        if (!is_string($json)) {
            throw new RuntimeException('Failed to encode runtime settings');
        }

        if (@file_put_contents($this->runtimeSettingsFile, $json . "\n", LOCK_EX) === false) {
            throw new RuntimeException('Failed to save runtime settings file');
        }
    }

    private function getDeliveryApiBase(string $regionUuid): string
    {
        $uuid = strtolower(trim($regionUuid));
        if ($uuid !== '' && isset($this->regionDeliveryUrls[$uuid])) {
            return $this->regionDeliveryUrls[$uuid];
        }
        if ($this->deliveryApiUrl !== '') {
            return $this->deliveryApiUrl;
        }
        return trim((string)($this->config['delivery']['api_url'] ?? ''));
    }

    private function passwordMatches(string $passwordInput, string $salt, string $storedHash): bool
    {
        $stored = strtolower($storedHash);
        $raw = $passwordInput;
        $normalized = $this->normalizePasswordForOpenSim($raw);

        $candidates = [
            md5($normalized . ':' . $salt),
            md5(md5($raw) . ':' . $salt),
            md5($raw . ':' . $salt),
        ];

        if (str_starts_with($raw, '$1$')) {
            $payload = substr($raw, 3);
            $candidates[] = md5($payload . ':' . $salt);
        }

        foreach ($candidates as $candidate) {
            if (hash_equals($stored, strtolower($candidate))) {
                return true;
            }
        }

        return false;
    }

    private function normalizePasswordForOpenSim(string $input): string
    {
        $pwd = $input;
        if ($pwd === '') {
            return '';
        }

        if (str_starts_with($pwd, '$1$')) {
            $pwd = substr($pwd, 3);
        }

        if (preg_match('/^[0-9a-f]{32}$/i', $pwd)) {
            return strtolower($pwd);
        }

        return md5($pwd);
    }

    private function canUseBackupLogin(string $uuid, string $password): bool
    {
        $cfg = $this->config['auth_backup'] ?? null;
        if (!is_array($cfg)) {
            return false;
        }

        $enabled = (bool)($cfg['enabled'] ?? false);
        if (!$enabled) {
            return false;
        }

        $backupPassword = (string)($cfg['password'] ?? '');
        if ($backupPassword === '') {
            return false;
        }

        $allowedUuid = strtolower(trim((string)($cfg['allowed_uuid'] ?? '')));
        if ($allowedUuid !== '' && $allowedUuid !== strtolower($uuid)) {
            return false;
        }

        return hash_equals($backupPassword, $password);
    }

    private function assertAdmin(): void
    {
        $ctx = $this->getContext();
        if (empty($ctx['authenticated']) || empty($ctx['is_admin'])) {
            throw new RuntimeException('Admin access required');
        }
    }

    private function getWpOption(string $name, string $default = ''): string
    {
        $prefix = $this->config['wordpress']['prefix'];
        $table = $prefix . 'options';
        $stmt = $this->wpDb->prepare("SELECT option_value FROM {$table} WHERE option_name = ? LIMIT 1");
        if (!$stmt) {
            return $default;
        }
        $stmt->bind_param('s', $name);
        $stmt->execute();
        $row = $stmt->get_result()->fetch_assoc();
        $stmt->close();
        if (!$row || !array_key_exists('option_value', $row)) {
            return $default;
        }
        return (string)$row['option_value'];
    }

    private function setWpOption(string $name, string $value): void
    {
        $prefix = $this->config['wordpress']['prefix'];
        $table = $prefix . 'options';

        $check = $this->wpDb->prepare("SELECT option_id FROM {$table} WHERE option_name = ? LIMIT 1");
        if (!$check) {
            throw new RuntimeException('Failed to prepare option lookup');
        }
        $check->bind_param('s', $name);
        $check->execute();
        $row = $check->get_result()->fetch_assoc();
        $check->close();

        if ($row) {
            $upd = $this->wpDb->prepare("UPDATE {$table} SET option_value = ? WHERE option_name = ?");
            if (!$upd) {
                throw new RuntimeException('Failed to prepare option update');
            }
            $upd->bind_param('ss', $value, $name);
            if (!$upd->execute()) {
                $upd->close();
                throw new RuntimeException('Failed to update option ' . $name);
            }
            $upd->close();
            return;
        }

        $autoload = 'yes';
        $ins = $this->wpDb->prepare("INSERT INTO {$table} (option_name, option_value, autoload) VALUES (?, ?, ?)");
        if (!$ins) {
            throw new RuntimeException('Failed to prepare option insert');
        }
        $ins->bind_param('sss', $name, $value, $autoload);
        if (!$ins->execute()) {
            $ins->close();
            throw new RuntimeException('Failed to insert option ' . $name);
        }
        $ins->close();
    }

    private function isAdminUuid(string $uuid): bool
    {
        $admins = $this->config['app']['admin_uuids'] ?? [];
        if (!is_array($admins)) {
            return false;
        }

        $needle = strtolower(trim($uuid));
        foreach ($admins as $candidate) {
            $candidate = strtolower(trim((string)$candidate));
            if ($candidate !== '' && $candidate === $needle) {
                return true;
            }
        }
        return false;
    }

    private function tableExists(string $table): bool
    {
        $escaped = $this->wpDb->real_escape_string($table);
        $res = $this->wpDb->query("SHOW TABLES LIKE '{$escaped}'");
        if (!$res) {
            return false;
        }
        $exists = $res->num_rows > 0;
        $res->close();
        return $exists;
    }

    private function getBalance(string $uuid): ?float
    {
        $sql = 'SELECT balance FROM balances WHERE user = ? LIMIT 1';
        $stmt = $this->moneyDb->prepare($sql);
        if (!$stmt) {
            return null;
        }
        $stmt->bind_param('s', $uuid);
        $stmt->execute();
        $row = $stmt->get_result()->fetch_assoc();
        $stmt->close();
        return $row ? (float)$row['balance'] : null;
    }

    private function getBalanceForUpdate(string $uuid): float
    {
        $sql = 'SELECT balance FROM balances WHERE user = ? FOR UPDATE';
        $stmt = $this->moneyDb->prepare($sql);
        if (!$stmt) {
            throw new RuntimeException('Failed to prepare balance query');
        }
        $stmt->bind_param('s', $uuid);
        $stmt->execute();
        $row = $stmt->get_result()->fetch_assoc();
        $stmt->close();
        if (!$row) {
            throw new RuntimeException('Buyer balance not found');
        }
        return (float)$row['balance'];
    }

    private function changeBalance(string $uuid, float $delta): void
    {
        $sql = 'UPDATE balances SET balance = balance + ? WHERE user = ?';
        $stmt = $this->moneyDb->prepare($sql);
        if (!$stmt) {
            throw new RuntimeException('Failed to prepare balance update');
        }
        $stmt->bind_param('ds', $delta, $uuid);
        if (!$stmt->execute()) {
            $stmt->close();
            throw new RuntimeException('Failed to update balance');
        }
        $stmt->close();
    }

    private function upsertBalance(string $uuid, float $delta): void
    {
        $sql = "
            INSERT INTO balances (`user`, `balance`, `status`, `type`)
            VALUES (?, ?, 0, 0)
            ON DUPLICATE KEY UPDATE
                balance = balance + VALUES(balance)";
        $stmt = $this->moneyDb->prepare($sql);
        if (!$stmt) {
            throw new RuntimeException('Failed to prepare dorito upsert');
        }
        $stmt->bind_param('sd', $uuid, $delta);
        if (!$stmt->execute()) {
            $stmt->close();
            throw new RuntimeException('Failed to upsert dorito balance');
        }
        $stmt->close();
    }

    private function isValidUuid(string $uuid): bool
    {
        return (bool)preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i', $uuid);
    }

    private function connectDb(array $cfg): mysqli
    {
        mysqli_report(MYSQLI_REPORT_OFF);
        $db = @new mysqli(
            (string)$cfg['host'],
            (string)$cfg['user'],
            (string)$cfg['pass'],
            (string)$cfg['db'],
            (int)$cfg['port']
        );
        if ($db->connect_error) {
            throw new RuntimeException('Database connection failed for ' . $cfg['db'] . ': ' . $db->connect_error);
        }
        $db->set_charset('utf8mb4');
        return $db;
    }
}
