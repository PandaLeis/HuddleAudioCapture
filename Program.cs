using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\HuddleAudioCapture.WindowsHelper";
    private const string TokenHeader = "X-Huddle-Bridge-Token";
    private static readonly TimeSpan BridgeStartupTimeout = TimeSpan.FromSeconds(20);

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
            return RunUi();
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
        return RunUi();
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

            if (!Guid.TryParse(sessionId, out _))
            {
                ShowUriError(
                    "The Huddle Scribe session ID was not valid."
                );

                BridgeLogger.Log($"Protocol command rejected invalid sessionId=\"{sessionId}\"");

                return 1;
            }

            BridgeLogger.Log($"Protocol command received command={command} sessionId={sessionId}");


            // --------------------------------------------------------
            // TOKEN FILE EXISTS WHILE THE MAIN HELPER / BRIDGE IS
            // RUNNING. IF THE PROTOCOL LAUNCHED THIS SHORT-LIVED
            // PROCESS FIRST, START THE UI HELPER AND WAIT FOR THE
            // LOCAL BRIDGE.
            // --------------------------------------------------------
            await EnsureHelperIsRunningAsync();


            var bridgeToken =
                await ReadBridgeTokenAsync();


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
                        TimeSpan.FromMinutes(3)
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
                BridgeLogger.Log($"Protocol start received sessionId={sessionId}");

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
                BridgeLogger.Log($"Protocol stop received sessionId={sessionId}");

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

                using var transcribeResponse =
                    await http.PostAsync(
                        BuildBridgeUrl(
                            $"recording/{Uri.EscapeDataString(sessionId)}/transcribe"
                        ),
                        new StringContent(
                            "",
                            Encoding.UTF8,
                            "application/json"
                        )
                    );


                if (!transcribeResponse.IsSuccessStatusCode)
                {
                    var error =
                        await transcribeResponse.Content
                            .ReadAsStringAsync();

                    ShowUriError(
                        "Huddle Scribe recording stopped, but transcription failed."
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
    // NORMAL UI ENTRYPOINT
    // ================================================================
    private static int RunUi()
    {
        using var mutex =
            new Mutex(
                initiallyOwned: true,
                name: SingleInstanceMutexName,
                createdNew: out var createdNew
            );

        if (!createdNew)
        {
            BridgeLogger.Log("UI launch ignored because another helper instance is already running.");
            return 0;
        }

        BridgeLogger.Log("Application startup");
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());

        return 0;
    }


    private static async Task EnsureHelperIsRunningAsync()
    {
        if (await BridgeIsReadyAsync())
        {
            return;
        }

        BridgeLogger.Log("Local bridge unavailable; launching UI helper for protocol command.");

        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                    Arguments = "--ui",
                    UseShellExecute = true
                });
        }
        catch (Exception ex)
        {
            BridgeLogger.Log($"Unable to launch UI helper: {ex.Message}");
            throw new InvalidOperationException(
                "Huddle Audio Capture could not be started.",
                ex);
        }

        var deadline =
            DateTimeOffset.UtcNow.Add(BridgeStartupTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500);

            if (await BridgeIsReadyAsync())
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "Huddle Audio Capture started, but its local bridge did not become ready in time.");
    }


    private static async Task<bool> BridgeIsReadyAsync()
    {
        if (!File.Exists(AppInfo.BridgeTokenFilePath))
        {
            return false;
        }

        var bridgeToken = await ReadBridgeTokenAsync();
        if (string.IsNullOrWhiteSpace(bridgeToken))
        {
            return false;
        }

        try
        {
            using var handler =
                new HttpClientHandler
                {
                    UseProxy = false
                };

            using var http =
                new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(2)
                };

            http.DefaultRequestHeaders.Add(TokenHeader, bridgeToken);

            using var healthResponse =
                await http.GetAsync(BuildBridgeUrl("health"));

            return healthResponse.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            BridgeLogger.Log($"Local bridge readiness check failed: {ex.Message}");
            return false;
        }
    }


    private static async Task<string> ReadBridgeTokenAsync()
    {
        if (!File.Exists(AppInfo.BridgeTokenFilePath))
        {
            return "";
        }

        return
            (
                await File.ReadAllTextAsync(
                    AppInfo.BridgeTokenFilePath
                )
            ).Trim();
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

        BridgeLogger.Log($"Bridge-only mode started url={bridgeServer.Url} version={AppInfo.Version}");
        BridgeLogger.Log($"Bridge-only token file={AppInfo.BridgeTokenFilePath}");

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
