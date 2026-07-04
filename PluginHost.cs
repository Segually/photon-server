using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PhotonServer.Api;

namespace PhotonServer
{
    // The server's implementation of the plugin contract. Everything here runs
    // under the global _sync lock (Monitor is reentrant, so plugins may call back
    // in from inside a hook). Kept in its own file to keep the wire/protocol core
    // separate from the plugin surface.
    public sealed partial class PhotonServer : IPluginHost
    {
        private readonly HookRegistry _hooks;
        private readonly List<IPlugin> _plugins = new();
        private readonly Dictionary<char, Action> _consoleKeys = new();

        public HookRegistry Hooks => _hooks;

        // ── Plugin loading ───────────────────────────────────────────────────
        private void LoadPlugins()
        {
            if (!_config.EnablePlugins) { Log("[Plugins] disabled by config"); return; }

            // Resolve the plugins folder next to the executable (AppContext.BaseDirectory),
            // not the working directory, so a distributed binary always looks beside
            // itself. A relative config value is taken relative to the exe; an
            // absolute one is honoured as-is. Create it on first run so operators
            // have an obvious place to drop plugin DLLs.
            string dir = Path.IsPathRooted(_config.PluginsDirectory)
                ? _config.PluginsDirectory
                : Path.Combine(AppContext.BaseDirectory, _config.PluginsDirectory);

            try { Directory.CreateDirectory(dir); }
            catch (Exception e) { Log($"[Plugins] could not create plugins directory {dir}: {e.Message}"); return; }

            foreach (var path in Directory.GetFiles(dir, "*.dll"))
            {
                // Skip a stray copy of the API assembly itself.
                if (Path.GetFileNameWithoutExtension(path).Equals("PhotonServer.Api", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var asm = Assembly.LoadFrom(path);
                    foreach (var type in asm.GetTypes())
                    {
                        if (!typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface) continue;
                        if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                        var plugin = (IPlugin)Activator.CreateInstance(type)!;
                        plugin.Initialize(this);
                        _plugins.Add(plugin);
                        Log($"[Plugins] loaded '{plugin.Name}' ({type.FullName}) from {Path.GetFileName(path)}");
                    }
                }
                catch (Exception e)
                {
                    Log($"[Plugins] failed to load {Path.GetFileName(path)}: {e.Message}");
                }
            }
            Log($"[Plugins] {_plugins.Count} plugin(s) active");
        }

        // ── Console hotkey dispatch ──────────────────────────────────────────
        // One background reader; a registered key's handler runs on this thread and
        // may block on its own Console I/O (server packet loops are unaffected).
        private void StartConsoleThread()
        {
            bool any;
            lock (_sync) any = _consoleKeys.Count > 0;
            if (!any) return;
            if (Console.IsInputRedirected)
            {
                Log("[Plugins] console input redirected — hotkeys disabled");
                return;
            }

            var t = new Thread(() =>
            {
                while (true)
                {
                    ConsoleKeyInfo k;
                    try { k = Console.ReadKey(intercept: true); }
                    catch { return; }
                    char c = char.ToUpperInvariant(k.KeyChar);
                    Action? handler;
                    lock (_sync) _consoleKeys.TryGetValue(c, out handler);
                    if (handler == null) continue;
                    try { handler(); }
                    catch (Exception e) { Log($"[Plugins] hotkey '{c}' handler error: {e.Message}"); }
                }
            }) { IsBackground = true, Name = "ConsoleKeys" };
            t.Start();
        }

        // ── IPluginHost: inspect ─────────────────────────────────────────────
        public IReadOnlyList<IRoom> Rooms
        {
            get { lock (_sync) return _rooms.Values.Cast<IRoom>().ToList(); }
        }

        public IRoom? GetRoom(string name)
        {
            lock (_sync) return _rooms.TryGetValue(name, out var r) ? r : null;
        }

        // ── IPluginHost: create / delete / overwrite ─────────────────────────
        public IRoom CreateRoom(string name, int maxPlayers = 0, bool isOpen = true, bool isVisible = true)
        {
            lock (_sync)
            {
                if (_rooms.TryGetValue(name, out var existing)) return existing;
                var room = new Room(name) { MaxPlayers = maxPlayers, IsOpen = isOpen, IsVisible = isVisible };
                room.Properties[(byte)255] = maxPlayers;
                room.Properties[(byte)253] = isOpen;
                room.Properties[(byte)254] = isVisible;
                _rooms[name] = room;
                Log($"[Plugins] room '{name}' created (server-side, ownerless)");
                _hooks.RaiseRoomCreated(room);
                return room;
            }
        }

        public bool DeleteRoom(string name)
        {
            lock (_sync)
            {
                if (!_rooms.TryGetValue(name, out var room)) return false;
                // Notify + detach any live members before tearing the room down.
                foreach (var m in room.Members.Values.Where(m => !m.IsInactive && m.Peer != null).ToList())
                {
                    var p = m.Peer;
                    SendReliableMessage(p, 0, BuildEvt(p, EvCode.Leave, new Dictionary<byte, object?>
                    {
                        [Param.ActorNr]    = m.ActorNr,
                        [Param.IsInactive] = false,
                    }));
                    p.Room = null; p.ActorNr = 0;
                }
                DestroyRoom(room, "deleted by plugin");
                return true;
            }
        }

        public IRoom OverwriteRoom(string name, int? maxPlayers = null, bool? isOpen = null,
            bool? isVisible = null, IReadOnlyDictionary<object, object?>? properties = null)
        {
            lock (_sync)
            {
                bool created = false;
                if (!_rooms.TryGetValue(name, out var room))
                {
                    room = new Room(name);
                    _rooms[name] = room;
                    created = true;
                }
                var changed = new Dictionary<object, object?>();
                if (maxPlayers.HasValue) { room.MaxPlayers = maxPlayers.Value; room.Properties[(byte)255] = maxPlayers.Value; changed[(byte)255] = maxPlayers.Value; }
                if (isOpen.HasValue)     { room.IsOpen     = isOpen.Value;     room.Properties[(byte)253] = isOpen.Value;     changed[(byte)253] = isOpen.Value; }
                if (isVisible.HasValue)  { room.IsVisible  = isVisible.Value;  room.Properties[(byte)254] = isVisible.Value;  changed[(byte)254] = isVisible.Value; }
                if (properties != null)
                    foreach (var kv in properties) { room.Properties[kv.Key] = kv.Value; changed[kv.Key] = kv.Value; }

                Log($"[Plugins] room '{name}' {(created ? "created via overwrite" : "overwritten")}");
                if (created) _hooks.RaiseRoomCreated(room);
                if (changed.Count > 0 && room.PlayerCount > 0) BroadcastRoomProps(room, changed);
                return room;
            }
        }

        // ── IPluginHost: room mutation (broadcasts to clients) ───────────────
        private bool MutateRoom(string name, Func<Room, Dictionary<object, object?>> apply)
        {
            lock (_sync)
            {
                if (!_rooms.TryGetValue(name, out var room)) return false;
                var changed = apply(room);
                if (changed.Count > 0) BroadcastRoomProps(room, changed);
                return true;
            }
        }

        public bool SetMaxPlayers(string room, int maxPlayers) => MutateRoom(room, r =>
            { r.MaxPlayers = maxPlayers; r.Properties[(byte)255] = maxPlayers; return new() { [(byte)255] = maxPlayers }; });

        public bool SetRoomOpen(string room, bool open) => MutateRoom(room, r =>
            { r.IsOpen = open; r.Properties[(byte)253] = open; return new() { [(byte)253] = open }; });

        public bool SetRoomVisible(string room, bool visible) => MutateRoom(room, r =>
            { r.IsVisible = visible; r.Properties[(byte)254] = visible; return new() { [(byte)254] = visible }; });

        public bool SetRoomProperty(string room, object key, object? value) => MutateRoom(room, r =>
            { r.Properties[key] = value; return new() { [key] = value }; });

        // ── IPluginHost: player mutation / moderation ────────────────────────
        public bool SetPlayerProperty(string room, int actorNr, object key, object? value)
        {
            lock (_sync)
            {
                if (!_rooms.TryGetValue(room, out var r) || !r.Members.TryGetValue(actorNr, out var m)) return false;
                m.Properties[key] = value;
                BroadcastPlayerProps(r, actorNr, new() { [key] = value });
                return true;
            }
        }

        public bool SetPlayerNickName(string room, int actorNr, string nickName) =>
            SetPlayerProperty(room, actorNr, (byte)255, nickName);

        public bool KickPlayer(string room, int actorNr)
        {
            lock (_sync)
            {
                if (!_rooms.TryGetValue(room, out var r) || !r.Members.TryGetValue(actorNr, out var m)) return false;
                if (m.Peer != null) LeaveRoom(m.Peer, willComeBack: false);
                else ForceRemoveActor(r, actorNr);
                return true;
            }
        }

        public bool DisconnectPlayer(string room, int actorNr)
        {
            lock (_sync)
            {
                if (!_rooms.TryGetValue(room, out var r) || !r.Members.TryGetValue(actorNr, out var m) || m.Peer == null)
                    return false;
                var peer = m.Peer;
                SendDisconnectCommand(peer);
                LeaveRoom(peer, willComeBack: false);
                _peers.Remove(peer.EndPoint);
                CleanupSession(peer);
                _hooks.RaisePlayerDisconnected(peer);
                return true;
            }
        }

        // ── IPluginHost: messaging ───────────────────────────────────────────
        public void BroadcastEvent(string room, byte eventCode, object? data)
        {
            lock (_sync)
            {
                if (!_rooms.TryGetValue(room, out var r)) return;
                BroadcastEvent(r, eventCode, new Dictionary<byte, object?> { [Param.Data] = data, [Param.ActorNr] = 0 });
            }
        }

        public bool SendEvent(string room, int actorNr, byte eventCode, object? data)
        {
            lock (_sync)
            {
                if (!_rooms.TryGetValue(room, out var r) || !r.Members.TryGetValue(actorNr, out var m) || m.Peer == null)
                    return false;
                SendReliableMessage(m.Peer, 0, BuildEvt(m.Peer, eventCode,
                    new Dictionary<byte, object?> { [Param.Data] = data, [Param.ActorNr] = 0 }));
                return true;
            }
        }

        // ── IPluginHost: host services ───────────────────────────────────────
        void IPluginHost.Log(string message) => Log($"[Plugin] {message}");

        public void SuspendLog()
        {
            lock (_logSync) _logSuspended = true;
        }

        public void ResumeLog()
        {
            List<string> flush;
            lock (_logSync) { _logSuspended = false; flush = new List<string>(_logBuffer); _logBuffer.Clear(); }
            foreach (var l in flush) Console.WriteLine(l);
        }

        public void RegisterConsoleKey(char key, Action handler)
        {
            lock (_sync) _consoleKeys[char.ToUpperInvariant(key)] = handler;
        }

        // ── Shared internals used by the host methods ────────────────────────

        // Fire delete hooks and remove a room. The single choke point for room
        // teardown so RoomDeleting/RoomDeleted always fire (empty-room cleanup,
        // TTL sweep, and plugin DeleteRoom all route through here).
        private void DestroyRoom(Room room, string reason)
        {
            if (!_rooms.ContainsKey(room.Name)) return;
            _hooks.RaiseRoomDeleting(room);
            _rooms.Remove(room.Name);
            Log($"[Server]   room '{room.Name}' removed ({reason})");
            _hooks.RaiseRoomDeleted(room.Name);
        }

        // Remove an inactive (peerless) actor and broadcast a permanent Leave,
        // mirroring the TTL-sweep path. Used when a plugin kicks an inactive slot.
        private void ForceRemoveActor(Room room, int actorNr)
        {
            int oldMaster = room.MasterClientActor;
            room.RemoveMember(actorNr);
            if (room.CleanupCacheOnLeave)
                room.EventCache.RemoveAll(ce => ce.Sender == actorNr && !ce.Global);
            var leaveParams = new Dictionary<byte, object?>
            {
                [Param.ActorNr]    = actorNr,
                [Param.ActorList]  = room.ActorNumbers,
                [Param.IsInactive] = false,
            };
            if (room.MasterClientActor != oldMaster && room.MasterClientActor != 0)
                leaveParams[Param.MasterClientId] = room.MasterClientActor;
            BroadcastEvent(room, EvCode.Leave, leaveParams);
            if (room.PlayerCount == 0) DestroyRoom(room, "empty");
        }

        private void BroadcastRoomProps(Room room, Dictionary<object, object?> changed) =>
            BroadcastEvent(room, EvCode.PropertiesChanged, new Dictionary<byte, object?>
            {
                [Param.Properties]    = changed,
                [Param.TargetActorNr] = 0,
                [Param.ActorNr]       = 0,
            });

        private void BroadcastPlayerProps(Room room, int actorNr, Dictionary<object, object?> changed) =>
            BroadcastEvent(room, EvCode.PropertiesChanged, new Dictionary<byte, object?>
            {
                [Param.Properties]    = changed,
                [Param.TargetActorNr] = actorNr,
                [Param.ActorNr]       = 0,
            });

        private void SendDisconnectCommand(Peer peer)
        {
            using var ms = new MemoryStream();
            ms.WriteByte((byte)CommandType.Disconnect);
            ms.WriteByte(0xFF); // channel
            ms.WriteByte(0x00); // flags (unreliable)
            ms.WriteByte(0x00); // reserved
            Wire.WriteI32(ms, 12); // size (header only, no payload)
            Wire.WriteI32(ms, 0);  // reliable seq
            SendPacket(peer, new[] { ms.ToArray() });
        }
    }
}
