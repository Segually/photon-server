using System;
using System.Collections.Generic;

namespace PhotonServer.Api
{
    // ── Cancellable / mutable hook contexts ──────────────────────────────────
    // A hook handler runs synchronously inside the server's global lock, so it
    // sees consistent state and may call IPluginHost execute methods reentrantly.
    // For the "…ing" (present-tense) hooks a handler may set Cancel=true to reject
    // the client action; ReturnCode/Message become the Photon OpResponse the
    // client receives. Mutable input fields (room name, properties, nick, event
    // data) let a handler rewrite the action instead of merely vetoing it.

    public sealed class AuthContext
    {
        public string  Address { get; }          // "ip:port"
        public string? UserId { get; }
        public bool    IsV18 { get; }
        public string? AppId { get; }
        public string? AppVersion { get; }

        public bool    Cancel { get; set; }
        public short   ReturnCode { get; set; } = 32755; // ErrorCode.InvalidAuthentication
        public string? Message { get; set; }

        public AuthContext(string address, string? userId, bool isV18, string? appId, string? appVersion)
        {
            Address = address; UserId = userId; IsV18 = isV18; AppId = appId; AppVersion = appVersion;
        }
    }

    public sealed class RoomCreateContext
    {
        public IPlayer Player { get; }
        public string  Name { get; set; }                 // mutable — rewrite the room name
        public int     MaxPlayers { get; set; }
        public bool    IsOpen { get; set; }
        public bool    IsVisible { get; set; }
        public IDictionary<object, object?> Properties { get; }  // mutable

        public bool    Cancel { get; set; }
        public short   ReturnCode { get; set; } = 32766;  // ErrorCode.GameIdAlreadyExists-ish generic
        public string? Message { get; set; }

        public RoomCreateContext(IPlayer player, string name, int maxPlayers, bool isOpen, bool isVisible,
            IDictionary<object, object?> properties)
        {
            Player = player; Name = name; MaxPlayers = maxPlayers; IsOpen = isOpen; IsVisible = isVisible;
            Properties = properties;
        }
    }

    public sealed class JoinContext
    {
        public IPlayer Player { get; }
        public IRoom   Room { get; }

        // Set true to let this player in even if the room is at its MaxPlayers /
        // server cap (the "bypass player limit for a special IP" use case).
        public bool    AllowOverCapacity { get; set; }

        public bool    Cancel { get; set; }
        public short   ReturnCode { get; set; } = 32765; // ErrorCode.GameFull
        public string? Message { get; set; }

        public JoinContext(IPlayer player, IRoom room) { Player = player; Room = room; }
    }

    public sealed class PropertiesContext
    {
        public IRoom   Room { get; }
        public IPlayer Setter { get; }
        public int     TargetActor { get; }   // 0 = room properties, else the target actor
        public IDictionary<object, object?> Properties { get; }  // mutable

        public bool    Cancel { get; set; }
        public short   ReturnCode { get; set; } = 32752; // ErrorCode.OperationNotAllowedInCurrentState-ish
        public string? Message { get; set; }

        public PropertiesContext(IRoom room, IPlayer setter, int targetActor,
            IDictionary<object, object?> properties)
        {
            Room = room; Setter = setter; TargetActor = targetActor; Properties = properties;
        }
    }

    public sealed class NickNameContext
    {
        public IRoom   Room { get; }
        public IPlayer Player { get; }
        public string? OldNick { get; }
        public string  NewNick { get; set; }   // mutable — rewrite / sanitize the nick

        public bool    Cancel { get; set; }
        public short   ReturnCode { get; set; } = 32752;
        public string? Message { get; set; }

        public NickNameContext(IRoom room, IPlayer player, string? oldNick, string newNick)
        {
            Room = room; Player = player; OldNick = oldNick; NewNick = newNick;
        }
    }

    public sealed class RaiseEventContext
    {
        public IRoom   Room { get; }
        public IPlayer Sender { get; }
        public byte    EventCode { get; set; }   // mutable
        public object? Data { get; set; }        // mutable
        public byte    ReceiverGroup { get; }

        public bool    Cancel { get; set; }      // drop the event entirely

        public RaiseEventContext(IRoom room, IPlayer sender, byte eventCode, object? data, byte receiverGroup)
        {
            Room = room; Sender = sender; EventCode = eventCode; Data = data; ReceiverGroup = receiverGroup;
        }
    }

    // ── Hook registry ────────────────────────────────────────────────────────
    // Plugins subscribe with += in Initialize. The server raises each via the
    // Raise* methods; a throwing handler is caught and logged so one bad plugin
    // can't take down the server or block later handlers.
    public sealed class HookRegistry
    {
        private readonly Action<string, Exception> _onError;
        public HookRegistry(Action<string, Exception> onError) { _onError = onError; }

        // Intercept (cancellable / mutable):
        public event Action<AuthContext>?       PlayerAuthenticating;
        public event Action<RoomCreateContext>? RoomCreating;
        public event Action<JoinContext>?       PlayerJoining;
        public event Action<PropertiesContext>? PropertiesChanging;
        public event Action<NickNameContext>?   NickNameChanging;
        public event Action<RaiseEventContext>? EventRaising;

        // Notify (past tense — observe only):
        public event Action<IRoom>?             RoomCreated;
        public event Action<IRoom>?             RoomDeleting;
        public event Action<string>?            RoomDeleted;       // room name (object already gone)
        public event Action<IRoom, IPlayer>?    PlayerJoined;
        public event Action<IRoom, IPlayer>?    PlayerLeft;
        public event Action<IPlayer>?           PlayerDisconnected;
        public event Action<RaiseEventContext>? EventRaised;
        public event Action<IPlayer>?           LobbyJoined;

        public void RaisePlayerAuthenticating(AuthContext c) => Fire(PlayerAuthenticating, c, nameof(PlayerAuthenticating));
        public void RaiseRoomCreating(RoomCreateContext c)   => Fire(RoomCreating, c, nameof(RoomCreating));
        public void RaisePlayerJoining(JoinContext c)        => Fire(PlayerJoining, c, nameof(PlayerJoining));
        public void RaisePropertiesChanging(PropertiesContext c) => Fire(PropertiesChanging, c, nameof(PropertiesChanging));
        public void RaiseNickNameChanging(NickNameContext c) => Fire(NickNameChanging, c, nameof(NickNameChanging));
        public void RaiseEventRaising(RaiseEventContext c)   => Fire(EventRaising, c, nameof(EventRaising));

        public void RaiseRoomCreated(IRoom r)                => Fire(RoomCreated, r, nameof(RoomCreated));
        public void RaiseRoomDeleting(IRoom r)               => Fire(RoomDeleting, r, nameof(RoomDeleting));
        public void RaiseRoomDeleted(string name)            => Fire(RoomDeleted, name, nameof(RoomDeleted));
        public void RaisePlayerJoined(IRoom r, IPlayer p)    => Fire(PlayerJoined, r, p, nameof(PlayerJoined));
        public void RaisePlayerLeft(IRoom r, IPlayer p)      => Fire(PlayerLeft, r, p, nameof(PlayerLeft));
        public void RaisePlayerDisconnected(IPlayer p)       => Fire(PlayerDisconnected, p, nameof(PlayerDisconnected));
        public void RaiseEventRaised(RaiseEventContext c)    => Fire(EventRaised, c, nameof(EventRaised));
        public void RaiseLobbyJoined(IPlayer p)              => Fire(LobbyJoined, p, nameof(LobbyJoined));

        private void Fire<T>(Action<T>? evt, T arg, string name)
        {
            if (evt == null) return;
            foreach (var d in evt.GetInvocationList())
                try { ((Action<T>)d)(arg); }
                catch (Exception e) { _onError($"hook {name}", e); }
        }

        private void Fire<T1, T2>(Action<T1, T2>? evt, T1 a1, T2 a2, string name)
        {
            if (evt == null) return;
            foreach (var d in evt.GetInvocationList())
                try { ((Action<T1, T2>)d)(a1, a2); }
                catch (Exception e) { _onError($"hook {name}", e); }
        }
    }
}
