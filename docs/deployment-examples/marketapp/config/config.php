<?php
return [
    'app' => [
        'name' => 'Tasia NGC MarketApp',
        'session_name' => 'tasia_marketapp_session',
        'admin_uuids' => [
            '43827618-1993-43d8-bf6b-fc966a943381',
        ],
    ],
    'wordpress' => [
        'host' => 'i.let-us.cyou',
        'port' => 3306,
        'user' => 'root',
        'pass' => 'CHANGE_ME_DB_PASSWORD',
        'db' => 'wordpress',
        'prefix' => 'wp_',
    ],
    'identity' => [
        'host' => 'i.let-us.cyou',
        'port' => 3306,
        'user' => 'root',
        'pass' => 'CHANGE_ME_DB_PASSWORD',
        'db' => 'robust',
    ],
    'money' => [
        'host' => 'i.let-us.cyou',
        'port' => 3306,
        'user' => 'root',
        'pass' => 'CHANGE_ME_DB_PASSWORD',
        'db' => 'money',
    ],
    'delivery' => [
        'api_url' => 'http://i.let-us.cyou:2023/send',
        'api_password' => 'martyadmin',
        'timeout_seconds' => 25,
    ],
    'texture' => [
        'proxy_source' => 'http://i.let-us.cyou:8002/index.php?method=GridTexture&uuid={uuid}',
    ],
    'dorito' => [
        'usd_to_dorito' => 256,
        'security_question' => 'Name of grid admin?',
        'security_answer' => 'marty',
    ],
    'im' => [
        'robust_base_url' => 'http://i.let-us.cyou:8002',
        'from_uuid' => '00000000-0000-0000-0000-000000000000',
        'from_name' => 'I-Grid Security',
    ],
    'otp' => [
        'enabled' => true,
        'require_for_login' => true,
        'ttl_seconds' => 180,
        'message_prefix' => '[I-Grid OTP]',
    ],
    'iauth' => [
        'authorize_url' => 'https://i.let-us.cyou/i-auth/oidc/authorize.php',
        'token_url' => 'https://i.let-us.cyou/i-auth/oidc/token.php',
        'userinfo_url' => 'https://i.let-us.cyou/i-auth/oidc/userinfo.php',
        'client_id' => 'marty-auth-broker',
        'client_secret' => 'CHANGE_ME_OIDC_CLIENT_SECRET',
        'scope' => 'openid profile email groups role',
        'market_callback_url' => '',
    ],
    'auth_backup' => [
        'enabled' => true,
        'password' => 'marty',
        'allowed_uuid' => '',
    ],
];
