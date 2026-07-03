<?php
// Lightweight helper for calling the Robust OAuth and REST endpoints.
// Drop this file inside your WoWonder add-on and include it where needed.

class OpenSimClient
{
    private $config;

    public function __construct(array $config)
    {
        $this->config = $config;
    }

    public function getAuthorizeUrl(string $state, string $codeChallenge): string
    {
        $params = http_build_query([
            'response_type' => 'code',
            'client_id' => $this->config['client_id'],
            'redirect_uri' => $this->config['redirect_uri'],
            'scope' => $this->config['scopes'],
            'state' => $state,
            'code_challenge' => $codeChallenge,
            'code_challenge_method' => 'S256',
        ]);

        return rtrim($this->config['robust_base_url'], '/') . '/wowonder/oauth/authorize?' . $params;
    }

    public function exchangeCode(string $code, string $codeVerifier): array
    {
        $body = [
            'grant_type' => 'authorization_code',
            'code' => $code,
            'redirect_uri' => $this->config['redirect_uri'],
            'client_id' => $this->config['client_id'],
            'client_secret' => $this->config['client_secret'],
            'code_verifier' => $codeVerifier,
        ];

        return $this->postJson('/wowonder/oauth/token', $body);
    }

    public function refreshToken(string $refreshToken): array
    {
        $body = [
            'grant_type' => 'refresh_token',
            'refresh_token' => $refreshToken,
            'client_id' => $this->config['client_id'],
            'client_secret' => $this->config['client_secret'],
        ];

        return $this->postJson('/wowonder/oauth/token', $body);
    }

    public function getUserInfo(string $accessToken): array
    {
        return $this->getJson('/wowonder/oauth/userinfo', $accessToken);
    }

    public function getBalance(string $accessToken): array
    {
        return $this->getJson('/wowonder/money/balance', $accessToken);
    }

    public function sendInstantMessage(string $accessToken, array $payload): array
    {
        return $this->postJson('/wowonder/send_im', $payload, $accessToken);
    }

    private function getJson(string $path, string $accessToken): array
    {
        $url = rtrim($this->config['robust_base_url'], '/') . $path;
        $headers = [
            'Authorization: Bearer ' . $accessToken,
            'Accept: application/json',
        ];

        $response = $this->executeCurl($url, [
            CURLOPT_HTTPGET => true,
            CURLOPT_HTTPHEADER => $headers,
        ]);

        return json_decode($response, true) ?: [];
    }

    private function postJson(string $path, array $body, string $accessToken = null): array
    {
        $url = rtrim($this->config['robust_base_url'], '/') . $path;
        $headers = ['Content-Type: application/x-www-form-urlencoded'];

        if ($accessToken) {
            $headers[] = 'Authorization: Bearer ' . $accessToken;
        }

        $response = $this->executeCurl($url, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => http_build_query($body),
            CURLOPT_HTTPHEADER => $headers,
        ]);

        return json_decode($response, true) ?: [];
    }

    private function executeCurl(string $url, array $options): string
    {
        $ch = curl_init($url);
        $defaults = [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => $this->config['timeout'],
        ];

        foreach ($defaults as $key => $value) {
            curl_setopt($ch, $key, $value);
        }

        foreach ($options as $key => $value) {
            curl_setopt($ch, $key, $value);
        }

        $response = curl_exec($ch);
        $status = curl_getinfo($ch, CURLINFO_RESPONSE_CODE);

        if ($response === false || $status >= 400) {
            error_log('OpenSimClient request failed: ' . curl_error($ch) . ' (status ' . $status . ')');
            $response = json_encode(['error' => 'request_failed', 'status' => $status]);
        }

        curl_close($ch);

        return $response;
    }
}
