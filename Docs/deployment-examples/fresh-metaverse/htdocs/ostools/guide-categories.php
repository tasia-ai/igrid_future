<?php

declare(strict_types=1);

$sourceUrl = 'https://hg.easierit.org/dst/proxy-iframe.php';
$categorySize = 9;

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

$rows = fetch_source_rows($sourceUrl);
$total = count($rows);
$pages = ($total === 0) ? 1 : (int)ceil($total / $categorySize);

$labels = [];
for ($i = 1; $i <= $pages; ++$i) {
    $labels[] = 'Page ' . $i;
}

header('Content-Type: text/plain; charset=utf-8');
echo implode('&', $labels);
