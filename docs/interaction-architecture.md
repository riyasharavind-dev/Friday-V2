# FRIDAY hybrid interaction

The desktop now combines local Windows navigation with Groq chat. Changes are already applied in this project; no replacement installer is required. Restart any old running FRIDAY instance to load the new build.

## Using it

- Say **open Notepad**, **minimize Notepad**, **maximize Notepad**, **close Notepad**, **open Settings**, **next tab**, **go back**, or **go forward** directly. These route locally and do not call a model or cloud transcription service.
- Say **Hey Friday** for the saved-address greeting, then ask a question. Or say **Hey Friday, what is RAM?** in one utterance. The Windows recognizer produces the text locally; non-navigation questions go to the configured Groq chat provider.
- After an answer, follow-up questions are accepted for 15 seconds without another wake phrase. Idle, unaddressed conversation is ignored. Hi/hello alone are not wake phrases; include Friday.
- Say **Friday, stop**, **Friday, wait**, or **Hold on, Friday** to cancel a turn. Bare stop/wait/cancel is ignored to avoid accidental interruptions. Saying **Friday** while busy cancels the previous turn and opens listening again; **Friday, explain it simply** replaces it with the new request.
- Tap the island to interrupt when busy, or wake conversation when idle. The separate pause button cancels current work and pauses native capture until resumed.
- Low-confidence voice commands ask for confirmation. The panel shows what was heard; say **Friday, confirm** within 10 seconds, or **Friday, cancel**. Shell/terminal launches also require explicit voice confirmation. The spoken prompt deliberately does not read the confirmation phrase aloud.
- Ctrl+Shift+P opens the existing text panel. Typed local actions and saved preferences remain local; typed questions use chat without a wake requirement. Code stays in the panel and is removed from spoken output.

## Boundaries

`LocalNavigationListener` owns one native `SpeechRecognitionEngine` using the Windows default input device. It loads configurable local commands, deliberate addressing with inline dictation, and English conversation dictation. Conversation dictation is disabled at startup/idle and during speech or API work; it is enabled only for the awake follow-up window, excluding pending confirmations/choices. Local-command grammar is disabled while busy; addressing remains enabled for interruption. Grammar state is changed only on mode transitions, not on every idle timer tick. `LiveMicrophoneService`, `WakeWordListener`, and `GroqSpeechToText` remain available as older source components but are not instantiated by this desktop path. There is no simultaneous second capture engine and no custom live PCM stream.

`InputRouter` decides whether an input is local navigation, wake, interruption, confirmation, API question, or ignored. Its decision has no network dependency. `MainWindow` coordinates the routing, preferences, UI, speech and conversation window. `FridayAssistant` receives chat/preferences only in this desktop integration; it has no navigation executor or desktop tools. The agent-facing navigation executor interface remains separate for future work.

`TurnCoordinator` invalidates canceled/replaced turns. Every awaited answer checks current ownership before it can update output or start speaking. Cancellation also clears pending confirmation/selection and invalidates queued voice deliveries. The assistant releases its wait even if a provider fails to honor cancellation; a late response cannot enter its conversation history or replace the next answer. API waits have a 30-second deadline. Cancelling an HTTP request cannot refund provider usage already incurred.

`WindowsVoiceService.Stop` stops playback and releases render/playback completion waits so a canceled turn does not occupy the next speech slot. Native microphone recognition remains eligible for deliberate addressing during API waits and speech. Recent spoken text is used to reduce matching self-echo. This is not acoustic echo cancellation: headphones or a quiet input path may still be needed for reliable interruption through laptop speakers.

## Navigation safeguards

- Installed application names/IDs come from the local Windows catalog, with editable aliases. The speech grammar no longer treats every individual word of an app name as a target.
- Stored recipes are checked against the currently resolved catalog matches before launch. A wrong cached Notepad-to-CMD mapping cannot bypass name resolution.
- Local commands have a 2-second execution deadline. Recognition backlog is checked, canceled input epochs are discarded, and workers check cancellation/deadlines before OS actions. There is no deferred action queue.
- Unqualified voice window commands bind the foreground HWND and process at speech detection. Focus changes or process mismatches reject execution. If FRIDAY itself is foreground, a voice command does not fall back to an older external window. Typed panel actions may use the remembered external window.
- Named window matching uses executable identity/product metadata, not arbitrary browser-page/document title words. Ambiguous windows require clarification rather than guessing.
- Close requests use WM_CLOSE, allowing unsaved-work dialogs; processes are never force-killed. A stop cannot undo an action Windows already accepted or close an application that was already opened.
- The installed-app catalog is a session snapshot reused by commands; the old five-minute rediscovery path is removed. Restart FRIDAY after installing new applications or editing grammar settings.
- Next page is still ambiguous and asks for clarification; arbitrary web-page buttons and multi-step agent automation are not implemented.

No recognizer can guarantee that every spoken app name is transcribed correctly. Strict confidence checks, confirmation, shell protection and target validation reduce wrong actions; live acoustic testing remains necessary.

## Configuration and storage

`config/navigation.json` owns local command phrases, app aliases, Windows Settings destinations, confirmation wording and browser shortcuts. `config/interaction.json` owns wake/interruption phrases, confirmation policy, confidence thresholds, local expiry and follow-up timing. Restart after edits; these files are copied into the build output.

The existing `.env` loader supplies the configured Groq key/model and data directory without logging secrets. Existing preference storage and the local-navigation database remain in place; no recordings are persisted. Technical `voice-diagnostics.log` messages contain confidence, grammar, timing and input levels, not raw transcripts or keys.

The source backup for changed pre-existing C# files/project file is outside the project at `FRIDAY-fixes/hybrid-integration-backup-20261023-201043`. Verification sources and results are at `FRIDAY-fixes/hybrid-verification`. Do not apply earlier standalone-navigation/voice-fix installers over this integration.

## Validation limits

The complete desktop is compiled in a matching isolated source tree because an older running FRIDAY instance locks normal output DLLs. Regression checks use fake providers/executors and temporary preference databases. They cover hybrid routing, explicit interruption, cancellation of a noncooperative provider, continued second questions, stale response suppression, poisoned cache rejection, focus/expiry policies, confirmation rules, compiled local/address grammars and preference persistence.

No paid/live API request, real navigation action, or microphone capture is performed by these regression checks. The restricted test process cannot see the user's en-GB recognizer. A separate native probe was subsequently run in the normal Windows user session using Microsoft Hazel-generated audio and the installed en-GB recognizer, without microphone input or app actions. Eight production-grammar/routing scenarios pass; see `FRIDAY-fixes/recognition-investigation/NATIVE-VALIDATION.txt`.

The native comparison exposed why startup looked healthy but voice did nothing: always-enabled conversation dictation reduced synthetic phrase scores below the application floors (Notepad 0.33 and Hey Friday 0.51). Disabling that competing grammar at idle produced 0.57 and 0.62. The wake floor is now 0.40; the local candidate floor is 0.30. The much stricter automatic-execution/word-confidence checks and shell confirmation policy remain, so a weaker local candidate can request confirmation but cannot directly execute. Rejected/below-floor input now logs its score without recording the transcript. These measurements are calibration evidence, not a guarantee of live microphone accuracy. Speaker echo and device-specific latency still need live use.
