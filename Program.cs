using System;
using System.Collections.Generic;
using System.IO;
using PhotonServer;

// Self-test the V18 codec without a network round-trip.
if (args.Length == 1 && args[0] == "selftest")
{
    SelfTest.RunV18();
    return;
}

// Config lives next to the executable's working directory. A default config.json
// is written on first run.
string configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.json");
var config = ServerConfig.Load(configPath);

// A single port list may also be passed on the command line to override config.
if (args.Length > 0)
    config.Ports = Array.ConvertAll(args, a => int.Parse(a));

Console.WriteLine("photon-server");
new PhotonServer.PhotonServer(config).Run();
