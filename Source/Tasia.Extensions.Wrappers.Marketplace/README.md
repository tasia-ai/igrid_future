# OpenSim Marketplace Prim Delivery Add-on

The marketplace add-on revives the legacy `/send` HTTP endpoint and console
command that external web marketplaces use to deliver boxed products into an
avatar's inventory. It registers as an `IApplicationPlugin`, so it is
initialised as part of the simulator startup process once the assembly and
Mono.Addins manifest are present.

## Enabling the plugin

1. Copy `TasiaAddon.Marketplace.dll` and
   `Resources/TasiaAddon.Marketplace.addin.xml` to
   `bin/addon-modules/TasiaAddon.Marketplace`.
2. In `OpenSim.ini` (or the include of your choice) add the configuration
   block:

   ```ini
   [Marketplace]
   Enabled = true
   Password = change-me
   ```

   The password is required for both the HTTP endpoint and the
   `send <object-uuid> <user-uuid>` console command. Pick a strong secret and
   keep it private.

3. Restart the simulator. The `/send` handler becomes available on the main
   HTTP server (e.g. `https://grid.example.com:9000/send`). Requests must
   include `password=<value>` plus the source object and destination avatar
   UUIDs.

## Delivery behaviour

- Objects are located locally first. If the prim is not found the module
  consults the grid service to locate the owning region and issues a remote
  delivery request.
- Inventory and asset services must be available; otherwise the request fails
  with an explanatory message.
- Successful deliveries print to the simulator console and return `OK` to HTTP
  callers.
