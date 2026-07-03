<?php
// Basic configuration scaffold for the OpenSim <-> WoWonder bridge.
// Adjust the environment variable names or defaults to suit your deployment.

return [
    'robust_base_url' => getenv('OPENSIM_ROBUST_URL') ?: 'https://grid.example.com',
    'client_id' => getenv('OPENSIM_CLIENT_ID') ?: 'replace-me',
    'client_secret' => getenv('OPENSIM_CLIENT_SECRET') ?: 'replace-me',
    'redirect_uri' => (isset($GLOBALS['wo']['config']['site_url'])
        ? $GLOBALS['wo']['config']['site_url'] . '/openim/callback'
        : 'https://your-wowonder-site.example.com/openim/callback'),
    'scopes' => 'im.write wallet.read wallet.write profile.read',
    'timeout' => 15,
];
