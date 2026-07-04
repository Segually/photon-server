using System;
using System.Collections.Generic;
using System.Linq;
using PhotonServer.Api;

namespace PhotonServer
{
    // A room (Photon "game") and its actors. All access is serialized by the
    // server's global lock, so no internal locking here. Implements the read-only
    // IRoom view exposed to plugins (explicit implementations so the plugin-facing
    // surface stays separate from the fields the server mutates directly).
    public sealed class Room : IRoom
    {
        public string Name;
        public int    MaxPlayers;
        public bool   IsOpen    = true;
        public bool   IsVisible = true;

        // Player time-to-live in ms: how long a disconnected actor stays in the
        // room as "inactive" and can rejoin. 0 = leave immediately (no rejoin);
        // -1 (or int.MaxValue) = never expire.
        public int PlayerTtl;
        public bool CleanupCacheOnLeave = true;

        // Custom room properties + the "props for lobby" whitelist.
        public readonly Dictionary<object, object?> Properties = new();
        public string[] PropsListedInLobby = Array.Empty<string>();

        public int MasterClientActor;
        private int _nextActor = 1;

        // actorNr → member
        public readonly Dictionary<int, RoomMember> Members = new();

        // Buffered events (RaiseEvent with AddToRoomCache) replayed to joiners so
        // late arrivals see already-spawned objects / state.
        public readonly List<CachedEvent> EventCache = new();

        public Room(string name) { Name = name; }

        public int AddMember(Peer peer, string? userId)
        {
            int actorNr = _nextActor++;
            Members[actorNr] = new RoomMember { ActorNr = actorNr, Peer = peer, UserId = userId };
            RecomputeMaster();
            return actorNr;
        }

        public void RemoveMember(int actorNr)
        {
            Members.Remove(actorNr);
            RecomputeMaster();
        }

        // The master client is the lowest-numbered active actor.
        public void RecomputeMaster()
        {
            var active = Members.Values.Where(m => !m.IsInactive).Select(m => m.ActorNr);
            MasterClientActor = active.Any() ? active.Min() : 0;
        }

        // All actors, active and inactive (inactive players still hold a slot).
        public int[] ActorNumbers => Members.Keys.OrderBy(x => x).ToArray();

        public IEnumerable<RoomMember> ActiveMembers => Members.Values.Where(m => !m.IsInactive);

        public int PlayerCount => Members.Count;

        public RoomMember? FindInactiveByUserId(string? userId) =>
            userId == null ? null
                : Members.Values.FirstOrDefault(m => m.IsInactive && m.UserId == userId);

        // ── IRoom (plugin-facing read-only view) ─────────────────────────────
        string IRoom.Name       => Name;
        int    IRoom.MaxPlayers => MaxPlayers;
        bool   IRoom.IsOpen     => IsOpen;
        bool   IRoom.IsVisible  => IsVisible;
        int    IRoom.MasterActor => MasterClientActor;
        int    IRoom.PlayerCount => PlayerCount;
        IReadOnlyDictionary<object, object?> IRoom.Properties => Properties;
        IReadOnlyList<IPlayer> IRoom.Players =>
            Members.Values.OrderBy(m => m.ActorNr).Cast<IPlayer>().ToList();
    }

    public sealed class RoomMember : IPlayer
    {
        public int     ActorNr;
        public Peer    Peer = null!;
        public string? UserId;
        public bool    IsInactive;
        public long    DeactivatedMs;
        public readonly Dictionary<object, object?> Properties = new();

        // ── IPlayer (plugin-facing read-only view) ───────────────────────────
        int     IPlayer.ActorNr    => ActorNr;
        string? IPlayer.UserId     => UserId;
        bool    IPlayer.IsInactive => IsInactive;
        string? IPlayer.NickName   => Properties.TryGetValue((byte)255, out var n) ? n as string : null;
        string? IPlayer.Address    => Peer?.EndPoint?.ToString();
        bool    IPlayer.IsV18      => Peer?.V18 ?? false;
        IReadOnlyDictionary<object, object?> IPlayer.Properties => Properties;
    }

    // A cached room event awaiting replay to future joiners.
    public sealed class CachedEvent
    {
        public byte    Code;
        public object? Data;
        public int     Sender;
        public bool    Global;   // AddToRoomCacheGlobal survives the sender leaving
    }
}
