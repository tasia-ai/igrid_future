# Hypergrid HTTP and HTTPS on one gatekeeper port

Legacy Hypergrid links may use `http://` while newer grids use `https://`. The
GridServer can optionally accept both protocols on a single **native TLS
listener** and dispatch both to the same registered handlers.

This is disabled by default. Enable it only when legacy HTTP interoperability is
required: HTTP traffic is unencrypted and can expose Hypergrid request data and
credentials to network observers. Do not enable it on a listener intended to
enforce transport encryption or on private/admin endpoints.

Example `[Network]` configuration, with the regular `port` kept private and the
mixed listener on the public port:

```ini
port = 8002
https_main = false
https_listener = true
https_port = 8003
https_external = false
https_allow_http = true
cert_path = "/path/to/certificate.p12"
cert_pass = "..."
```

The certificate must be configured for the native TLS listener. The setting is
rejected if there is no native TLS listener (including when HTTPS is terminated
externally). If Pangolin or another reverse proxy terminates TLS, configure both
schemes there and leave `https_allow_http` disabled.

Keep the grid's advertised `GatekeeperURI` and `HomeURI` on HTTPS where
possible. Enabling this option does not create a second port or alter the
registered Hypergrid handlers, QUIC listeners, authentication checks, inventory
permissions, or estate-management policy; it only allows a plain HTTP
connection to reach the same native TLS listener.
