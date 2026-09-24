using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

// ---------------------------------------------------------------------------------------
// STEP 7, PART 1: a reachability probe, not the sync server yet.
//
// Before writing anything real, we need to know one thing: can the phone reach this PC at
// all? Two things commonly stop it, and neither is anything we can fix in code:
//   - Windows treating the Wi-Fi as a Public network and blocking the port
//   - the router or hotspot isolating clients from each other
//
// Run this, open the address it prints on the phone, and you have your answer.
// ---------------------------------------------------------------------------------------

const int Port = 8787;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(Port);

    // Also plain port 80 if it is free, so the address is just the IP with nothing after it.
    // One less thing to mistype while we are working out why the phone cannot get through.
    try { options.ListenAnyIP(80); } catch { }
});

var app = builder.Build();

app.MapGet("/", () => Results.Content($"""
    <!doctype html>
    <html><head><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>PassVault</title></head>
    <body style="font-family:system-ui;background:#16181D;color:#E7EAEF;
                 display:grid;place-items:center;height:100vh;margin:0;text-align:center">
      <div>
        <div style="font-size:64px">&#9989;</div>
        <h1 style="margin:12px 0 6px">Your phone can reach your PC</h1>
        <p style="color:#8D95A3">{Environment.MachineName} &middot; {DateTime.Now:HH:mm:ss}</p>
      </div>
    </body></html>
    """, "text/html"));

app.MapGet("/ping", () => Results.Ok(new { ok = true, host = Environment.MachineName }));

Console.WriteLine("PassVault reachability probe\n");
Console.WriteLine("Open ONE of these on your phone, on the same Wi-Fi:\n");
foreach (var address in LanAddresses())
{
    Console.WriteLine($"    http://{address}/");
    Console.WriteLine($"    http://{address}:{Port}/");
}

Console.WriteLine($"    http://{Environment.MachineName.ToLowerInvariant()}.local:{Port}/");

Console.WriteLine("""

    If the page loads, we are in business.
    If it does not:
      - Windows may have asked to allow this app through the firewall. Say yes.
      - Your Wi-Fi is set to "Public", which blocks incoming connections.
      - A mobile hotspot may be keeping its devices from talking to each other.

    Ctrl+C to stop.
    """);

app.Run();

static IEnumerable<string> LanAddresses()
{
    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (nic.OperationalStatus != OperationalStatus.Up) continue;
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

        foreach (var ip in nic.GetIPProperties().UnicastAddresses)
        {
            if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (IPAddress.IsLoopback(ip.Address)) continue;
            if (ip.Address.ToString().StartsWith("169.254.")) continue;

            yield return ip.Address.ToString();
        }
    }
}
