namespace PhotonServer
{
    // Photon operation codes (client → server).
    public static class OpCode
    {
        public const byte JoinLobby      = 229;
        public const byte LeaveLobby     = 228;
        public const byte CreateGame     = 227;
        public const byte JoinGame       = 226;
        public const byte JoinRandomGame = 225;
        public const byte Leave          = 254;
        public const byte RaiseEvent     = 253;
        public const byte SetProperties  = 252;
        public const byte GetProperties  = 251;
        public const byte ChangeGroups   = 248;
        public const byte Authenticate   = 230;
        public const byte GetRegions     = 220; // NameServer
        public const byte AuthenticateOnce = 231; // PUN2 AuthMode.AuthOnce
    }

    // Photon event codes (server → client).
    public static class EvCode
    {
        public const byte GameList          = 230; // full room list
        public const byte GameListUpdate    = 229; // room list delta
        public const byte AppStats          = 226;
        public const byte Join              = 255;
        public const byte Leave             = 254;
        public const byte PropertiesChanged = 253;
    }

    // Photon parameter codes (keys inside op/event parameter dictionaries).
    public static class Param
    {
        public const byte Address          = 230;
        public const byte JoinMode         = 215; // 0 = Default, 1 = CreateIfNotExists, 2 = JoinOrRejoin, 3 = RejoinOnly (verified: Stumble Guys JoinMode enum)
        public const byte Region           = 210; // GetRegions: region codes (string[])
        public const byte GameId           = 255; // room name
        public const byte ActorNr          = 254;
        public const byte TargetActorNr    = 253;
        public const byte ActorList        = 252;
        public const byte Properties       = 251;
        public const byte PlayerProperties = 249;
        public const byte GameProperties   = 248;
        public const byte Cache            = 247;
        public const byte ReceiverGroup    = 246;
        public const byte Data             = 245;
        public const byte Code             = 244;
        public const byte Broadcast        = 250;
        public const byte Secret           = 221;
        public const byte AppVersion       = 220;
        public const byte AppId            = 224;
        public const byte UserId           = 225;
        public const byte RoomOptionCleanup= 241;
        public const byte MasterClientId   = 203; // leave-event param: new master actor
        // Well-known GAME-PROPERTY key (inside the room-properties hashtable) that
        // carries the master client's actor number. The client caches it into
        // Room.MasterClientId, which drives IsMasterClient. Numerically equals the
        // GameProperties container code but lives in a different scope.
        public const byte MasterClientKey  = 248; // GamePropertyKey.MasterClientId
        public const byte RoomFlags        = 191; // room option bit flags
        public const byte IsInactive       = 233;
        public const byte EmptyRoomTtl     = 236;
        public const byte PlayerTtl        = 235;
        public const byte SuppressRoomEvents = 237;
    }

    // RaiseEvent receiver groups.
    public static class ReceiverGroup
    {
        public const byte Others = 0; // everyone except sender (default)
        public const byte All    = 1; // everyone including sender
        public const byte Master = 2; // master client only
    }

    // RaiseEvent caching options (param 247).
    public static class EventCaching
    {
        public const byte DoNotCache             = 0;
        public const byte AddToRoomCache         = 4;
        public const byte AddToRoomCacheGlobal   = 5;
        public const byte RemoveFromRoomCache    = 6;
        public const byte RemoveFromRoomCacheForActorsLeft = 7;
    }
}
