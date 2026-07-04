using System.Collections.Generic;

namespace PhotonServer.Api
{
    // Read-only views handed to plugins. State is owned by the server; plugins
    // observe it through these and mutate it through IPluginHost execute methods
    // (which apply the change AND broadcast it to clients under the server lock).
    // Never cache a view across turns — a room/player can be gone by the next call.

    public interface IRoom
    {
        string Name { get; }
        int    MaxPlayers { get; }   // 0 = unlimited (Photon convention)
        bool   IsOpen { get; }
        bool   IsVisible { get; }
        int    MasterActor { get; }
        int    PlayerCount { get; }

        // Custom + well-known room properties (byte keys 248..255 are reserved by
        // Photon: 255=maxPlayers, 254=isVisible, 253=isOpen, 248=masterClient …).
        IReadOnlyDictionary<object, object?> Properties { get; }

        // Every actor in the room, active and inactive (inactive players still
        // hold their slot until PlayerTtl elapses).
        IReadOnlyList<IPlayer> Players { get; }
    }

    public interface IPlayer
    {
        int     ActorNr { get; }
        string? UserId { get; }
        string? NickName { get; }    // player property 255, if the client set one
        string? Address { get; }     // "ip:port" of the live connection, null if inactive
        bool    IsInactive { get; }
        bool    IsV18 { get; }       // serialization protocol: true=GpBinaryV18, false=V16
        IReadOnlyDictionary<object, object?> Properties { get; }
    }
}
