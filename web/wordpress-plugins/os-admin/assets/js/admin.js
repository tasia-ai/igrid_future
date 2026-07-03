// assets/js/admin.js
jQuery(document).ready(function($) {
    // User search functionality
    var searchTimer;
    $('#user-search').on('input', function() {
        clearTimeout(searchTimer);
        var searchTerm = $(this).val();
        
        searchTimer = setTimeout(function() {
            $.ajax({
                url: opensimAjax.ajaxurl,
                type: 'POST',
                data: {
                    action: 'search_users',
                    nonce: opensimAjax.nonce,
                    term: searchTerm
                },
                success: function(response) {
                    if (response.success) {
                        $('#user-list').html(response.data);
                    }
                }
            });
        }, 500);
    });

    // Balance update handler
    $('.update-balance').on('click', function(e) {
        e.preventDefault();
        var uuid = $(this).data('uuid');
        var amount = prompt('Enter new balance:');
        
        if (amount !== null) {
            $.ajax({
                url: opensimAjax.ajaxurl,
                type: 'POST',
                data: {
                    action: 'update_user_balance',
                    nonce: opensimAjax.nonce,
                    uuid: uuid,
                    amount: amount
                },
                success: function(response) {
                    if (response.success) {
                        alert('Balance updated successfully');
                        location.reload();
                    } else {
                        alert('Error: ' + response.data);
                    }
                }
            });
        }
    });

    // Password change handler
    $('.change-password').on('click', function(e) {
        e.preventDefault();
        var uuid = $(this).data('uuid');
        var password = prompt('Enter new password:');
        
        if (password !== null) {
            $.ajax({
                url: opensimAjax.ajaxurl,
                type: 'POST',
                data: {
                    action: 'change_password',
                    nonce: opensimAjax.nonce,
                    uuid: uuid,
                    password: password
                },
                success: function(response) {
                    if (response.success) {
                        alert('Password changed successfully');
                    } else {
                        alert('Error: ' + response.data);
                    }
                }
            });
        }
    });
});