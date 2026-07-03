using PhotonServer;

// Bind the standard Photon UDP ports (name server / master / game = 27000/27001/27002)
// plus 5055 for clients configured with a custom master port. Extra ports may be
// passed as CLI args.
int[] ports = args.Length > 0
    ? Array.ConvertAll(args, a => int.Parse(a))
    : new[] { 5055, 27000, 27001, 27002 };

Console.WriteLine("photon-server (protocol probe build)");
new PhotonServer.PhotonServer(ports).Run();
