namespace EverQuestStreamDeck
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using EqSpells.Core;

    // Entry point. The Stream Deck software launches this executable with the connection
    // details on the command line, so there is no service to register with and no host
    // object to inherit from - the plugin is simply a process that talks JSON.
    internal static class Program
    {
        private static async Task<Int32> Main(String[] args)
        {
            var dataDir = ResolveDataDirectory();
            var log = new FileLog(dataDir);

            try
            {
                var port = ArgInt(args, "-port");
                var uuid = Arg(args, "-pluginUUID");
                var registerEvent = Arg(args, "-registerEvent");
                if (port == 0 || uuid == null || registerEvent == null)
                {
                    // Run by hand rather than by the software. Say so instead of dying
                    // with an unhandled exception in a process nobody is watching.
                    Console.Error.WriteLine(
                        "This is a Stream Deck plugin; it is started by the Stream Deck software.\n" +
                        "Expected: -port <n> -pluginUUID <uuid> -registerEvent <event> -info <json>");
                    return 2;
                }

                if (!Win32Keyboard.LayoutIsSane)
                {
                    // Worth a loud line: when this is wrong, keys look fine and simply
                    // never cast anything.
                    log.Error("INPUT struct size is wrong for this architecture; keystrokes will not work");
                }

                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

                using var client = new StreamDeckClient(port, uuid, registerEvent);
                var reader = new SpellBarReader(log, dataDir);
                var keys = new SpellKeys(client, reader, log);
                using var updater = new IconUpdater(log, reader);

                client.WillAppear += (action, context, payload) =>
                {
                    keys.Remember(context, payload);
                    Forget(keys.RepaintAsync(force: true, cts.Token));
                };
                client.SettingsChanged += (action, context, payload) =>
                {
                    keys.Remember(context, payload);
                    Forget(keys.RepaintAsync(force: true, cts.Token));
                };
                client.WillDisappear += (action, context, payload) => keys.Forget(context);
                client.KeyDown += (action, context, payload) => Forget(keys.PressAsync(context, cts.Token));

                // Both the reader's own change notification and the updater's dumb
                // heartbeat land on the same push; the heartbeat exists because a key
                // that silently missed one repaint would otherwise stay wrong forever.
                reader.IconsChanged += (sender, e) => Forget(keys.RepaintAsync(force: false, cts.Token));
                updater.ForceRepaint = () => Forget(keys.RepaintAsync(force: true, cts.Token));

                log.Info($"Starting; data directory {dataDir}");

                // Rebuild last session's images before the first read, so keys are never
                // blank while the game is being located.
                _ = Task.Run(async () =>
                {
                    reader.RestoreImages();
                    await updater.RunAsync(full: false).ConfigureAwait(false);
                });
                updater.SetAutoUpdate(true);

                await client.RunAsync(cts.Token).ConfigureAwait(false);
                log.Info("Stream Deck closed the connection; exiting");
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                log.Error($"Fatal: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        // These handlers are called from the socket reader loop, which must not block on
        // a key repaint. Starting the task and not awaiting it is the point; this names
        // that intent and swallows the failure so one bad push cannot take the plugin
        // down through an unobserved exception.
        private static void Forget(Task task)
        {
            _ = task.ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // Stream Deck gives a plugin no data folder of its own. Deliberately NOT the
        // folder the Logitech plugin uses: the two would be separate processes writing
        // the same calibration file, and whichever wrote last would win.
        private static String ResolveDataDirectory()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EverQuestSpells", "StreamDeck");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static String Arg(String[] args, String name)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (String.Equals(args[i], name, StringComparison.Ordinal)) { return args[i + 1]; }
            }
            return null;
        }

        private static Int32 ArgInt(String[] args, String name) =>
            Int32.TryParse(Arg(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
