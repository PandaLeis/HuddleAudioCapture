using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string TokenHeader = "X-Huddle-Bridge-Token";

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        // ============================================================
        // HUDDLE SCRIBE CUSTOM URI
        //
        // Examples:
        //   huddlescribe://start/{sessionId}
        //   huddlescribe://stop/{sessionId}
        //
        // The URI handler is a short-lived process.
        // It sends the requested command to the already-running
        // local bridge at 127.0.0.1:17843.
        // ============================================================
        if (
            args.Length > 0
            &&
            args[0].StartsWith(
                "huddlescribe://",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return await RunUriCommandAsync(args[0]);
        }


        // ============================================================
        // NORMAL WINDOWS UI
        // ============================================================
        if (
            args.Length > 0
            &&
            args[0].Equals(
                "--ui",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());

            return 0;
        }


        // ============================================================
        // BRIDGE-ONLY MODE
        // ============================================================
        if (
            args.Length > 0
            &&
            args[0].Equals(
                "--bridge",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return await RunBridgeOnlyAsync();
        }


        // ============================================================
        // COMMAND-LINE MODE
        // ============================================================
        if (args.Length > 0)
        {
            if (
                args[0].Equals(
                    "--cli",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                args = args.Skip(1).ToArray();
            }

            return await CliRunner.RunAsync(args);
        }


        // ============================================================
        // DEFAULT = WINDOWS UI
        // ============================================================
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());

        return 0;
    }


    // ================================================================
    // CUSTOM URI COMMAND HANDLER
    // ================================================================
    private static async Task<int> RunUriCommandAsync(
        string rawUri
    )
    {
        try
        {
            if (
                !Uri.TryCreate(
                    rawUri,
                    UriKind.Absolute,
                    out var uri
                )
            )
            {
                ShowUriError(
                    "The Huddle Scribe command was not valid."
                );

                return 1;
            }


            // --------------------------------------------------------
            // URI structure:
            //
            // huddlescribe://start/ABC
            //
            // Host         = start
            // AbsolutePath = /ABC
            // --------------------------------------------------------
            var command =
                uri.Host
                    .Trim()
                    .ToLowerInvariant();


            var sessionId =
                Uri.UnescapeDataString(
                    uri.AbsolutePath.Trim('/')
                );


            if (
                command != "start"
                &&
                command != "stop"
            )
            {
                ShowUriError(
                    $"Unknown Huddle Scribe command: {command}"
                );

                return 1;
            }


            if (string.IsNullOrWhiteSpace(sessionId))
            {
                ShowUriError(
                    "The Huddle Scribe command did not contain a session ID."
                );

                return 1;
            }


            // --------------------------------------------------------
            // TOKEN FILE EXISTS WHILE THE MAIN HELPER / BRIDGE
            // IS RUNNING.
            // --------------------------------------------------------
            if (
                !File.Exists(
                    AppInfo.BridgeTokenFilePath
                )
            )
            {
                ShowUriError(
                    "Huddle Audio Capture is not running.\n\n"
                    +
                    "Start Huddle Audio Capture and try again."
                );

                return 1;
            }


            var bridgeToken =
                (
                    await File.ReadAllTextAsync(
                        AppInfo.BridgeTokenFilePath
                    )
                ).Trim();


            if (string.IsNullOrWhiteSpace(bridgeToken))
            {
                ShowUriError(
                    "The Huddle Audio Capture bridge token is unavailable."
                );

                return 1;
            }


            using var handler =
                new HttpClientHandler
                {
                    UseProxy = false
                };


            using var http =
                new HttpClient(handler)
                {
                    Timeout =
                        TimeSpan.FromSeconds(15)
                };


            http.DefaultRequestHeaders.Add(
                TokenHeader,
                bridgeToken
            );


            // --------------------------------------------------------
            // VERIFY LOCAL BRIDGE FIRST
            // --------------------------------------------------------
            try
            {
                using var healthResponse =
                    await http.GetAsync(
                        BuildBridgeUrl("health")
                    );

                if (!healthResponse.IsSuccessStatusCode)
                {
                    ShowUriError(
                        "Huddle Audio Capture is running, but its local bridge is not ready."
                    );

                    return 1;
                }
            }
            catch (Exception ex)
            {
                ShowUriError(
                    "Unable to connect to Huddle Audio Capture."
                    +
                    Environment.NewLine
                    +
                    Environment.NewLine
                    +
                    "Bridge: "
                    +
                    BuildBridgeUrl("health")
                    +
                    Environment.NewLine
                    +
                    Environment.NewLine
                    +
                    ex.Message
                );

                return 1;
            }


            // --------------------------------------------------------
            // START
            // --------------------------------------------------------
            if (command == "start")
            {
                using var response =
                    await http.PostAsync(
                        BuildBridgeUrl(
                            "recording/start"
                        ),
                        BuildSessionContent(sessionId)
                    );


                if (!response.IsSuccessStatusCode)
                {
                    var error =
                        await response.Content
                            .ReadAsStringAsync();

                    ShowUriError(
                        "Unable to start Huddle Scribe recording."
                        +
                        Environment.NewLine
                        +
                        Environment.NewLine
                        +
                        error
                    );

                    return 1;
                }


                return 0;
            }


            // --------------------------------------------------------
            // STOP
            // --------------------------------------------------------
            if (command == "stop")
            {
                using var response =
                    await http.PostAsync(
                        BuildBridgeUrl(
                            "recording/stop"
                        ),
                        BuildSessionContent(sessionId)
                    );


                if (!response.IsSuccessStatusCode)
                {
                    var error =
                        await response.Content
                            .ReadAsStringAsync();

                    ShowUriError(
                        "Unable to stop Huddle Scribe recording."
                        +
                        Environment.NewLine
                        +
                        Environment.NewLine
                        +
                        error
                    );

                    return 1;
                }


                return 0;
            }


            return 1;
        }
        catch (Exception ex)
        {
            ShowUriError(
                "Huddle Scribe command failed."
                +
                Environment.NewLine
                +
                Environment.NewLine
                +
                ex.Message
            );

            return 1;
        }
    }


    // ================================================================
    // BUILD LOCAL BRIDGE URL
    // ================================================================
    private static string BuildBridgeUrl(
        string relativePath
    )
    {
        return
            AppInfo.BridgeUrl.TrimEnd('/')
            +
            "/"
            +
            relativePath.TrimStart('/');
    }


    private static StringContent BuildSessionContent(
        string sessionId
    )
    {
        var json =
            JsonSerializer.Serialize(
                new
                {
                    sessionId
                }
            );

        return new StringContent(
            json,
            Encoding.UTF8,
            "application/json"
        );
    }


    // ================================================================
    // URI ERROR MESSAGE
    //
    // We intentionally show nothing when Start / Stop succeeds.
    // Normal Power Apps usage should feel seamless.
    // ================================================================
    private static void ShowUriError(
        string message
    )
    {
        MessageBox.Show(
            message,
            "Huddle Audio Capture",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }


    // ================================================================
    // EXISTING BRIDGE-ONLY MODE
    // ================================================================
    private static async Task<int> RunBridgeOnlyAsync()
    {
        using var recordingService =
            new LocalRecordingService();

        recordingService.CleanupStaleFiles();


        Directory.CreateDirectory(
            AppInfo.TempFolder
        );


        var bridgeToken =
            BridgeToken.Create();


        File.WriteAllText(
            AppInfo.BridgeTokenFilePath,
            bridgeToken
        );


        using var bridgeServer =
            new LocalBridgeServer(
                recordingService,
                bridgeToken
            );


        bridgeServer.Start();


        Console.WriteLine(
            $"Local bridge running: {bridgeServer.Url}"
        );

        Console.WriteLine(
            $"Version: {AppInfo.Version}"
        );

        Console.WriteLine(
            $"Token file: {AppInfo.BridgeTokenFilePath}"
        );

        Console.WriteLine(
            "Press Ctrl+C to stop."
        );


        var stopped =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );


        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopped.TrySetResult();
        };


        await stopped.Task;

        return 0;
    }
}
