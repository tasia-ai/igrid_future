jQuery(document).ready(function ($) {
    $('#change-password-form').on('submit', function (e) {
        e.preventDefault();
        var password = $('#new-password').val();

        $.post(opensimAjax.ajaxurl, {
            action: 'change_password',
            nonce: opensimAjax.nonce,
            uuid: opensimAjax.uuid, // we'll define this in PHP
            password: password
        }, function (response) {
            $('#change-password-status').text(
                response.success ? 'Password updated!' : 'Failed: ' + response.data
            );
        });
    });
});