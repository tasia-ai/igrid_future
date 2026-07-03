<?php
/*
Plugin Name: Grid Endpoints Settings Manager
Description: Allow to set settings of endoints for grids they will land on baseurl/connect/filename
Version: 1.1
Author: Tasia for Marty
*/

add_action('admin_menu', function() {
    add_menu_page('Grid Endpoints', 'Grid Endpoints', 'manage_options', 'multi-grid-info', 'multi_grid_info_options_page');
});

add_action('admin_init', function() {
    register_setting('multi_grid_info_settings', 'multi_grid_info_data');
    add_settings_section('multi_grid_info_section', 'Grid Entries', null, 'multi_grid_info');

    add_settings_field('multi_grid_info_grids', 'Grid Entries', function() {
        $option = get_option('multi_grid_info_data');
        $grids = isset($option['grids']) && is_array($option['grids']) ? $option['grids'] : [];
        ?>
        <div id="grid-entries">
            <?php foreach ($grids as $index => $grid) { ?>
                <div class="grid-entry" style="border:1px solid #ccc;padding:10px;margin-bottom:10px;">
                    <h4>Grid <?= $index+1 ?></h4>
                    <?php foreach ([
                        'filename'=>'Filename (np. mygrid.xml)',
                        'gridname'=>'Grid Name',
                        'gridnick'=>'Grid Nick',
                        'loginuri'=>'Login URI',
                        'loginpage'=>'Login Page',
                        'helperuri'=>'Helper URI',
                        'website'=>'Website URL',
                        'support'=>'Support URL',
                        'about'=>'About URL',
                        'register'=>'Register URL',
                        'password'=>'Password URL',
                        'search'=>'Search URL',
                        'message'=>'Message URL'
                    ] as $field => $label) {
                        $value = isset($grid[$field]) ? $grid[$field] : '';
                        ?>
                        <p><label><?= esc_html($label) ?><br>
                        <input type="text" name="multi_grid_info_data[grids][<?= $index ?>][<?= esc_attr($field) ?>]" value="<?= esc_attr($value) ?>" size="50"></label></p>
                    <?php } ?>
                    <button type="button" onclick="this.closest('.grid-entry').remove()">Remove Grid</button>
                </div>
            <?php } ?>
        </div>
        <button type="button" class="button" onclick="addGrid()">Add Grid</button>
        <script>
            function addGrid() {
                const container = document.getElementById('grid-entries');
                const count = container.querySelectorAll('.grid-entry').length;
                const fields = ['filename','gridname','gridnick','loginuri','loginpage','helperuri','website','support','about','register','password','search','message'];
                let html = '<div class="grid-entry" style="border:1px solid #ccc;padding:10px;margin-bottom:10px;">';
                html += '<h4>Grid ' + (count+1) + '</h4>';
                fields.forEach(function(field) {
                    html += '<p><label>' + field.charAt(0).toUpperCase() + field.slice(1) + '<br>';
                    html += '<input type="text" name="multi_grid_info_data[grids][' + count + '][' + field + ']" size="50"></label></p>';
                });
                html += '<button type="button" onclick="this.closest(\'.grid-entry\').remove()">Remove Grid</button>';
                html += '</div>';
                container.insertAdjacentHTML('beforeend', html);
            }
        </script>
        <?php
    }, 'multi_grid_info', 'multi_grid_info_section');
});

// Generuj pliki XML przy zapisie opcji
add_action('update_option_multi_grid_info_data', function($old_value, $value, $option) {
    $upload_dir = wp_upload_dir();
    $dir = '/var/www/html/web/connect';
    if (!file_exists($dir)) mkdir($dir, 0777, true);

    if (isset($value['grids']) && is_array($value['grids'])) {
        foreach ($value['grids'] as $grid) {
            $filename = sanitize_file_name($grid['filename']);
            $xml = "<?xml version=\"1.0\"?>\n<gridinfo>\n";
            foreach ($grid as $key => $val) {
                if ($key !== 'filename') {
                    $xml .= "  <{$key}>" . esc_html($val) . "</{$key}>\n";
                }
            }
            $xml .= "</gridinfo>\n";
            file_put_contents("$dir/$filename", $xml);
        }
    }
}, 10, 3);

// Wyświetl panel ustawień
function multi_grid_info_options_page() {
    ?>
    <div class="wrap">
        <h1>Multi Grid Info</h1>
        <form method="post" action="options.php">
            <?php
            settings_fields('multi_grid_info_settings');
            do_settings_sections('multi_grid_info');
            submit_button();
            ?>
        </form>
        <h2>Linki do XML</h2>
        <ul>
        <?php
        $upload_dir = wp_upload_dir();
        $dir = '/var/www/html/web/connect';
        $url = 'https://i.let-us.cyou/connect';
        if (file_exists($dir)) {
            foreach (glob("$dir/*.xml") as $file) {
                $name = basename($file);
                echo "<li><a href='$url/$name' target='_blank'>$name</a></li>";
            }
        } else {
            echo "<li>Brak plików XML</li>";
        }
        ?>
        </ul>
    </div>
    <?php
}
?>