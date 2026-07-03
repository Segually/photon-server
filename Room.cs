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

        // Custom room properties + the "props for lobby" whitelist.
        public readonly Dictionary<object, object?> Properties = new();
        public string[] PropsListedInLobby = Array.Empty<string>();

        public int MasterClientActor;
        private int _nextActor = 1;

        // actorNr → member
        public readonly Dictionary<int, RoomMember> Members = new();

        public Room(string name) { Name = name; }

        public int AddMember(Peer peer)
        {
            int actorNr = _nextActor++;
            Members[actorNr] = new RoomMember { ActorNr = actorNr, Peer = peer };
            if (MasterClientActor == 0) MasterClientActor = actorNr;
            return actorNr;
        }

        public void RemoveMember(int actorNr)
        {
            Members.Remove(actorNr);
            if (MasterClientActor == actorNr)
                MasterClientActor = Members.Keys.Count > 0 ? Members.Keys.Min() : 0;
        }

        public int[] ActorNumbers => Members.Keys.OrderBy(x => x).ToArray();

        public IEnumerable<RoomMember> Others(int exceptActor) =>
            Members.Values.Where(m => m.ActorNr != exceptActor);

        public int PlayerCount => Members.Count;
    }

    public sealed class RoomMember
    {
        public int  ActorNr;
        public Peer Peer = null!;
        public readonly Dictionary<object, object?> Properties = new();
    }
}
