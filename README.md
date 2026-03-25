# Scale Reader Service

A .NET 8.0 Windows Service that reads weight data from industrial scales via IP (SMA protocol, Mettler Toledo Shared Data, or custom) and posts readings to configured HTTP endpoints.

## How It Works

1. The service connects to one or more scales via TCP/IP
2. It polls each scale at a configurable interval (default 750ms)
3. Weight readings are parsed and posted to your web application's API endpoint(s)
4. Supports automatic reconnection with exponential backoff on connection loss

## Supported Protocols

| Protocol | Description |
|----------|-------------|
| SMA (Generic) | Standard SMA weight request/response over TCP. Configurable request command. |
| Mettler Toledo Shared Data | Mettler Toledo Shared Data Services protocol via IP. |
| Custom | Any TCP-based scale protocol with configurable request/response parsing. |

Scale protocol definitions can be loaded from the [device-definitions](https://github.com/GTMichelli-Dev/device-definitions) repo.

## Configuration

Edit `appsettings.json`:

```json
{
  "Polling": {
    "IntervalMs": 750,
    "TimeoutMs": 1000,
    "ReconnectBackoffMs": 2000,
    "MaxBackoffMs": 10000
  },
  "Sma": {
    "RequestCommand": "W\r\n",
    "Encoding": "ascii"
  },
  "Endpoints": [
    {
      "Name": "LocalUpdate",
      "Url": "http://localhost:5110/api/Scale/UpdateScale",
      "Method": "POST",
      "TimeoutMs": 2000
    }
  ],
  "Scales": [
    {
      "Description": "Truck Scale 1",
      "Id": 1,
      "IpAddress": "192.168.1.50",
      "Port": 10001
    }
  ]
}
```

### Settings

| Setting | Description |
|---------|-------------|
| `Polling.IntervalMs` | How often to query each scale (ms) |
| `Polling.TimeoutMs` | Per-request socket timeout (ms) |
| `Polling.ReconnectBackoffMs` | Initial backoff after connection error (ms) |
| `Polling.MaxBackoffMs` | Maximum backoff cap (ms) |
| `Sma.RequestCommand` | Command sent to the scale to request weight |
| `Endpoints` | HTTP endpoints to post weight readings to |
| `Scales` | Array of scale connections (IP, port, ID) |

## Installation

### Run as console app
```bash
dotnet run
```

### Install as Windows Service
```bash
dotnet publish -c Release -o C:\Services\ScaleReaderService
sc create "ScaleReaderService" binPath="C:\Services\ScaleReaderService\ScaleReaderService.exe"
sc start ScaleReaderService
```

## Requirements

- .NET 8.0 Runtime
- Network access to the scale(s) and the web application
