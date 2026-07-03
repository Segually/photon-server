# Deploying photon-server

## Build a self-contained Linux binary (from a machine with the .NET SDK)

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

Output: `bin/Release/net10.0/linux-x64/publish/photon-server` — a single ELF
executable that bundles the .NET runtime (no .NET install needed on the VPS).

Copy it to the VPS, e.g.:

```bash
scp bin/Release/net10.0/linux-x64/publish/photon-server user@your-vps:/opt/photon/
```

## Configure

On first run the server writes `config.json` next to the binary. The important
field for a public deployment is **`PublicHost`**: the IP/DNS clients reconnect
to for the game server after a create/join redirect. Set it to your VPS's
public address:

```json
{
  "Ports": [5055, 27000, 27001, 27002],
  "PublicHost": "203.0.113.10",
  "MaxPeersPerRoom": -1,
  "MaxRooms": -1,
  "RequireAppId": null,
  "RequireAppVersion": null,
  "PeerTimeoutMs": 15000,
  "VerboseLogging": true
}
```

## Firewall

Open the bound ports for **UDP**. A client in direct mode reaches the master on
`5055` and is redirected to the game server on `27002`, so at minimum allow:

```bash
sudo ufw allow 5055/udp
sudo ufw allow 27002/udp
# (27000/27001 too if you use the standard name-server/master ports)
```

## Run

```bash
chmod +x photon-server
./photon-server
```

The startup log prints the bound ports and the redirect address. If `PublicHost`
is still loopback it warns you to change it.

## Connect the probe (from your machine)

```bash
dotnet run --project ../photon-client/photon-client/photon-probe.csproj -- \
  direct <your-vps-ip>:5055 --nick tester
```
