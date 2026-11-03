using Vmc.Client;

// Two modes:
//   Vmc.Client.Sample listen [port]            prints what a performer sends (marionette side, default 39539)
//   Vmc.Client.Sample blink [host] [port]      makes a VMC performer blink (default 127.0.0.1:39540)
string mode = args.Length > 0 ? args[0] : "listen";
using CancellationTokenSource stop = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

if (mode == "blink")
{
    using VmcSender sender = new(
        args.Length > 1 ? args[1] : "127.0.0.1",
        args.Length > 2
            ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)
            : VmcMessages.PerformerPort
    );
    foreach (float value in new[] { 1f, 0f })
    {
        await sender.SendFrameAsync(
            [new BlendShapeValue("Blink", value), new BlendShapeApply()],
            stop.Token
        );
        await Task.Delay(150, stop.Token);
    }

    return 0;
}

using VmcReceiver receiver = new(
    args.Length > 1
        ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
        : VmcMessages.MarionettePort
);
Console.WriteLine($"listening on {receiver.Port}; Ctrl+C stops");
await foreach (IVmcMessage message in receiver.ReadAllAsync(stop.Token))
{
    Console.WriteLine(message);
}

return 0;
