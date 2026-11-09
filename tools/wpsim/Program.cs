using Watchpoint.Wpsim;

int port = 5090;
if (args.Length > 0 && int.TryParse(args[0], out var p))
{
    port = p;
}

var app = WpsimApp.Build(port);
await app.RunAsync();
