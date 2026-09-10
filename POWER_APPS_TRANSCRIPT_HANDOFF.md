# Power Apps Transcript Handoff

Phase 6C retrieves the computer-audio transcript from cloud staging and passes it into the existing AI Scribe analysis button. Power Apps remains responsible for setting `varAIScribeTranscript`, running `btnAIScribeAnalyzeDiscussion`, creating draft cards, and all SharePoint business logic.

Do not use browser scripting, JavaScript injection, DOM manipulation, window messaging, PCF, or direct Windows-to-Power Apps variable writes.

## Start Computer Audio

Use one GUID for the whole recording.

```powerfx
Set(
    varAIScribeSessionID,
    Text(GUID())
);

Set(varAIScribeTranscript, Blank());
Set(varAIScribeError, Blank());
Set(varSpeechResult, Blank());
Set(varAIScribeRecordingReady, false);
Set(varAIScribeWaitingForTranscript, false);
Set(varAIScribeTranscriptRecord, Blank());
Set(varAIScribeTranscriptStartTime, Blank());

Set(
    varAIScribeStatus,
    "Starting computer audio recording..."
);

Launch(
    "huddlescribe://start/" & varAIScribeSessionID
);
```

Do not create another GUID after this point for the same recording.

## Stop Computer Audio

```powerfx
Set(
    varAIScribeStatus,
    "Stopping computer audio recording..."
);

Set(
    varAIScribeWaitingForTranscript,
    true
);

Set(
    varAIScribeTranscriptStartTime,
    Now()
);

Launch(
    "huddlescribe://stop/" & varAIScribeSessionID
);
```

After Stop, the Windows helper finalizes the WAV and submits it to `TranscribeHuddleAudio`. The Flow transcribes, creates or updates the staging record, and the Timer retrieves the result by `varAIScribeSessionID`.

## Timer Control

Suggested control name:

```text
tmrAIScribeTranscriptCheck
```

Suggested properties:

```text
Duration: 2500
Repeat: true
Start: varAIScribeWaitingForTranscript
```

Suggested `OnTimerEnd`:

```powerfx
Refresh(Huddle_AIScribeTranscript);

Set(
    varAIScribeTranscriptRecord,
    LookUp(
        Huddle_AIScribeTranscript,
        AIScribe_SessionID = varAIScribeSessionID
    )
);

If(
    !IsBlank(varAIScribeTranscriptStartTime) &&
    DateDiff(
        varAIScribeTranscriptStartTime,
        Now(),
        TimeUnit.Seconds
    ) > 120,

    Set(
        varAIScribeWaitingForTranscript,
        false
    );

    Set(
        varAIScribeStatus,
        "Transcription is taking longer than expected."
    );

    Set(
        varAIScribeError,
        "The recording was submitted, but the transcript was not returned within 120 seconds."
    ),

    varAIScribeTranscriptRecord.AIScribe_Status = "Complete",

    Set(
        varAIScribeWaitingForTranscript,
        false
    );

    Set(
        varAIScribeTranscript,
        Trim(
            Coalesce(
                varAIScribeTranscriptRecord.AIScribe_Transcript,
                ""
            )
        )
    );

    If(
        !IsBlank(varAIScribeTranscript),

        Set(
            varAIScribeStatus,
            "Transcription complete. Analyzing discussion..."
        );

        If(
            varAIScribeAnalyzedSessionID <> varAIScribeSessionID,
            Set(
                varAIScribeAnalyzedSessionID,
                varAIScribeSessionID
            );
            Select(
                btnAIScribeAnalyzeDiscussion
            )
        ),

        Set(
            varAIScribeStatus,
            "No speech was recognized."
        );

        Set(
            varAIScribeError,
            "The transcription completed but returned no transcript."
        )
    ),

    varAIScribeTranscriptRecord.AIScribe_Status = "Failed",

    Set(
        varAIScribeWaitingForTranscript,
        false
    );

    Set(
        varAIScribeStatus,
        "Transcription failed."
    );

    Set(
        varAIScribeError,
        Coalesce(
            varAIScribeTranscriptRecord.AIScribe_Error,
            "Computer audio transcription failed."
        )
    )
);
```

The Timer stops on success, failure, or timeout. Do not automatically resubmit the recording when Power Apps times out.

## Retrieval Contract

Power Apps should always look up the staging record with the exact GUID it generated:

```powerfx
LookUp(
    Huddle_AIScribeTranscript,
    AIScribe_SessionID = varAIScribeSessionID
)
```

After successful retrieval, optionally mark the staging record:

```text
AIScribe_Processed = true
AIScribe_Status = Retrieved
AIScribe_DateRetrieved = Now()
```

## Regression Boundary

The existing Power Apps microphone path should continue to work exactly as before:

```text
Power Apps Microphone
-> Existing TranscribeHuddleAudio
-> Azure Speech
-> varAIScribeTranscript
-> btnAIScribeAnalyzeDiscussion
```

Phase 6C must not redesign the microphone path and must not duplicate Analyze Discussion logic in the Windows helper.
