<?php
/**
 * Plugin Name: OpenSim Chat Audit Viewer
 * Description: Displays chat and instant-message audit logs collected by the OpenSim ChatAudit addon, with support for multiple endpoints.
 * Version: 1.2.0
 * Author: Tranquillity Grid
 * License: MIT
 */

if (!defined('ABSPATH')) {
    exit;
}

class OpenSimChatAuditPlugin
{
    const OPTION_GROUP = 'opensim_chat_audit';
    const OPTION_NAME  = 'opensim_chat_audit_settings';
    const NONCE_ACTION = 'opensim_chat_audit_refresh';

    public function __construct()
    {
        add_action('admin_menu', array($this, 'registerAdminMenu'));
        add_action('admin_init', array($this, 'registerSettings'));
        add_shortcode('opensim_chat_audit', array($this, 'renderShortcode'));
        add_action('admin_post_opensim_chat_audit_refresh', array($this, 'handleAdminRefresh'));
    }

    public static function activate()
    {
        if (!get_option(self::OPTION_NAME)) {
            add_option(self::OPTION_NAME, array(
                // Multi-endpoint storage
                'endpoints'             => array(), // each: ['label', 'endpoint', 'token']
                'active_endpoint_index' => 0,
                'limit'                 => 100,
                'event_type'            => '',
            ));
        }
    }

    public function registerSettings()
    {
        register_setting(self::OPTION_GROUP, self::OPTION_NAME, array(
            'type'              => 'array',
            'sanitize_callback' => array($this, 'sanitizeSettings'),
            'default'           => array(
                'endpoints'             => array(),
                'active_endpoint_index' => 0,
                'limit'                 => 100,
                'event_type'            => '',
            ),
        ));

        add_settings_section(
            'opensim_chat_audit_main',
            __('Chat Audit API', 'opensim-chat-audit'),
            function () {
                echo '<p>' . esc_html__(
                    'Configure one or more ChatAudit endpoints exposed by the OpenSim region servers.',
                    'opensim-chat-audit'
                ) . '</p>';
            },
            self::OPTION_GROUP
        );

        add_settings_field(
            'endpoints',
            __('Endpoints', 'opensim-chat-audit'),
            array($this, 'renderEndpointsField'),
            self::OPTION_GROUP,
            'opensim_chat_audit_main'
        );

        add_settings_field(
            'active_endpoint',
            __('Active Endpoint', 'opensim-chat-audit'),
            array($this, 'renderActiveEndpointField'),
            self::OPTION_GROUP,
            'opensim_chat_audit_main'
        );

        add_settings_field(
            'limit',
            __('Max Records', 'opensim-chat-audit'),
            array($this, 'renderLimitField'),
            self::OPTION_GROUP,
            'opensim_chat_audit_main'
        );

        add_settings_field(
            'event_type',
            __('Event Filter', 'opensim-chat-audit'),
            array($this, 'renderEventTypeField'),
            self::OPTION_GROUP,
            'opensim_chat_audit_main'
        );
    }

    public function registerAdminMenu()
    {
        add_menu_page(
            __('OpenSim Chat Logs', 'opensim-chat-audit'),
            __('OpenSim Chat Logs', 'opensim-chat-audit'),
            'manage_options',
            'opensim-chat-audit',
            array($this, 'renderAdminPage'),
            'dashicons-format-chat',
            81
        );
    }

    public function renderAdminPage()
    {
        if (!current_user_can('manage_options')) {
            wp_die(__('You do not have permission to view this page.', 'opensim-chat-audit'));
        }

        $settings = $this->getSettings();
        $records  = null;

        // Only fetch logs if explicitly requested (?refreshed=1)
        if (isset($_GET['refreshed']) && intval($_GET['refreshed']) === 1) {
            $records = $this->fetchRecords($settings);
        }

        echo '<div class="wrap">';
        echo '<h1>' . esc_html__('OpenSim Chat Audit', 'opensim-chat-audit') . '</h1>';

        echo '<form action="options.php" method="post">';
        settings_fields(self::OPTION_GROUP);
        do_settings_sections(self::OPTION_GROUP);
        submit_button(__('Save Changes', 'opensim-chat-audit'));
        echo '</form>';

        // Refresh section
        $refresh_url = admin_url('admin-post.php');
        echo '<hr />';
        echo '<h2>' . esc_html__('Logs', 'opensim-chat-audit') . '</h2>';
        echo '<form method="post" action="' . esc_url($refresh_url) . '">';
        echo '<input type="hidden" name="action" value="opensim_chat_audit_refresh" />';
        wp_nonce_field(self::NONCE_ACTION);
        submit_button(__('Refresh Logs', 'opensim-chat-audit'));
        echo '</form>';

        if ($records !== null) {
            if (is_wp_error($records)) {
                echo '<div class="notice notice-error"><p>' . esc_html($records->get_error_message()) . '</p></div>';
            } else {
                $this->renderTable($records);
            }
        } else {
            echo '<p>' . esc_html__(
                'Click "Refresh Logs" to load the latest records from the active endpoint.',
                'opensim-chat-audit'
            ) . '</p>';
        }

        echo '</div>';
    }

    public function renderShortcode($atts)
    {
        if (!current_user_can('manage_options')) {
            return __('You do not have permission to view the OpenSim chat log.', 'opensim-chat-audit');
        }

        $settings = $this->getSettings();
        $records  = $this->fetchRecords($settings);
        if (is_wp_error($records)) {
            return '<div class="opensim-chat-audit-error">' . esc_html($records->get_error_message()) . '</div>';
        }

        ob_start();
        $this->renderTable($records);
        return ob_get_clean();
    }

    public function handleAdminRefresh()
    {
        if (!current_user_can('manage_options')) {
            wp_die(__('You do not have permission to perform this action.', 'opensim-chat-audit'));
        }

        check_admin_referer(self::NONCE_ACTION);
        wp_safe_redirect(admin_url('admin.php?page=opensim-chat-audit&refreshed=1'));
        exit;
    }

    /**
     * SETTINGS HELPERS
     */

    private function getSettings()
    {
        $defaults = array(
            'endpoints'             => array(),
            'active_endpoint_index' => 0,
            'limit'                 => 100,
            'event_type'            => '',
        );

        $settings = get_option(self::OPTION_NAME, $defaults);

        if (!is_array($settings)) {
            $settings = $defaults;
        }

        // Backwards compatibility: migrate old single endpoint if present
        if (
            empty($settings['endpoints']) &&
            (isset($settings['endpoint']) && !empty($settings['endpoint']))
        ) {
            $label = __('Default', 'opensim-chat-audit');
            $token = isset($settings['token']) ? $settings['token'] : '';

            $settings['endpoints'] = array(
                array(
                    'label'    => $label,
                    'endpoint' => $settings['endpoint'],
                    'token'    => $token,
                ),
            );
            $settings['active_endpoint_index'] = 0;
        }

        if (!isset($settings['endpoints']) || !is_array($settings['endpoints'])) {
            $settings['endpoints'] = array();
        }

        if (!isset($settings['active_endpoint_index'])) {
            $settings['active_endpoint_index'] = 0;
        }

        return wp_parse_args($settings, $defaults);
    }

    public function sanitizeSettings($input)
    {
        $sanitized = array();

        // Endpoints list (table rows)
        $sanitized['endpoints'] = array();
        if (isset($input['endpoints']) && is_array($input['endpoints'])) {
            foreach ($input['endpoints'] as $endpoint) {
                $label = isset($endpoint['label']) ? sanitize_text_field($endpoint['label']) : '';
                $url   = isset($endpoint['endpoint']) ? esc_url_raw(trim($endpoint['endpoint'])) : '';
                $token = isset($endpoint['token']) ? sanitize_text_field($endpoint['token']) : '';

                // Only keep rows with a URL
                if (!empty($url)) {
                    $sanitized['endpoints'][] = array(
                        'label'    => $label,
                        'endpoint' => $url,
                        'token'    => $token,
                    );
                }
            }
        }

        // Active endpoint index
        $active_index = isset($input['active_endpoint_index']) ? intval($input['active_endpoint_index']) : 0;
        $count        = count($sanitized['endpoints']);
        if ($count === 0) {
            $active_index = 0;
        } else {
            if ($active_index < 0) {
                $active_index = 0;
            }
            if ($active_index >= $count) {
                $active_index = $count - 1;
            }
        }
        $sanitized['active_endpoint_index'] = $active_index;

        // Limit & event_type
        $sanitized['limit']      = isset($input['limit']) ? max(1, min(500, intval($input['limit']))) : 100;
        $sanitized['event_type'] = isset($input['event_type']) ? sanitize_text_field($input['event_type']) : '';

        return $sanitized;
    }

    private function getActiveEndpoint($settings)
    {
        if (
            !isset($settings['endpoints']) ||
            !is_array($settings['endpoints']) ||
            empty($settings['endpoints'])
        ) {
            return null;
        }

        $index = isset($settings['active_endpoint_index']) ? intval($settings['active_endpoint_index']) : 0;
        if (!isset($settings['endpoints'][$index])) {
            $index = 0;
        }

        return $settings['endpoints'][$index];
    }

    /**
     * DATA FETCHING
     */

    private function fetchRecords($settings)
    {
        $endpointConfig = $this->getActiveEndpoint($settings);
        if ($endpointConfig === null || empty($endpointConfig['endpoint'])) {
            return new WP_Error(
                'opensim_chat_audit_missing_endpoint',
                __('Configure at least one endpoint and select an active endpoint before fetching logs.', 'opensim-chat-audit')
            );
        }

        $url  = $endpointConfig['endpoint'];
        $args = array(
            'timeout' => 10,
            'headers' => array(),
        );

        if (!empty($endpointConfig['token'])) {
            $args['headers']['X-Chat-Audit-Token'] = $endpointConfig['token'];
        }

        $query = array();

        if (!empty($settings['limit'])) {
            $query['limit'] = intval($settings['limit']);
        }

        if (!empty($settings['event_type'])) {
            $query['type'] = $settings['event_type'];
        }

        if (!empty($query)) {
            $separator = (false === strpos($url, '?')) ? '?' : '&';
            $url      .= $separator . http_build_query($query, '', '&', PHP_QUERY_RFC3986);
        }

        $response = wp_remote_get($url, $args);
        if (is_wp_error($response)) {
            return $response;
        }

        $code = wp_remote_retrieve_response_code($response);
        if ($code !== 200) {
            $message = wp_remote_retrieve_body($response);
            if (empty($message)) {
                $message = sprintf(__('Unexpected response status: %d', 'opensim-chat-audit'), $code);
            }
            return new WP_Error('opensim_chat_audit_http_error', $message, array('status' => $code));
        }

        $body = wp_remote_retrieve_body($response);

        // safety guard against huge responses
        if (strlen($body) > 5 * 1024 * 1024) { // 5 MB
            return new WP_Error(
                'opensim_chat_audit_body_too_large',
                __('Response from OpenSim is too large.', 'opensim-chat-audit')
            );
        }

        $data = json_decode($body, true);
        if (!is_array($data) || !isset($data['records']) || !is_array($data['records'])) {
            return new WP_Error(
                'opensim_chat_audit_invalid_body',
                __('Invalid JSON structure received from OpenSim.', 'opensim-chat-audit')
            );
        }

        return $data['records'];
    }

    /**
     * TABLE RENDERING
     */

    private function renderTable($records)
    {
        echo '<table class="widefat fixed striped">';
        echo '<thead><tr>';
        echo '<th>' . esc_html__('Timestamp', 'opensim-chat-audit') . '</th>';
        echo '<th>' . esc_html__('Type', 'opensim-chat-audit') . '</th>';
        echo '<th>' . esc_html__('Region', 'opensim-chat-audit') . '</th>';
        echo '<th>' . esc_html__('From', 'opensim-chat-audit') . '</th>';
        echo '<th>' . esc_html__('Target', 'opensim-chat-audit') . '</th>';
        echo '<th>' . esc_html__('Message', 'opensim-chat-audit') . '</th>';
        echo '</tr></thead>';
        echo '<tbody>';

        if (empty($records)) {
            echo '<tr><td colspan="6">' . esc_html__('No records available.', 'opensim-chat-audit') . '</td></tr>';
        } else {
            foreach ($records as $record) {
                $timestamp = isset($record['timestamp']) ? esc_html($this->formatTimestamp($record['timestamp'])) : '';
                $type      = isset($record['eventType']) ? esc_html($record['eventType']) : '';
                $region    = isset($record['region']) ? esc_html($record['region']) : '';
                $from      = isset($record['fromName']) ? esc_html($record['fromName']) : '';
                $target    = isset($record['target']) ? esc_html($record['target']) : '';
                $message   = isset($record['message']) ? esc_html($record['message']) : '';

                echo '<tr>';
                echo '<td>' . $timestamp . '</td>';
                echo '<td>' . $type . '</td>';
                echo '<td>' . $region . '</td>';
                echo '<td>' . $from . '</td>';
                echo '<td>' . $target . '</td>';
                echo '<td>' . $message . '</td>';
                echo '</tr>';
            }
        }

        echo '</tbody>';
        echo '</table>';
    }

    private function formatTimestamp($timestamp)
    {
        if (empty($timestamp)) {
            return '';
        }

        try {
            $dt = new DateTime($timestamp, new DateTimeZone('UTC'));

            if (function_exists('wp_timezone')) {
                $dt->setTimezone(wp_timezone());
            } else {
                $tz_string = get_option('timezone_string');
                if (!$tz_string) {
                    $tz_string = 'UTC';
                }
                $dt->setTimezone(new DateTimeZone($tz_string));
            }

            return $dt->format(get_option('date_format') . ' ' . get_option('time_format'));
        } catch (Exception $ex) {
            return sanitize_text_field($timestamp);
        }
    }

    /**
     * SETTINGS FIELDS RENDERING
     */

    public function renderEndpointsField()
    {
        $settings  = $this->getSettings();
        $endpoints = $settings['endpoints'];

        // Ensure at least one row so user can add
        if (empty($endpoints)) {
            $endpoints = array(
                array(
                    'label'    => '',
                    'endpoint' => '',
                    'token'    => '',
                ),
            );
        }

        echo '<table class="widefat striped">';
        echo '<thead><tr>';
        echo '<th>' . esc_html__('Label', 'opensim-chat-audit') . '</th>';
        echo '<th>' . esc_html__('Endpoint URL', 'opensim-chat-audit') . '</th>';
        echo '<th>' . esc_html__('API Token', 'opensim-chat-audit') . '</th>';
        echo '</tr></thead>';
        echo '<tbody>';

        foreach ($endpoints as $index => $endpoint) {
            $label = isset($endpoint['label']) ? $endpoint['label'] : '';
            $url   = isset($endpoint['endpoint']) ? $endpoint['endpoint'] : '';
            $token = isset($endpoint['token']) ? $endpoint['token'] : '';

            echo '<tr>';
            printf(
                '<td><input type="text" class="regular-text" name="%1$s[endpoints][%2$d][label]" value="%3$s" placeholder="%4$s" /></td>',
                esc_attr(self::OPTION_NAME),
                intval($index),
                esc_attr($label),
                esc_attr__('Main Grid', 'opensim-chat-audit')
            );
            printf(
                '<td><input type="url" class="regular-text" name="%1$s[endpoints][%2$d][endpoint]" value="%3$s" placeholder="https://grid.example.com/addons/chat-audit" /></td>',
                esc_attr(self::OPTION_NAME),
                intval($index),
                esc_attr($url)
            );
            printf(
                '<td><input type="text" class="regular-text" name="%1$s[endpoints][%2$d][token]" value="%3$s" autocomplete="off" /></td>',
                esc_attr(self::OPTION_NAME),
                intval($index),
                esc_attr($token)
            );
            echo '</tr>';
        }

        // Extra blank row to add another endpoint
        $new_index = count($endpoints);
        echo '<tr>';
        printf(
            '<td><input type="text" class="regular-text" name="%1$s[endpoints][%2$d][label]" value="" placeholder="%3$s" /></td>',
            esc_attr(self::OPTION_NAME),
            intval($new_index),
            esc_attr__('New Endpoint', 'opensim-chat-audit')
        );
        printf(
            '<td><input type="url" class="regular-text" name="%1$s[endpoints][%2$d][endpoint]" value="" placeholder="https://grid.example.com/addons/chat-audit" /></td>',
            esc_attr(self::OPTION_NAME),
            intval($new_index)
        );
        printf(
            '<td><input type="text" class="regular-text" name="%1$s[endpoints][%2$d][token]" value="" autocomplete="off" /></td>',
            esc_attr(self::OPTION_NAME),
            intval($new_index)
        );
        echo '</tr>';

        echo '</tbody>';
        echo '</table>';

        echo '<p class="description">' . esc_html__(
            'Add one or more endpoints. Rows without a URL will be ignored on save.',
            'opensim-chat-audit'
        ) . '</p>';
    }

    public function renderActiveEndpointField()
    {
        $settings  = $this->getSettings();
        $endpoints = $settings['endpoints'];
        $active    = isset($settings['active_endpoint_index']) ? intval($settings['active_endpoint_index']) : 0;

        if (empty($endpoints)) {
            echo '<p>' . esc_html__(
                'No endpoints defined yet. Add at least one endpoint above and save changes.',
                'opensim-chat-audit'
            ) . '</p>';
            return;
        }

        echo '<select name="' . esc_attr(self::OPTION_NAME) . '[active_endpoint_index]">';

        foreach ($endpoints as $index => $endpoint) {
            $label = !empty($endpoint['label'])
                ? $endpoint['label']
                : sprintf(__('Endpoint #%d', 'opensim-chat-audit'), $index + 1);

            $url  = isset($endpoint['endpoint']) ? $endpoint['endpoint'] : '';
            $text = $label;
            if (!empty($url)) {
                $text .= ' (' . $url . ')';
            }

            printf(
                '<option value="%1$d" %2$s>%3$s</option>',
                intval($index),
                selected($active, $index, false),
                esc_html($text)
            );
        }

        echo '</select>';

        echo '<p class="description">' . esc_html__(
            'The active endpoint will be used for fetching logs and for the shortcode.',
            'opensim-chat-audit'
        ) . '</p>';
    }

    public function renderLimitField()
    {
        $settings = $this->getSettings();
        printf(
            '<input type="number" min="1" max="500" name="%1$s[limit]" value="%2$d" />',
            esc_attr(self::OPTION_NAME),
            intval($settings['limit'])
        );
        echo '<p class="description">' . esc_html__(
            'Maximum number of recent records to request from the endpoint (capped by the region server).',
            'opensim-chat-audit'
        ) . '</p>';
    }

    public function renderEventTypeField()
    {
        $settings = $this->getSettings();
        printf(
            '<input type="text" name="%1$s[event_type]" value="%2$s" placeholder="chat or im" />',
            esc_attr(self::OPTION_NAME),
            esc_attr($settings['event_type'])
        );
        echo '<p class="description">' . esc_html__(
            'Optional event filter. Leave blank to include all records.',
            'opensim-chat-audit'
        ) . '</p>';
    }
}

register_activation_hook(__FILE__, array('OpenSimChatAuditPlugin', 'activate'));
new OpenSimChatAuditPlugin();
