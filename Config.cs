using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotonServer
{
    // Host configuration, loaded from config.json next to the executable. A
    // default file is written on first run so it's easy to discover and edit.
    public sealed class ServerConfig
    {
        /// <summary>UDP ports to bind. The standard Photon UDP ports are
        /// 27000 (name server), 27001 (master), 27002 (game); 5055 covers
        /// clients configured with a custom master port.</summary>
        public int[] Ports { get; set; } = { 5055, 27000, 27001, 27002 };

        /// <summary>IP or hostname handed to clients for the create/join game-server
        /// redirect (they reconnect here on the game-server port to enter a room).
        /// Defaults to loopback for local testing — set this to your VPS's public
        /// IP or DNS name when deploying so remote clients can reach it.</summary>
        public string PublicHost { get; set; } = "127.0.0.1";

        /// <summary>Hard cap on actors per room, applied to every room on top of
        /// the room's own MaxPlayers. -1 means no server-imposed limit.</summary>
        public int MaxPeersPerRoom { get; set; } = -1;

        /// <summary>Maximum number of concurrent rooms. -1 means no limit.</summary>
        public int MaxRooms { get; set; } = -1;

        /// <summary>If set, clients must present this AppId to authenticate.
        /// null accepts any AppId (useful while reverse engineering).</summary>
        public string? RequireAppId { get; set; } = null;

        /// <summary>If set, clients must present this AppVersion. null accepts any.</summary>
        public string? RequireAppVersion { get; set; } = null;

        /// <summary>Drop a peer (and remove it from its room) after this many
        /// milliseconds without any inbound packet. Default 5 minutes so a briefly
        /// stalled/backgrounded client is not kicked. -1 disables timeout sweeps.</summary>
        public int PeerTimeoutMs { get; set; } = 300000;

        /// <summary>Region code advertised to clients (shown in the game's UI).
        /// Empty = auto-detect from the server's public IP geolocation.</summary>
        public string Region { get; set; } = "";

        /// <summary>Log every decoded operation/event. Disable for quieter,
        /// higher-throughput hosting.</summary>
        public bool VerboseLogging { get; set; } = true;

        // ── Load / create ────────────────────────────────────────────────────
        public static ServerConfig Load(string path)
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            };

            if (!File.Exists(path))
            {
                var def = new ServerConfig();
                try
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(def, opts));
                    Console.WriteLine($"[Config] No config.json found — wrote defaults to {path}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[Config] Could not write default config: {e.Message}");
                }
                return def;
            }

            try
            {
                var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), opts);
                if (cfg == null) { Console.WriteLine("[Config] Empty config — using defaults"); return new ServerConfig(); }
                Console.WriteLine($"[Config] Loaded {path}");
                return cfg;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Config] Failed to parse {path}: {e.Message} — using defaults");
                return new ServerConfig();
            }
        }
    }
}
