using PhotonServer;

int port = 5055;
if (args.Length >= 1 && int.TryParse(args[0], out int p))
    port = p;

Console.WriteLine("photon-server (protocol probe build)");
new PhotonServer.PhotonServer(port).Run();
