<?php

declare(strict_types=1);

$sourceUrl = 'https://hg.easierit.org/dst/proxy-iframe.php';
$categorySize = 9;
$requestCategory = trim((string)($_POST['request_categ'] ?? $_GET['request_categ'] ?? 'Page 1'));

function looks_host_port(string $token): bool
{
    $p = strrpos($token, ':');
    if ($p === false || $p <= 0) {
        return false;
    }
    return ctype_digit(substr($token, $p + 1));
}

function normalize_raw_destination(string $raw, string $label): string
{
    $raw = trim($raw);
    $label = trim($label);
    if ($raw === '') {
        return '';
    }

    if (preg_match('/^(.*?)=\s*<\s*([-+]?\d*\.?\d+)\s*,\s*([-+]?\d*\.?\d+)\s*,\s*([-+]?\d*\.?\d+)\s*>$/', $raw, $m)) {
        return trim($m[1]) . '/' . $m[2] . '/' . $m[3] . '/' . $m[4];
    }

    $parts = explode('/', $raw);
    if (count($parts) === 4) {
        $a = trim($parts[0]);
        $b = trim($parts[1]);
        $c = trim($parts[2]);
        $d = trim($parts[3]);

        if (substr_count($a, ':') >= 2) {
            return $a . '/' . $b . '/' . $c . '/' . $d;
        }

        if (preg_match('/^([^\s]+)\s+(.+)$/', $a, $m) && looks_host_port($m[1])) {
            return trim($m[1]) . ':' . trim($m[2]) . '/' . $b . '/' . $c . '/' . $d;
        }

        if (looks_host_port($a) && $label !== '') {
            return $a . ':' . $label . '/' . $b . '/' . $c . '/' . $d;
        }
    }

    if (count($parts) >= 5) {
        $base = 0;
        if (strpos(trim($parts[0]), ':') === false) {
            $base = 1;
        }
        if (isset($parts[$base + 3])) {
            $region = trim($parts[$base]);
            if (substr_count($region, ':') >= 2) {
                return $region
                    . '/' . trim($parts[$base + 1])
                    . '/' . trim($parts[$base + 2])
                    . '/' . trim($parts[$base + 3]);
            }
        }
    }

    if (substr_count($raw, ':') >= 2) {
        return $raw . '/128/128/25';
    }

    if (preg_match('/^([^\s]+)\s+(.+)$/', $raw, $m) && looks_host_port($m[1])) {
        return trim($m[1]) . ':' . trim($m[2]) . '/128/128/25';
    }

    if (looks_host_port($raw) && $label !== '') {
        return $raw . ':' . $label . '/128/128/25';
    }

    return '';
}

function fetch_source_rows(string $sourceUrl): array
{
    $context = stream_context_create([
        'http' => [
            'method' => 'GET',
            'timeout' => 20,
        ],
    ]);

    $raw = @file_get_contents($sourceUrl, false, $context);
    if ($raw === false) {
        return [];
    }

    $rows = [];
    if (preg_match_all('/<li>\s*(.*?)\s*<\/li>/is', $raw, $m)) {
        foreach ($m[1] as $item) {
            $row = html_entity_decode(trim((string)$item), ENT_QUOTES | ENT_HTML5, 'UTF-8');
            if ($row !== '') {
                $rows[] = $row;
            }
        }
    }

    return $rows;
}

function parse_page_from_category(string $requestCategory): int
{
    if (preg_match('/page\s+(\d+)/i', $requestCategory, $m)) {
        return max(1, (int)$m[1]);
    }
    return 1;
}

$rows = fetch_source_rows($sourceUrl);
$page = parse_page_from_category($requestCategory);
$offset = ($page - 1) * $categorySize;
$slice = array_slice($rows, $offset, $categorySize);

$out = [];
foreach ($slice as $row) {
    $label = '';
    $destRaw = $row;
    $sep = strpos($row, '|');
    if ($sep !== false) {
        $label = trim(substr($row, 0, $sep));
        $destRaw = trim(substr($row, $sep + 1));
    }

    $normalized = normalize_raw_destination($destRaw, $label);
    if ($normalized === '') {
        continue;
    }

    if ($label === '') {
        $parts = explode(':', explode('/', $normalized)[0]);
        $label = urldecode(trim((string)end($parts)));
    }

    $out[] = $label . '|' . $normalized;
}

header('Content-Type: text/plain; charset=utf-8');
echo implode('&', $out);
