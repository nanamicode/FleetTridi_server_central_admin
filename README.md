# FleetTridi

FleetTridi is the central fleet-management stack for TridiAudience Android totems.

## Goal

Manage any number of enrolled totems over the Internet from one Windows admin console:

- online/offline inventory by city/site
- hardware telemetry and health
- remote APK install/update
- remote file upload to arbitrary device paths
- remote screen snapshots and input control
- remote root shell for authorized maintenance
- one-device or bulk jobs
- persistent device identity independent of changing IP addresses
- audit/job history

## Architecture

A totem does **not** need inbound Internet access. Each Android device runs **FleetTridi Agent**, which opens an outbound WebSocket connection to **FleetTridi Server**. The Windows admin app talks only to the central server.

```text
+-----------------------+            HTTPS / WebSocket            +----------------------+
| FleetTridi Admin.exe  | --------------------------------------> | FleetTridi Server    |
| Windows               |                                         | public endpoint      |
+-----------------------+                                         +----------+-----------+
                                                                           ^
                                                                           |
                                                            outbound WSS   |
                                                                           |
                                              +----------------------------+------------------+
                                              |                            |                  |
                                      +-------+------+             +-------+------+   +-------+------+
                                      | Totem Agent  |             | Totem Agent  |   | Totem Agent  |
                                      | city/site A  |             | city/site B  |   | city/site N  |
                                      +--------------+             +--------------+   +--------------+
```

The server stores a stable device ID, name, city/site, last-seen address and enrollment token. IP is telemetry, not identity.

## Initial development credentials

For the prototype only:

- login: `nanamicode`
- password: `veralucia12`

Change them before any public deployment.

## Repository layout

The implementation is being added under:

- `src/FleetTridi.Server` — ASP.NET Core central server
- `src/FleetTridi.Admin` — Windows WPF admin console
- `android-agent` — Android/Kotlin root agent
- `scripts` — build/publish helpers
- `.github/workflows` — reproducible Windows/server/Android builds

## Internet deployment model

Only the central server needs a public address. Totems initiate their own connection, so they work behind normal routers and NAT. If the server is hosted at a branch/office, expose its configured HTTPS port with normal router/firewall forwarding or place it on a public host. No paid relay is required by FleetTridi itself.

## Status

Prototype implementation in progress.
