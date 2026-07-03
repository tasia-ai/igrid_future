<?php
/**
 * Plugin Name: OpenSim Chat Audit Viewer
 * Description: Displays chat and instant-message audit logs collected by the OpenSim ChatAudit addon.
 * Version: 1.0.0
 * Author: Tranquillity Grid
 * License: MIT
 *
 * This plugin queries the OpenSim ChatAudit HTTP endpoint and renders the
 * resulting transcript inside a WordPress admin page and optional shortcode.
 */

if (!defined('ABSPATH')) {
    exit;
}

class OpenSimChatAuditPlugin
{
    private const OPTION_GROUP = 'opensim_chat_audit';
    private const OPTION_NAME = 'opensim_chat_audit_settings';
    private const NONCE_ACTION = 'opensim_chat_audit_refresh';

    public function __construct()
    {
        add_action('admin_menu', [$this, 'registerAdminMenu']);
        add_action('admin_init', [$this, 'registerSettings']);
        add_shortcode('opensim_chat_audit', [$this, 'renderShortcode']);
        add_action('admin_post_opensim_chat_audit_refresh', [$this, 'handleAdminRefresh']);
    }

    public static function activate(): void
    {
        if (!get_option(self::OPTION_NAME)) {
            add_option(self::OPTION_NAME, [
                'endpoint' => '',
                'token' => '',
                'limit' => 100,
                'event_type' => '',
            ]);
        }
    }

    public function registerSettings(): void
    {
        register_setting(self::OPTION_GROUP, self::OPTION_NAME, [
            'type' => 'array',
            'sanitize_callback' => [$this, 'sanitizeSettings'],
            'default' => [
                'endpoint' => '',
                'token' => '',
                'limit' => 100,
                'event_type' => '',
            ],
        ]);

        add_settings_section(
            'opensim_chat_audit_main',
            __('Chat Audit API', 'opensim-chat-audit'),
            function () {
                echo '<p>' . esc_html__(
                    'Configure the ChatAudit endpoint exposed by the OpenSim region server.',
                    'opensim-chat-audit'
                ) . '</p>';
            },
            self::OPTION_GROUP
        );

        add_settings_field(
            'endpoint',
            __('Endpoint URL', 'opensim-chat-audit'),
            [$this, 'renderEndpointField'],
            self::OPTION_GROUP,
            'opensim_chat_audit_main'
        );

        add_settings_field(
            'token',
            __('API Token', 'opensim-chat-audit'),
            [$this, 'renderTokenField'],
            self::OPTION_GROUP,
            'opensim_chat_audit_main'
        );

        add_settings_field(
            'limit',
            __('Max Records', 'opensim-chat-audit'),
            [$this, 'renderLimitField'],
            self::OPTION_GROUP,
            'opensim_chat_audit_main'
        );

        add_settings_field(
            'event_type',
            __('Event Filter', 'opensim-chat-audit'),
            [$this, 'renderEventTypeField'],
            self::OPTION_GROUP,
            'opensim_chat_audit_main'
        );
    }

    public function registerAdminMenu(): void
    {
        add_menu_page(
            __('OpenSim Chat Logs', 'opensim-chat-audit'),
            __('OpenSim Chat Logs', 'opensim-chat-audit'),
            'manage_options',
            'opensim-chat-audit',
            [$this, 'renderAdminPage'],
            'dashicons-format-chat',
            81
        );
    }

    public function renderAdminPage(): void
    {
        if (!current_user_can('manage_options')) {
            wp_die(__('You do not have permission to view this page.', 'opensim-chat-audit'));
        }

        $settings = $this->getSettings();
        $records = $this->fetchRecords($settings);

        echo '<div class="wrap">';
        echo '<h1>' . esc_html__('OpenSim Chat Audit', 'opensim-chat-audit') . '</h1>';

        echo '<form action="options.php" method="post">';
        settings_fields(self::OPTION_GROUP);
        do_settings_sections(self::OPTION_GROUP);
        submit_button();
        echo '</form>';

        if (is_wp_error($records)) {
            echo '<div class="notice notice-error"><p>' . esc_html($records->get_error_message()) . '</p></div>';
        } else {
            $refresh_url = admin_url('admin-post.php');
            echo '<form method="post" action="' . esc_url($refresh_url) . '">';
            echo '<input type="hidden" name="action" value="opensim_chat_audit_refresh" />';
            wp_nonce_field(self::NONCE_ACTION);
            submit_button(__('Refresh Logs', 'opensim-chat-audit'));
            echo '</form>';
            $this->renderTable($records);
        }

        echo '</div>';
    }

    public function renderShortcode($atts): string
    {
        if (!current_user_can('manage_options')) {
            return __('You do not have permission to view the OpenSim chat log.', 'opensim-chat-audit');
        }

        $settings = $this->getSettings();
        $records = $this->fetchRecords($settings);
        if (is_wp_error($records)) {
            return '<div class="opensim-chat-audit-error">' . esc_html($records->get_error_message()) . '</div>';
        }

        ob_start();
        $this->renderTable($records);
        return ob_get_clean();
    }

    public function handleAdminRefresh(): void
    {
        if (!current_user_can('manage_options')) {
            wp_die(__('You do not have permission to perform this action.', 'opensim-chat-audit'));
        }

        check_admin_referer(self::NONCE_ACTION);
        wp_safe_redirect(admin_url('admin.php?page=opensim-chat-audit&refreshed=1'));
        exit;
    }

    private function getSettings(): array
    {
        $defaults = [
            'endpoint' => '',
            'token' => '',
            'limit' => 100,
            'event_type' => '',
        ];

        $settings = get_option(self::OPTION_NAME, $defaults);
        if (!is_array($settings)) {
            $settings = $defaults;
        }

        return wp_parse_args($settings, $defaults);
    }

    private function sanitizeSettings($input): array
    {
        $sanitized = [];
        $sanitized['endpoint'] = isset($input['endpoint']) ? esc_url_raw(trim($input['endpoint'])) : '';
        $sanitized['token'] = isset($input['token']) ? sanitize_text_field($input['token']) : '';
        $sanitized['limit'] = isset($input['limit']) ? max(1, min(500, intval($input['limit']))) : 100;
        $sanitized['event_type'] = isset($input['event_type']) ? sanitize_text_field($input['event_type']) : '';
        return $sanitized;
    }

    private function fetchRecords(array $settings)
    {
        if (empty($settings['endpoint'])) {
            return new WP_Error('opensim_chat_audit_missing_endpoint', __('Configure the endpoint URL before fetching logs.', 'opensim-chat-audit'));
        }

        $args = [
            'timeout' => 10,
            'headers' => [],
        ];

        $url = $settings['endpoint'];
        $query = [];

        if (!empty($settings['token'])) {
            $args['headers']['X-Chat-Audit-Token'] = $settings['token'];
        }

        if (!empty($settings['limit'])) {
            $query['limit'] = intval($settings['limit']);
        }

        if (!empty($settings['event_type'])) {
            $query['type'] = $settings['event_type'];
        }

        if (!empty($query)) {
            $separator = (false === strpos($url, '?')) ? '?' : '&';
            $url .= $separator . http_build_query($query, '', '&', PHP_QUERY_RFC3986);
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
            return new WP_Error('opensim_chat_audit_http_error', $message, ['status' => $code]);
        }

        $body = wp_remote_retrieve_body($response);
        $data = json_decode($body, true);
        if (!is_array($data) || !isset($data['records']) || !is_array($data['records'])) {
            return new WP_Error('opensim_chat_audit_invalid_body', __('Invalid JSON structure received from OpenSim.', 'opensim-chat-audit'));
        }

        return $data['records'];
    }

    private function renderTable(array $records): void
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
                $type = isset($record['eventType']) ? esc_html($record['eventType']) : '';
                $region = isset($record['region']) ? esc_html($record['region']) : '';
                $from = isset($record['fromName']) ? esc_html($record['fromName']) : '';
                $target = isset($record['target']) ? esc_html($record['target']) : '';
                $message = isset($record['message']) ? esc_html($record['message']) : '';

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

    private function formatTimestamp($timestamp): string
    {
        if (empty($timestamp)) {
            return '';
        }

        try {
            $dt = new DateTime($timestamp, new DateTimeZone('UTC'));
            $dt->setTimezone(wp_timezone());
            return $dt->format(get_option('date_format') . ' ' . get_option('time_format'));
        } catch (Exception $ex) {
            return sanitize_text_field($timestamp);
        }
    }

    public function renderEndpointField(): void
    {
        $settings = $this->getSettings();
        printf(
            '<input type="url" class="regular-text" name="%1$s[endpoint]" value="%2$s" placeholder="https://grid.example.com/addons/chat-audit" required />',
            esc_attr(self::OPTION_NAME),
            esc_attr($settings['endpoint'])
        );
        echo '<p class="description">' . esc_html__(
            'The full HTTPS URL to the ChatAudit endpoint, e.g. https://grid.example.com/addons/chat-audit',
            'opensim-chat-audit'
        ) . '</p>';
    }

    public function renderTokenField(): void
    {
        $settings = $this->getSettings();
        printf(
            '<input type="text" class="regular-text" name="%1$s[token]" value="%2$s" autocomplete="off" />',
            esc_attr(self::OPTION_NAME),
            esc_attr($settings['token'])
        );
        echo '<p class="description">' . esc_html__(
            'Optional API token configured in OpenSim.ini under [ChatAudit] ApiToken.',
            'opensim-chat-audit'
        ) . '</p>';
    }

    public function renderLimitField(): void
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

    public function renderEventTypeField(): void
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

register_activation_hook(__FILE__, ['OpenSimChatAuditPlugin', 'activate']);
new OpenSimChatAuditPlugin();
