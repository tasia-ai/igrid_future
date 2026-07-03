<?php
declare(strict_types=1);

require dirname(__DIR__) . '/lib.php';

$cfg = iauth_config();
$issuer = $cfg['oidc_issuer'];

header('Content-Type: application/json; charset=utf-8');
echo json_encode([
    'issuer' => $issuer,
    'authorization_endpoint' => $issuer . '/authorize',
    'token_endpoint' => $issuer . '/token',
    'userinfo_endpoint' => $issuer . '/userinfo',
    'jwks_uri' => $issuer . '/jwks',
    'response_types_supported' => ['code'],
    'subject_types_supported' => ['public'],
    'id_token_signing_alg_values_supported' => ['RS256'],
    'token_endpoint_auth_methods_supported' => ['client_secret_basic', 'client_secret_post'],
    'scopes_supported' => ['openid', 'profile', 'email', 'groups', 'role'],
    'claims_supported' => ['sub', 'preferred_username', 'username', 'email', 'upn', 'name', 'given_name', 'family_name', 'avatar_uuid', 'groups', 'role'],
], JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT);
