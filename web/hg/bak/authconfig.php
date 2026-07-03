<?php
// Github: https://github.com/cuga-rajal/hgauth

$db_server = 'i.let-us.cyou';
$db_name = 'wordpress';
$db_user = 'root';
$db_pass = 'CHANGE_ME_DB_PASSWORD';
$tablename = "wp_hgauth"; //this is the table name that will store your authorizations.

$authlink = "https://i.let-us.cyou/hg/index.php"; //name of the page your users will use to submit consent


function base64_url_encode($input) {
    return strtr(base64_encode(str_rot13($input)), '+/=', '-_,');
}

function base64_url_decode($input) {
    return str_rot13(base64_decode(strtr($input, '-_,', '+/=')));
}


?>