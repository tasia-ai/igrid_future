<?php
// Minimal installer hook to register database columns WoWonder can use to
// store OpenSim linkage data.

if (!function_exists('Wo_Db')) {
    die('WoWonder database helper missing.');
}

$db = Wo_Db();

$columns = [
    "ALTER TABLE `" . T_USERS . "` ADD `opensim_avatar` VARCHAR(36) NULL AFTER `email`, ADD `opensim_balance` BIGINT NULL AFTER `opensim_avatar`, ADD `opensim_access_token` TEXT NULL AFTER `opensim_balance`, ADD `opensim_refresh_token` TEXT NULL AFTER `opensim_access_token`",
];

foreach ($columns as $sql) {
    try {
        $db->rawQuery($sql);
    } catch (Exception $e) {
        // Ignore duplicate column errors when reinstalling.
    }
}
