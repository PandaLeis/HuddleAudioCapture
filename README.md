# Huddle Audio Capture

Version `0.7.0`

Huddle Audio Capture is a Windows helper that captures local computer/system audio with WASAPI loopback and exposes a local-only HTTP bridge.

Phase 6B makes Windows computer/system audio an alternate audio source for the existing Huddle AI Scribe transcription process. It does not replace the working Power Apps microphone path and does not add new AI Scribe parsing, SharePoint writes, Power Apps formulas, PCF, Microsoft Graph, or Teams transcription APIs.

## Phase 6B - AI Scribe Alternate Audio Source

Power Apps microphone recording remains supported in the existing app. The Windows helper adds an alternate path:

```text
Windows computer/system audio
-> HuddleAudioCapture WAV recording
-> existing TranscribeHuddleAudio Power Automate flow
-> Azure Speech
-> transcript returned to the Windows helper for diagnostics
```

Downstream AI Scribe logic remains unchanged.

The helper continues to use the `huddlescribe://` custom protocol as the Power Apps -> Windows control mechanism:

```text
huddlescribe://start/<sessionId>
huddlescribe://stop/<sessionId>
```

`huddlescribe://start/<sessionId>` sends a start command to the already-running local bridge. `huddlescribe://stop/<sessionId>` stops the matching recording through the local bridge, then asks the bridge to submit the finalized WAV to the configured transcription flow.

PCF is not required for Phase 6B.

If a protocol command is received while the helper UI is not already running, the short-lived protocol process launches the normal helper UI, waits for the localhost bridge to become ready, and then forwards the command. A named mutex prevents multiple normal helper UI instances from running at the same time.

## Phase 6C - Transcript Handoff Architecture

Phase 6C keeps Windows focused on capture and transcription submission. HuddleAudioCapture does not directly set a Power Apps variable and does not write directly to SharePoint.

```text
Power Apps creates sessionId
-> Windows captures audio
-> existing TranscribeHuddleAudio Power Automate flow transcribes audio
-> Flow stores transcript in a cloud staging record keyed by sessionId
-> Power Apps retrieves transcript by sessionId
-> existing AI Scribe logic continues
```

HuddleAudioCapture requires no SharePoint credentials and does not write directly to SharePoint.

The transcript returned to the Windows helper remains available for diagnostics. It does not replace the cloud staging record that Power Apps will retrieve by `sessionId`.

The Windows-to-Flow request contract is:

```json
{
  "sessionId": "<GUID>",
  "fileName": "HuddleRecording_<GUID>.wav",
  "language": "en-US",
  "source": "ComputerAudio",
  "audioBase64": "<base64 WAV>"
}
```

The `source` field is an explicit Phase 6C source indicator. The existing Flow should treat it as optional metadata and continue accepting the original request fields.

## Transcription Configuration

The Power Automate HTTP trigger URL is sensitive and must not be committed to GitHub.

Configuration resolution order:

1. Process environment variable: `HUDDLE_TRANSCRIPTION_FLOW_URL`
2. User environment variable: `HUDDLE_TRANSCRIPTION_FLOW_URL`
3. User configuration file: `%APPDATA%\HuddleAudioCapture\appsettings.json`

Example user configuration file:

```json
{
  "huddleTranscriptionFlowUrl": "<REDACTED>"
}
```

An example template is included as `appsettings.example.json`.

The publish output includes `install-huddlescribe-protocol.ps1`. To register the protocol and write production transcription configuration:

```powershell
.\install-huddlescribe-protocol.ps1 -TranscriptionFlowUrl "<REDACTED>"
```

To remove the per-user protocol registration:

```powershell
.\install-huddlescribe-protocol.ps1 -Uninstall
```

## Local Bridge

The helper listens only on:

```text
http://127.0.0.1:17843
```

It does not bind to `0.0.0.0` and does not expose itself to the local network. If port `17843` is unavailable, the app reports an error rather than choosing another port.

## Temporary Recording Cache

Recordings are temporary and stored under:

```text
%TEMP%\HuddleAudioCapture
```

Recording filenames use the supplied session ID:

```text
HuddleRecording_<SessionID>.wav
HuddleRecording_<SessionID>.json
```

Stale temporary recording files older than 24 hours are cleaned up on startup.

## Security

On each launch, the app generates a cryptographically random bridge token.

`GET /health` is unauthenticated for proof-of-concept diagnostics.

All recording control/data endpoints require:

```text
X-Huddle-Bridge-Token: <token>
```

The token is displayed in the diagnostic UI and written locally for test scripts:

```text
%TEMP%\HuddleAudioCapture\bridge-token.txt
```

No Azure, Microsoft, SharePoint, or Power Platform credentials are stored.

## Endpoints

```text
GET    /health
POST   /recording/start
POST   /recording/stop
GET    /recording/{sessionId}/status
GET    /recording/{sessionId}/audio
POST   /recording/{sessionId}/transcribe
DELETE /recording/{sessionId}
```

Start request:

```json
{
  "sessionId": "<GUID>"
}
```

Stop request:

```json
{
  "sessionId": "<GUID>"
}
```

`GET /recording/{sessionId}/audio` returns raw WAV bytes:

```text
Content-Type: audio/wav
```

`POST /recording/{sessionId}/transcribe` submits the finalized WAV to the configured `TranscribeHuddleAudio` flow and returns:

```json
{
  "success": true,
  "sessionId": "<GUID>",
  "status": "Transcription complete.",
  "transcript": "<transcript text>"
}
```

Only one active recording is supported in Phase 6A. A second start request returns `409 Conflict`.

## CORS Diagnostics

The bridge supports `OPTIONS` requests and logs the `Origin` header to:

```text
%TEMP%\HuddleAudioCapture\bridge.log
```

Allowed origins are configurable:

```powershell
$env:HUDDLE_BRIDGE_ALLOWED_ORIGINS = "https://apps.powerapps.com"
```

The bridge does not use `Access-Control-Allow-Origin: *`.

## Build

```powershell
dotnet build -c Release
```

## Run

Launch the desktop helper:

```powershell
dotnet run
```

Launch bridge-only diagnostic mode:

```powershell
dotnet run -- --bridge
```

## Publish

```powershell
.\publish-win-x64.ps1
```

Launch the self-contained executable:

```powershell
.\publish\win-x64\HuddleAudioCapture.exe
```

## Test Script

With Huddle Audio Capture running:

```powershell
.\test-local-bridge.ps1
```

For the full Phase 6B recording and transcription path:

```powershell
.\test-phase-6b.ps1
```

Use `-SkipTranscription` to test only the Windows recording/bridge/audio portion when the Power Automate flow URL is not configured:

```powershell
.\test-phase-6b.ps1 -SkipTranscription
```

Or pass the token explicitly:

```powershell
.\test-local-bridge.ps1 -BridgeToken "<token>"
```

The script:

1. Calls `GET /health`
2. Generates a GUID
3. Prompts you to start playing computer audio
4. Starts recording
5. Waits 10 seconds
6. Checks status
7. Stops recording
8. Downloads the WAV as `bridge-test.wav`
9. Prints the file size
10. Leaves the WAV available for playback testing
11. Deletes the temporary bridge recording only if you confirm

Manual URI validation:

```text
huddlescribe://start/<GUID>
huddlescribe://stop/<GUID>
huddlescribe://invalid/<GUID>
```

Expected behavior:

1. `start` launches or reuses the helper and begins computer-audio capture.
2. `stop` stops the matching session and submits it for transcription.
3. Invalid commands are rejected and logged without starting a recording.
4. A second normal helper launch exits without creating another bridge/recorder instance.

## Phase 6A Success Criteria

Phase 6A is successful when:

1. Huddle Audio Capture launches normally.
2. `GET /health` returns HTTP 200.
3. `POST /recording/start` starts real computer-audio capture.
4. `POST /recording/stop` produces a valid WAV.
5. `GET /recording/{sessionId}/audio` downloads that WAV.
6. The downloaded WAV can be played and contains computer audio.
7. `DELETE /recording/{sessionId}` removes the temporary recording.
8. The existing manual recording controls still work.

## Phase 6B Success Criteria

Phase 6B is successful when:

1. Huddle Audio Capture still records Windows system audio.
2. `huddlescribe://start/{sessionId}` starts the correct recording.
3. `huddlescribe://stop/{sessionId}` stops the correct recording.
4. The resulting WAV is valid and contains audible computer audio.
5. The recording can be submitted to the existing `TranscribeHuddleAudio` flow.
6. The returned session ID matches the recording session ID.
7. The transcript is non-empty.
8. No new downstream AI Scribe logic is added to Windows.
9. The helper remains localhost-only.
10. Sensitive data is not logged.
