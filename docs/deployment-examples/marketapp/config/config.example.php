<?php
return [
    'app' => [
        'name' => 'Tasia NGC MarketApp',
        'session_name' => 'tasia_marketapp_session',
        'admin_uuids' => [
            '[secrethere-uuid]',
        ],
    ],
    'wordpress' => [
        'host' => '127.0.0.1',
        'port' => 3306,
        'user' => '[secrethere]',
        'pass' => '[secrethere]',
        'db' => 'wordpress',
        'prefix' => 'wp_',
    ],
    'identity' => [
        'host' => '127.0.0.1',
        'port' => 3306,
        'user' => '[secrethere]',
        'pass' => '[secrethere]',
        'db' => 'robust',
    ],
    'money' => [
        'host' => '127.0.0.1',
        'port' => 3306,
        'user' => '[secrethere]',
        'pass' => '[secrethere]',
        'db' => 'money',
    ],
    'delivery' => [
        'api_url' => 'http://127.0.0.1:2023/send',
        'api_password' => '[secrethere]',
        'timeout_seconds' => 25,
    ],
    'texture' => [
        'proxy_source' => 'http://127.0.0.1:8002/index.php?method=GridTexture&uuid={uuid}',
    ],
    'dorito' => [
        'usd_to_dorito' => 256,
        'security_question' => 'Name of grid admin?',
        'security_answer' => '[secrethere]',
    ],
    'im' => [
        'robust_base_url' => 'http://127.0.0.1:8002',
        'from_uuid' => '00000000-0000-0000-0000-000000000000',
        'from_name' => 'I-Grid Security',
    ],
    'otp' => [
        'enabled' => true,
        'require_for_login' => true,
        'ttl_seconds' => 180,
        'message_prefix' => '[I-Grid OTP]',
    ],
    'auth_backup' => [
        'enabled' => false,
        'password' => '[secrethere]',
        'allowed_uuid' => '',
    ],
];
