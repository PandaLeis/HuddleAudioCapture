# Power Automate Transcript Staging

Phase 6C keeps the Windows helper focused on capture and transcription submission. The Power Automate flow owns the cloud staging record that Power Apps reads by `sessionId`.

## Request Contract

`TranscribeHuddleAudio` should continue accepting the existing microphone path and should also accept this computer-audio request:

```json
{
  "sessionId": "<GUID>",
  "fileName": "HuddleRecording_<GUID>.wav",
  "language": "en-US",
  "source": "ComputerAudio",
  "audioBase64": "<base64 WAV>"
}
```

Treat `source` as compatible metadata. Expected values are `Microphone` and `ComputerAudio`.

## SharePoint Staging List

Recommended list name:

```text
Huddle_AIScribeTranscript
```

Recommended columns:

```text
Title                         Single line text
AIScribe_SessionID            Single line text, required, indexed
AIScribe_Source               Choice or single line text
AIScribe_Status               Choice or single line text
AIScribe_Transcript           Multiple lines of text, plain text
AIScribe_Error                Multiple lines of text, plain text
AIScribe_FileName             Single line text
AIScribe_Language             Single line text
AIScribe_DateCompleted        Date/time
AIScribe_DateRetrieved        Date/time
AIScribe_Processed            Yes/No, default false
```

`AIScribe_SessionID` is the end-to-end correlation key and should be indexed because Power Apps will use it for lookup.

## Success Behavior

After Azure Speech returns a valid transcript, create or update exactly one staging record for the incoming `sessionId`.

Suggested values:

```text
Title                    AIScribe_<sessionId>
AIScribe_SessionID       incoming sessionId
AIScribe_Source          source, default ComputerAudio when blank
AIScribe_Status          Complete
AIScribe_Transcript      Azure transcript
AIScribe_Error           blank
AIScribe_FileName        incoming fileName
AIScribe_Language        incoming language
AIScribe_DateCompleted   utcNow()
AIScribe_Processed       false
```

Use `sessionId` as an idempotency key. If a record already exists for the same `AIScribe_SessionID`, update it instead of creating a duplicate.

## Failure Behavior

If transcription fails, still create or update the staging record for the same `sessionId`.

Suggested values:

```text
AIScribe_SessionID       incoming sessionId
AIScribe_Source          source, default ComputerAudio when blank
AIScribe_Status          Failed
AIScribe_Transcript      blank
AIScribe_Error           sanitized error message
AIScribe_DateCompleted   utcNow()
AIScribe_Processed       false
```

Do not write secrets, signed Flow URLs, access tokens, authentication values, raw exception internals, or full audio payloads into SharePoint or logs.

## Response To Windows

The Windows helper still displays the transcript for diagnostics. That diagnostic response does not replace the SharePoint staging record.

Recommended Flow response:

```json
{
  "success": true,
  "sessionId": "<GUID>",
  "status": "Transcription complete.",
  "transcript": "<transcript text>",
  "stagingStatus": "Complete",
  "stagingRecordId": "<SharePoint item id>"
}
```

On failure:

```json
{
  "success": false,
  "sessionId": "<GUID>",
  "status": "Transcription failed.",
  "transcript": "",
  "stagingStatus": "Failed"
}
```

The Windows helper treats `stagingStatus` and `stagingRecordId` as optional diagnostic fields.

## Retention

The staging list is temporary integration storage, not a permanent transcript archive unless that is approved later. Recommended retention is 7 to 30 days. Do not implement destructive cleanup until the retention policy is confirmed.
