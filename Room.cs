using System;
using System.Collections.Generic;
using System.Linq;

namespace PhotonServer
{
    // A room (Photon "game") and its actors. All access is serialized by the
    // server's global lock, so no internal locking here.
    public sealed class Room
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
    }

    public sealed class RoomMember
    {
        public int     ActorNr;
        public Peer    Peer = null!;
        public string? UserId;
        public bool    IsInactive;
        public long    DeactivatedMs;
        public readonly Dictionary<object, object?> Properties = new();
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
