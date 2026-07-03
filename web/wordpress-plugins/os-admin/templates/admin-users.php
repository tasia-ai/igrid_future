<?php if (!defined('ABSPATH')) exit; ?>
    
    <style>
    .table-xp {
    width: 100%;
    border-collapse: collapse;
    font-family: Tahoma, sans-serif;
    font-size: 12px;
}

.table-xp th, .table-xp td {
    border: 1px solid #A5A5A5;
    padding: 4px 8px;
    text-align: left;
}

.table-xp th {
    background: linear-gradient(to bottom, #D4D0C8, #B6B2AA);
    color: black;
    font-weight: bold;
    border-bottom: 2px solid #808080;
}

.table-xp tr:nth-child(even) {
    background-color: #E8E8E8;
}

.table-xp tr:hover {
    background-color: #C3D9FF;
}

.table-xp input {
    border: 1px inset #808080;
    padding: 2px;
    background-color: white;
    font-size: 12px;
    font-family: Tahoma, sans-serif;
}

.table-xp select {
    border: 1px solid #808080;
    padding: 2px;
    background-color: white;
    font-size: 12px;
    font-family: Tahoma, sans-serif;
}
</style>
    <div class="wrap">
    <h1>User Management</h1>
   <!-- <div class="tablenav top">
    <div class="alignleft actions">
    <input type="text" id="user-search" placeholder="Search users...">
    </div> -->
    </div>
    <!-- <table class="wp-list-table widefat fixed striped"> -->
     <table class="table-xp">
        <thead>
            <tr>
                <th>Username</th>
                <th>UUID</th>
                <th>Balance</th>
                <th>Last Login</th>
                <th>Actions</th>
            </tr>
        </thead>
        <tbody>
            <?php
            if ($this->opensim_db) {
                $users = $this->opensim_db->get_results("SELECT * FROM UserAccounts ORDER BY Created DESC");
                foreach ($users as $user) {
                    $balance = $this->get_user_balance($user->PrincipalID);
                    ?>
                    <tr>
                        <td><?php echo esc_html($user->FirstName . ' ' . $user->LastName); ?></td>
                        <td><?php echo esc_html($user->PrincipalID); ?></td>
                        <td><?php echo esc_html($balance); ?></td>
                       <!-- <td><?php echo esc_html($user->LastLogin); ?></td>-->
                        <td>
                            <button class="button update-balance" data-uuid="<?php echo esc_attr($user->PrincipalID); ?>">
                                Update Balance
                            </button>
                            <button class="button change-password" data-uuid="<?php echo esc_attr($user->PrincipalID); ?>">
                                Change Password
                            </button>
                        </td>
                    </tr>
                    <?php
                }
            }
            ?>
        </tbody>
    </table>
</div>