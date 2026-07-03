<?php if (!defined('ABSPATH')) exit; ?>
<style>
    /* General Admin Page Styling */
.opensim-admin {
    font-family: Arial, sans-serif;
    background: .f9f9f9;
    padding: 20px;
    border-radius: 8px;
}

/* Headings */
.opensim-admin h2 {
    color: .333;
    border-bottom: 2px solid .0073aa;
    padding-bottom: 5px;
    margin-bottom: 15px;
}

/* Tables */
.opensim-admin table {
    width: 100%;
    border-collapse: collapse;
    background: white;
    border-radius: 6px;
    overflow: hidden;
    box-shadow: 0 2px 5px rgba(0, 0, 0, 0.1);
}

.opensim-admin th, .opensim-admin td {
    padding: 10px;
    border-bottom: 1px solid .ddd;
    text-align: left;
}

.opensim-admin th {
    background: .0073aa;
    color: white;
    text-transform: uppercase;
}

.opensim-admin tr:nth-child(even) {
    background: .f2f2f2;
}

/* Buttons */
.opensim-btn {
    background: .0073aa;
    color: white;
    padding: 8px 15px;
    border: none;
    border-radius: 4px;
    cursor: pointer;
    transition: 0.3s;
}

.opensim-btn:hover {
    background: .005177;
}

/* Forms */
.opensim-admin input[type="text"],
.opensim-admin input[type="number"],
.opensim-admin select {
    width: 100%;
    padding: 8px;
    margin-bottom: 10px;
    border: 1px solid .ccc;
    border-radius: 4px;
}

/* Status Labels */
.status-success {
    color: green;
    font-weight: bold;
}

.status-failed {
    color: red;
    font-weight: bold;
}
</style>
<div class="opensim-admin">
    <h3>Your Balance</h3>
    <div class="opensim-admin">
        <?php echo esc_html($balance); ?> Dorito$
    </div>
<?php if (current_user_can('manage_options')): ?>
    <div class="admin-controls">
   <h3>Change OpenSim Password</h3>
<form id="opensim-change-password-form">
    <label for="opensim-new-password">New Password:</label><br>
    <input type="password" id="opensim-new-password" name="password" required><br>
    <button type="submit" class="button">Change Password</button>
    <div id="opensim-password-message"></div>
</form>
<script>
jQuery(document).ready(function($) {
    $('#opensim-change-password-form').on('submit', function(e) {
        e.preventDefault();
        var password = $('#opensim-new-password').val();

        $.post(opensimAjax.ajaxurl, {
            action: 'change_password',
            nonce: opensimAjax.nonce,
            uuid: '<?php echo esc_js($uuid); ?>',
            password: password
        }, function(response) {
            if (response.success) {
                $('#opensim-password-message').text('Password changed successfully!');
            } else {
                $('#opensim-password-message').text('Error: ' + response.data);
            }
        });
    });
});
</script>

    
        <button class="button update-own-balance">Update Balance</button>
    </div>
    <?php endif; ?>
</div>