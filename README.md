# Huddle Audio Capture

Version `0.6.0`

Huddle Audio Capture is a Windows helper that captures local computer/system audio with WASAPI loopback and exposes a local-only HTTP bridge proof of concept.

Phase 6A only proves that a local API can control the working recorder. It does not implement PCF, Azure Speech, SharePoint, Power Automate, or Power Apps business logic.

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
