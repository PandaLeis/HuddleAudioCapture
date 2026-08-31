using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Net;

sealed class MainForm : Form
{
    private readonly ComboBox deviceComboBox = new();
    private readonly Button startButton = new();
    private readonly Button stopButton = new();
    private readonly Button newButton = new();
    private readonly Button sendButton = new();
    private readonly Button openFolderButton = new();
    private readonly Label bridgeValueLabel = new();
    private readonly Label tokenValueLabel = new();
    private readonly Label statusValueLabel = new();
    private readonly Label durationValueLabel = new();
    private readonly Label sessionValueLabel = new();
    private readonly Label fileValueLabel = new();
    private readonly Label peakValueLabel = new();
    private readonly ProgressBar peakProgressBar = new();
    private readonly TextBox transcriptTextBox = new();
    private readonly System.Windows.Forms.Timer durationTimer = new();
    private readonly IHuddleRecordingSender huddleSender = new HuddleRecordingSender();
    private readonly LocalRecordingService recordingService = new();
    private readonly string bridgeToken = BridgeToken.Create();

    private LocalBridgeServer? bridgeServer;
    private List<DeviceItem> devices = [];
    private RecordingSession? currentSession;
    private bool recordingCompleted;

    public MainForm()
    {
        Text = "Huddle Audio Capture - Computer Audio";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);
        Size = new Size(940, 640);

        BuildLayout();
        Load += (_, _) => InitializeApp();
        FormClosing += (_, _) => Cleanup();

        durationTimer.Interval = 500;
        durationTimer.Tick += (_, _) => UpdateDuration();
        recordingService.PeakAvailable += OnInputPeakAvailable;
        recordingService.StateChanged += OnRecordingStateChanged;
    }

    private void BuildLayout()
    {
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(16),
        };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(main);

        main.Controls.Add(new Label
        {
            Text = "Huddle Audio Capture",
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        }, 0, 0);

        var devicePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
        };
        devicePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        devicePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.Controls.Add(devicePanel, 0, 1);

        devicePanel.Controls.Add(new Label
        {
            Text = "Playback Device",
            AutoSize = true,
            Margin = new Padding(0, 6, 12, 8),
        }, 0, 0);

        deviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        deviceComboBox.Dock = DockStyle.Fill;
        deviceComboBox.Margin = new Padding(0, 0, 0, 8);
        devicePanel.Controls.Add(deviceComboBox, 1, 0);

        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 12),
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.Controls.Add(statusPanel, 0, 2);

        AddStatusRow(statusPanel, 0, "Local Bridge", bridgeValueLabel, "Starting...");
        AddStatusRow(statusPanel, 1, "Bridge Token", tokenValueLabel, bridgeToken);
        AddStatusRow(statusPanel, 2, "Status", statusValueLabel, "Ready");
        AddStatusRow(statusPanel, 3, "Recording Duration", durationValueLabel, "00:00:00");
        AddStatusRow(statusPanel, 4, "Active Session", sessionValueLabel, "");
        AddStatusRow(statusPanel, 5, "Current Recording", fileValueLabel, "");
        AddStatusRow(statusPanel, 6, "Peak Level", peakValueLabel, "0%");

        statusPanel.Controls.Add(new Label
        {
            Text = "Audio Activity",
            AutoSize = true,
            Margin = new Padding(0, 6, 12, 6),
        }, 0, 7);

        peakProgressBar.Dock = DockStyle.Fill;
        peakProgressBar.Maximum = 100;
        peakProgressBar.Margin = new Padding(0, 4, 0, 6);
        statusPanel.Controls.Add(peakProgressBar, 1, 7);

        var infoBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = $"Local-only bridge: {AppInfo.BridgeUrl}\r\nTemporary recordings: {AppInfo.TempFolder}\r\nSet HUDDLE_BRIDGE_ALLOWED_ORIGINS to the exact Power Apps origin during browser testing.",
        };
        main.Controls.Add(infoBox, 0, 3);

        var transcriptPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 12, 0, 0),
        };
        transcriptPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        transcriptPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.Controls.Add(transcriptPanel, 0, 4);

        transcriptPanel.Controls.Add(new Label
        {
            Text = "Transcript",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        }, 0, 0);

        transcriptTextBox.Dock = DockStyle.Fill;
        transcriptTextBox.Multiline = true;
        transcriptTextBox.ReadOnly = true;
        transcriptTextBox.ScrollBars = ScrollBars.Vertical;
        transcriptTextBox.Margin = new Padding(0);
        transcriptPanel.Controls.Add(transcriptTextBox, 0, 1);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 12, 0, 8),
        };
        main.Controls.Add(buttonPanel, 0, 5);

        ConfigureButton(startButton, "Start Recording", StartRecordingAsync);
        ConfigureButton(stopButton, "Stop Recording", StopRecordingAsync);
        ConfigureButton(newButton, "New Recording", NewRecordingAsync);
        ConfigureButton(sendButton, "Send to Huddle", SendToHuddleAsync);
        ConfigureButton(openFolderButton, "Open Temp Folder", OpenRecordingFolderAsync);

        stopButton.Enabled = false;
        sendButton.Enabled = false;

        buttonPanel.Controls.Add(startButton);
        buttonPanel.Controls.Add(stopButton);
        buttonPanel.Controls.Add(newButton);
        buttonPanel.Controls.Add(sendButton);
        buttonPanel.Controls.Add(openFolderButton);

        main.Controls.Add(new Label
        {
            Text = $"Version {AppInfo.Version}",
            Dock = DockStyle.Bottom,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = true,
        }, 0, 6);
    }

    private void InitializeApp()
    {
        try
        {
            recordingService.CleanupStaleFiles();
            Directory.CreateDirectory(AppInfo.TempFolder);
            File.WriteAllText(AppInfo.BridgeTokenFilePath, bridgeToken);
            LoadPlaybackDevices();

            bridgeServer = new LocalBridgeServer(recordingService, bridgeToken);
            bridgeServer.Start();
            bridgeValueLabel.Text = $"{bridgeServer.Url} (Running)";
        }
        catch (HttpListenerException ex)
        {
            SetStatus("Error");
            bridgeValueLabel.Text = "Error";
            ShowError($"Local bridge could not start on {AppInfo.BridgeUrl}: {ex.Message}");
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            ShowError($"Could not initialize Huddle Audio Capture: {ex.Message}");
            startButton.Enabled = false;
        }
    }

    private static void ConfigureButton(Button button, string text, Func<Task> handler)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Margin = new Padding(0, 0, 8, 8);
        button.Click += async (_, _) => await handler();
    }

    private static void AddStatusRow(TableLayoutPanel panel, int row, string label, Label valueLabel, string value)
    {
        valueLabel.Text = value;
        valueLabel.AutoSize = true;
        valueLabel.Margin = new Padding(0, 6, 0, 6);

        panel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Margin = new Padding(0, 6, 12, 6),
        }, 0, row);
        panel.Controls.Add(valueLabel, 1, row);
    }

    private void LoadPlaybackDevices()
    {
        try
        {
            var activeDevices = recordingService.GetPlaybackDevices();

            devices = activeDevices
                .Select((device, index) => new DeviceItem(index, device))
                .ToList();

            deviceComboBox.Items.Clear();
            foreach (var device in devices)
            {
                deviceComboBox.Items.Add(device);
            }

            if (devices.Count == 0)
            {
                SetStatus("Error");
                ShowError("No playback devices are available.");
                startButton.Enabled = false;
                return;
            }

            var defaultDevice = recordingService.GetDefaultPlaybackDevice();
            var defaultIndex = devices.FindIndex(device => device.Device.ID == defaultDevice.ID);
            deviceComboBox.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;
            SetStatus("Ready");
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            ShowError($"Could not load playback devices: {ex.Message}");
            startButton.Enabled = false;
        }
    }

    private async Task StartRecordingAsync()
    {
        try
        {
            if (deviceComboBox.SelectedItem is not DeviceItem selectedDevice)
            {
                ShowError("Select a playback device before recording.");
                return;
            }

            ResetDisplayForNewRecording();

            var sessionId = Guid.NewGuid().ToString();
            var result = recordingService.Start(sessionId, selectedDevice.Device);
            currentSession = recordingService.ActiveSession;
            sessionValueLabel.Text = result.SessionId;
            fileValueLabel.Text = currentSession?.AudioFilePath ?? "";

            recordingCompleted = false;
            durationTimer.Start();
            startButton.Enabled = false;
            stopButton.Enabled = true;
            sendButton.Enabled = false;
            deviceComboBox.Enabled = false;
            SetStatus("Recording...");
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            ShowError($"Recording could not start: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private async Task StopRecordingAsync()
    {
        if (currentSession is null)
        {
            ShowError("No recording is currently active.");
            return;
        }

        try
        {
            stopButton.Enabled = false;
            startButton.Enabled = false;
            sendButton.Enabled = false;
            durationTimer.Stop();

            await recordingService.StopAsync(currentSession.SessionId);
            currentSession = recordingService.GetCompletedSession(currentSession.SessionId);
            durationValueLabel.Text = currentSession.Duration.ToString("hh\\:mm\\:ss");
            recordingCompleted = true;
            sendButton.Enabled = true;
            SetStatus("Recording Complete");

            if (!currentSession.AudibleAudioDetected)
            {
                ShowError("No audible audio was detected. Verify Windows is playing audio through the selected playback device.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            ShowError($"Recording could not stop cleanly: {ex.Message}");
        }
        finally
        {
            startButton.Enabled = true;
            deviceComboBox.Enabled = true;
        }
    }

    private async Task SendToHuddleAsync()
    {
        if (!recordingCompleted || currentSession is null)
        {
            ShowError("Complete a recording before sending it to Huddle.");
            return;
        }

        try
        {
            if (!currentSession.AudibleAudioDetected)
            {
                ShowError("No audible audio was detected. Record audio before sending to Huddle.");
                return;
            }

            SetStatus("Transcribing...");
            sendButton.Enabled = false;
            startButton.Enabled = false;
            newButton.Enabled = false;

            var result = await huddleSender.SendAsync(currentSession.AudioFilePath, currentSession.SessionId);
            transcriptTextBox.Text = result.Transcript;
            SetStatus("Transcription Complete");
            MessageBox.Show("Transcription completed successfully.", "Huddle Audio Capture", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            ShowError($"Recording is not ready for Huddle: {ex.Message}");
        }
        finally
        {
            var isRecording = recordingService.Status == "recording";
            var hasCompletedAudibleRecording =
                recordingCompleted
                && currentSession is not null
                && currentSession.AudibleAudioDetected;

            startButton.Enabled = !isRecording && devices.Count > 0;
            newButton.Enabled = !isRecording;
            sendButton.Enabled = !isRecording && hasCompletedAudibleRecording;
            deviceComboBox.Enabled = !isRecording;
        }
    }

    private async Task NewRecordingAsync()
    {
        if (recordingService.Status == "recording")
        {
            ShowError("Stop the current recording before starting a new recording.");
            return;
        }

        currentSession = null;
        recordingCompleted = false;
        ResetDisplayForNewRecording();
        SetStatus("Ready");
        startButton.Enabled = devices.Count > 0;
        stopButton.Enabled = false;
        sendButton.Enabled = false;
        deviceComboBox.Enabled = true;
        await Task.CompletedTask;
    }

    private async Task OpenRecordingFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.TempFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppInfo.TempFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Error");
            ShowError($"Could not open temp folder: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private void OnInputPeakAvailable(float peak)
    {
        if (IsDisposed)
        {
            return;
        }

        BeginInvoke(() =>
        {
            var value = Math.Clamp((int)Math.Round(peak * 100), 0, 100);
            peakProgressBar.Value = value;
            peakValueLabel.Text = $"{value}%";
        });
    }

    private void OnRecordingStateChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        BeginInvoke(() =>
        {
            var active = recordingService.ActiveSession;
            sessionValueLabel.Text = active?.SessionId ?? sessionValueLabel.Text;
            fileValueLabel.Text = active?.AudioFilePath ?? fileValueLabel.Text;
        });
    }

    private void UpdateDuration()
    {
        if (currentSession is null)
        {
            var active = recordingService.ActiveSession;
            if (active is null)
            {
                return;
            }

            currentSession = active;
        }

        durationValueLabel.Text = currentSession.Duration.ToString("hh\\:mm\\:ss");
    }

    private void Cleanup()
    {
        durationTimer.Stop();
        bridgeServer?.Dispose();
        recordingService.Dispose();
    }

    private void ResetDisplayForNewRecording()
    {
        peakProgressBar.Value = 0;
        peakValueLabel.Text = "0%";
        durationValueLabel.Text = "00:00:00";
        sessionValueLabel.Text = "";
        fileValueLabel.Text = "";
        transcriptTextBox.Text = "";
    }

    private void SetStatus(string status)
    {
        statusValueLabel.Text = status;
    }

    private static void ShowError(string message)
    {
        MessageBox.Show(message, "Huddle Audio Capture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private sealed record DeviceItem(int Index, MMDevice Device)
    {
        public override string ToString() => $"{Index}: {Device.FriendlyName}";
    }
}
