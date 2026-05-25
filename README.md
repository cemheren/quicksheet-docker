# quicksheet-docker

**Docker container monitoring on your desktop wallpaper.** A [QuickSheet](https://github.com/cemheren/QuickSheet) extension that shows running containers, resource usage, and images — always visible, zero clicks.

[![QuickSheet Extension](https://img.shields.io/badge/QuickSheet-extension-blue)](https://github.com/cemheren/QuickSheet)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

## Why?

Docker Desktop is heavy. `docker ps` requires a terminal switch. This extension puts your container status **on your wallpaper** — always visible while you code. Spot a crashed container instantly. Monitor resource usage at a glance.

## Installation

In any QuickSheet cell, type:
```
ext: github:cemheren/quicksheet-docker
```

## Usage

| Cell Value | Output |
|---|---|
| `docker:` | List running containers (name, image, status, ports) |
| `docker: all` | All containers including stopped |
| `docker: stats` | CPU% and memory usage per container |
| `docker: images` | Local images with sizes |
| `docker: inspect <name>` | Detailed info about a specific container |

### Example Output

```
{5 containers}
NAME | IMAGE | STATUS | PORTS
─────┼───────┼────────┼──────
postgres-dev | postgres:16 | Up 3 hours | 5432→5432
redis-cache | redis:alpine | Up 3 hours | 6379→6379
api-server | myapp:latest | Up 10 min | 8080→8080, 8443→443
nginx-proxy | nginx:1.25 | Up 3 hours | 80→80, 443→443
worker-1 | myapp:latest | Up 10 min | -
```

## How It Works

Communicates directly with the Docker Engine API over the Unix socket (`/var/run/docker.sock`) or Windows named pipe. **Zero external dependencies** — uses only .NET BCL `HttpClient` with `UnixDomainSocketEndPoint`.

### Prerequisites

- Docker Engine running (Docker Desktop, colima, OrbStack, etc.)
- Socket accessible at `/var/run/docker.sock` (or set `DOCKER_HOST`)
- .NET 9 runtime

## Building

```bash
dotnet build
dotnet run
```

## Protocol

Uses the standard QuickSheet JSON-lines extension protocol:

```json
→ {"id":"1","value":""}
← {"id":"1","result":"{3 containers}\nNAME | IMAGE | STATUS | PORTS\n..."}
```

## Related

- [QuickSheet](https://github.com/cemheren/QuickSheet) — the desktop wallpaper spreadsheet
- [quicksheet-sysmon](https://github.com/Deskworks/quicksheet-sysmon) — system resource monitoring

## License

MIT
