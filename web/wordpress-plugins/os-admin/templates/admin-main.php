<?php if (!defined('ABSPATH')) exit; ?>
<link rel="stylesheet" href="../assets/css/style.css">
<div class="wrap">
    <h1>OpenSim Grid Manager</h1>
    <div class="opensim-admin">
        <div class="opensim-admin">
            <h2>Grid Statistics</h2>
            <ul>
                <li>Total Users: <?php echo esc_html($this->get_total_users()); ?></li>
                <li>Online Users: <?php echo esc_html($this->get_online_users()); ?></li>
                <li>Total Regions: <?php echo esc_html($this->get_total_regions()); ?></li>
            </ul>
        </div>
    </div>
</div>