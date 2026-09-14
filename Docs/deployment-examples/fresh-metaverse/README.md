# Fresh Metaverse Deployment Snapshot

This snapshot preserves live files that are outside the OpenSim Git checkout on the Windows host.

- `FreshMetaverseManager/` - standalone WPF grid manager source. It starts/stops Robust, MoneyD, Apache/XAMPP, selected/all sims, shows sim status, manages money balances, OAR backup/restore, and add/remove named sims via `regions.yaml` regeneration.
- `htdocs/` - Apache/XAMPP website overlay for `H:/grid/xampp/htdocs`, including the Amber landing page, registration links, live map, search, and ostools pages configured for `os.tasia.work.gd:22000`.
- `igrid-package/` - generator/templates/start scripts snapshot from `H:/grid/igrid-package`, including `[RestartModule]` API settings in `SimOpenSim.ini.tpl`.

Live deployment paths on the Windows host:

- Manager: `H:/grid/FreshMetaverseManager`
- Apache htdocs: `H:/grid/xampp/htdocs`
- Config generator: `H:/grid/igrid-package`
- Money server: `H:/grid/moneyd`

Notes:

- Website files here are a preservation snapshot, not an automatically deployed package.
- Secrets/tokens from generated runtime files should not be added here.
- Regenerate live configs with `QUIC_REAL_CERT=1 python H:/grid/igrid-package/generate_configs.py`.
