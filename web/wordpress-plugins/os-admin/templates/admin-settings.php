<?php if (!defined('ABSPATH')) exit; ?>
<div class="wrap">
    <h1>OpenSim Settings</h1>
    <form method="post" action="options.php">
        <?php
        settings_fields('opensim_settings');
        $opensim_settings = get_option('opensim_db_settings');
        $money_settings = get_option('opensim_money_settings');
		$robust_settings = get_option('robust_db_settings');
        ?>
        
        <h2>OpenSim Database</h2>
        <table class="opensim-admin">
            <tr>
                <th><label for="opensim_host">Host</label></th>
                <td>
                    <input type="text" id="opensim_host" name="opensim_db_settings[host]" 
                           value="<?php echo esc_attr($opensim_settings['host'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
            <tr>
                <th><label for="opensim_db">Database</label></th>
                <td>
                    <input type="text" id="opensim_db" name="opensim_db_settings[database]" 
                           value="<?php echo esc_attr($opensim_settings['database'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
            <tr>
                <th><label for="opensim_user">Username</label></th>
                <td>
                    <input type="text" id="opensim_user" name="opensim_db_settings[user]" 
                           value="<?php echo esc_attr($opensim_settings['user'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
            <tr>
                <th><label for="opensim_pass">Password</label></th>
                <td>
                    <input type="password" id="opensim_pass" name="opensim_db_settings[password]" 
                           value="<?php echo esc_attr($opensim_settings['password'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
        </table>
         <h2>Robust Database</h2>
        <table class="opensim-admin">
            <tr>
                <th><label for="robust_host">Host</label></th>
                <td>
                    <input type="text" id="robust_host" name="robust_db_settings[host]" 
                           value="<?php echo esc_attr($robust_settings['host'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
            <tr>
                <th><label for="robust_db">Database</label></th>
                <td>
                    <input type="text" id="robust_db" name="robust_db_settings[database]" 
                           value="<?php echo esc_attr($robust_settings['database'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
            <tr>
                <th><label for="robust_user">Username</label></th>
                <td>
                    <input type="text" id="robust_user" name="robust_db_settings[user]" 
                           value="<?php echo esc_attr($robust_settings['user'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
            <tr>
                <th><label for="robust_pass">Password</label></th>
                <td>
                    <input type="password" id="robust_pass" name="robust_db_settings[password]" 
                           value="<?php echo esc_attr($robust_settings['password'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
        </table>
        <h2>Money Database</h2>
        <table class="opensim-admin">
            <tr>
                <th><label for="money_host">Host</label></th>
                <td>
                    <input type="text" id="money_host" name="opensim_money_settings[host]" 
                           value="<?php echo esc_attr($money_settings['host'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
            <tr>
                <th><label for="money_db">Database</label></th>
                <td>
                    <input type="text" id="money_db" name="opensim_money_settings[database]" 
                           value="<?php echo esc_attr($money_settings['database'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
            <tr>
                <th><label for="money_user">Username</label></th>
                <td>
                    <input type="text" id="money_user" name="opensim_money_settings[user]" 
                           value="<?php echo esc_attr($money_settings['user'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
            <tr>
                <th><label for="money_pass">Password</label></th>
                <td>
                    <input type="password" id="money_pass" name="opensim_money_settings[password]" 
                           value="<?php echo esc_attr($money_settings['password'] ?? ''); ?>" class="regular-text">
                </td>
            </tr>
        </table>
        
        <?php submit_button(); ?>
    </form>
</div>