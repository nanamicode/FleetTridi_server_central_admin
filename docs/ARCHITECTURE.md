# FleetTridi architecture

## Control plane

FleetTridi uses a stable device identity and an outbound WebSocket from every Android totem to the central server. The current IP is telemetry, not identity.

This is intentional: plain ADB-over-TCP by stored IP does not survive NAT, CGNAT, dynamic addresses or different networks reliably.

## Components

### FleetTridi.Server
.NET 8 central control plane. Receives agent connections, stores fleet state, stages APK/files and routes jobs.

### FleetTridi.Agent
Android/Kotlin foreground service. Starts at boot, reconnects automatically and executes maintenance jobs through local root when the owned device provides a working `su`.

Supported v0.1 job primitives:
- root shell
- key events
- tap
- swipe
- screenshot
- arbitrary file delivery
- APK install/update using `pm install -r -d`
- telemetry

### FleetTridi.Admin
Windows WPF client compiled as a self-contained `.exe`.

## Enrollment

1. Create a device centrally and obtain `deviceId` + `enrollmentToken`.
2. Install FleetTridi Agent once on the totem.
3. Enter server URL, device ID and token.
4. Save/start the service.
5. From then on the agent reconnects after boot and network/IP changes.

## Root

FleetTridi is a fleet manager, not a universal Android rooting exploit. The specific hardware/firmware must already support root (`su`, engineering firmware, vendor root, etc.). Once root exists, normal fleet operations no longer depend on an attached PC or repeated ADB sessions.

## Internet topology

```
Admin.exe -> HTTPS -> FleetTridi.Server <- WSS <- Totem Agent
```

Only the central server needs a public endpoint.

## Scale path

The v0.1 protocol is already device-ID based and can grow to city/site/device groups. Next production layers are durable database/audit, staged rollouts, hash-based creative synchronization, live video streaming, release channels and rollback.
