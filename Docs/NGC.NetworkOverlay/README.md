# NGC Network Overlay

The Network Overlay module provides a unified TLS, routing, and policy enforcement layer for NGC-hosted services and automatically provisioned regions. It is designed to front the existing Robust discovery plane while adding caching, fallback routing, health supervision, and security controls that are required for Internet-facing workloads.

## Deployment overview

1. **Build and publish the service library.**
   - Reference `Source/NGC.NetworkOverlay/NGC.NetworkOverlay.csproj` from your hosting application or gateway.
   - Use dependency injection to register `NetworkOverlayService` with its collaborators (`IServiceDiscovery`, `ITlsCertificateStore`, `IAcmeClient`, and `IMetricsSink`).
2. **Configure the overlay.**
   - Populate `OverlayServiceConfiguration` with the public domain (for example `PublicDomain("example.com")`).
   - Define `ServiceDefinition` entries for each host label (`login`, `assets`, `inventory`, etc.) and supply discovery keys, rate limits, CORS policies, and CIDR restrictions.
   - Optionally provide a `RegionTemplate` to enable region auto-provisioning with per-region rate limits. Region lookups are cached for `RegionLookupCacheDuration` and negative responses honour `RegionLookupNegativeCacheDuration`.
3. **Host the gateway.**
   - Deploy the hosting process behind a reverse proxy capable of SNI forwarding and client-certificate passthrough when mTLS is required.
   - Expose only HTTPS listeners; HTTP requests should be redirected or rejected before reaching the overlay to comply with HTTPS-only enforcement.
4. **Integrate health and telemetry sinks.**
   - Supply an `IHealthProbe` implementation for active checks (HTTPS status, TCP dial, etc.).
   - Connect `IMetricsSink` to Prometheus, Application Insights, or another metrics backend.

## DNS and certificate management

- **Wildcard coverage.** Publish a wildcard record for the chosen `PublicDomain` (e.g. `*.example.com`). The overlay retrieves wildcard certificates from the provided `ITlsCertificateStore` before attempting on-demand issuance.
- **On-demand issuance.** If `EnableOnDemandCertificates` is `true`, ensure the `IAcmeClient` implementation can solve challenges for arbitrary subdomains. HTTP-01 and DNS-01 flows are supported depending on your provider.
- **Static fallbacks.** Maintain `StaticFallbacks` for critical services so that DNS can be re-pointed to a static endpoint if Robust discovery is unavailable.
- **Service host mapping.** Populate `OverlayServiceConfiguration.Services` with keys that match the left-most DNS label (for example `login` for `login.cutegrid.net`). Labels not present in this map are treated as region slugs and resolved through the `IRegionLocator` implementation.
- **Robust discovery records.** Each `ServiceDefinition` that uses discovery should have a corresponding Robust registration. The overlay caches discovery responses using the configured `DiscoveryCacheDuration`.

## Troubleshooting

| Symptom | Diagnostic steps | Suggested remediation |
| --- | --- | --- |
| Requests rejected with `OverlaySecurityException` | Check TLS termination: ensure the incoming connection is HTTPS and that client certificates are forwarded for protected hosts. Verify the requesting IP is within the configured CIDR blocks and the `Origin` header matches the allowed CORS policy. | Update security configuration or correct the client request. Use overlay metrics (`overlay_security_block`) to identify the blocking reason. |
| Region slug not found | Confirm that the region exists in Robust and that the `IRegionLocator` implementation recognises the slug (after applying any configured normalisation rules). Remember that negative results are cached for `RegionLookupNegativeCacheDuration`. | Fix the upstream discovery/slug mapping and flush the cache if necessary. |
| Frequent `RateLimitExceededException` | Inspect the per-service rate limit policy and metrics (`overlay_rate_limit_block`). Ensure automated clients are throttled or increase the `RateLimitPolicy` capacity/refill interval for the affected service. | Adjust rate limits or distribute traffic across additional instances. |
| Fallback routing triggered unexpectedly | Review Robust discovery health and the overlay logs (`Routed ... fallback=true`). Validate active probes and passive failure counts for the affected endpoints. | Restore Robust, adjust probe logic, or update `StaticFallbacks` to point to healthy targets. |
| Certificates not provisioning | Confirm the wildcard certificate exists in the backing store. Check `EnableOnDemandCertificates` and review ACME client logs. `overlay_route_failure` metrics with `reason=no_endpoints` may indicate routing never occurred due to certificate errors. | Seed certificates, fix ACME connectivity, or disable on-demand issuance until resolved. |

## Observability

The overlay publishes counters for routing decisions, rate limiting, and security denials:

- `overlay_route_success` (`service`, `fallback`) – total routed requests.
- `overlay_route_failure` (`service`, `reason`) – failures due to missing endpoints or discovery errors.
- `overlay_rate_limit_block` (`service`) – requests rejected by rate limiting.
- `overlay_security_block` (`service`, `reason`) – policy enforcement outcomes (`https`, `mtls`, `cidr`, `cors`).

Integrate these metrics with your monitoring pipeline and correlate with hosting logs for full visibility.
