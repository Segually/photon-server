using System;
using System.Collections.Generic;

namespace PhotonServer.Api
{
    // A plugin is a class in a DLL dropped into the server's plugins directory.
    // The server scans for public parameterless-constructible IPlugin types at
    // startup and calls Initialize once, handing over the host. Register hooks
    // and console keys from there.
    public interface IPlugin
    {
        string Name { get; }
        void Initialize(IPluginHost host);
    }

    // The server surface plugins build tools on top of. Every method is safe to
    // call from any thread (a plugin's own console/admin thread, a webhook
    // callback, or reentrantly from inside a hook) — the server serializes them
    // on its global lock. Room/actor lookups that miss are no-ops returning false.
    public interface IPluginHost
    {
        HookRegistry Hooks { get; }

        // ── Inspect ──────────────────────────────────────────────────────────
        IReadOnlyList<IRoom> Rooms { get; }
        IRoom? GetRoom(string name);

        // ── Rooms: create / delete / overwrite ───────────────────────────────
        // Create an ownerless server-side room (no master client until a real
        // player joins). Returns the existing room if the name is taken.
        IRoom CreateRoom(string name, int maxPlayers = 0, bool isOpen = true, bool isVisible = true);
        // Remove a room; its remaining members are notified (Leave) and dropped.
        bool DeleteRoom(string name);
        // Atomically replace a room's limits/flags and merge extra properties,
        // broadcasting the changes. Creates the room if absent.
        IRoom OverwriteRoom(string name, int? maxPlayers = null, bool? isOpen = null,
            bool? isVisible = null, IReadOnlyDictionary<object, object?>? properties = null);

        // ── Rooms: mutate (each broadcasts to clients) ───────────────────────
        bool SetMaxPlayers(string room, int maxPlayers);
        bool SetRoomOpen(string room, bool open);
        bool SetRoomVisible(string room, bool visible);
        bool SetRoomProperty(string room, object key, object? value);

        // ── Players: mutate / moderate (each broadcasts to clients) ──────────
        bool SetPlayerProperty(string room, int actorNr, object key, object? value);
        bool SetPlayerNickName(string room, int actorNr, string nickName);
        bool KickPlayer(string room, int actorNr);          // remove from room, keep connection
        bool DisconnectPlayer(string room, int actorNr);    // drop the UDP peer entirely

        // ── Messaging ────────────────────────────────────────────────────────
        void BroadcastEvent(string room, byte eventCode, object? data);
        bool SendEvent(string room, int actorNr, byte eventCode, object? data);

        // ── Host services (primitives a plugin can build a UI/logger on) ─────
        void Log(string message);
        void SuspendLog();   // buffer console log output (e.g. while a plugin draws a menu)
        void ResumeLog();    // flush the buffered lines and resume live logging
        // Register a hotkey handled on the server's console-reader thread. The
        // handler runs on that thread and may use System.Console freely for its
        // own interactive UI; server packet handling continues on other threads.
        void RegisterConsoleKey(char key, Action handler);
    }
}
