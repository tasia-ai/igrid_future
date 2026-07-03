# Tasia Web Viewer

WordPress plugin to host an embedded web viewer for TasiaNGC/OpenSim grid.

## Features

- Embeds OpenSim web viewer in WordPress pages
- Configurable grid URL and name
- Shortcode: `[web_viewer height="600px"]`
- Viewer served at `/viewer` URL

## Setup

1. Install this plugin in WordPress
2. Go to Settings > Web Viewer to configure grid URL
3. Build and install the Andromeda Viewer

## Building Andromeda Viewer

The embedded viewer requires the Andromeda Viewer to be built:

```bash
git clone https://github.com/Terreii/andromeda-viewer.git
cd andromeda-viewer
npm install
npm run build
```

Copy the contents of the `build/` folder to:
```
wp-content/plugins/tasia-web-viewer/viewer/
```

## Files

- `tasia-web-viewer.php` - Main plugin file
- `viewer/` - Directory for built viewer files (create after building)
- `andromeda-viewer/` - Cloned source (can be removed after building)
