# FRIDAY agent interaction

The desktop combines on-device Windows speech recognition with a Groq-powered agent that can call a small, fixed set of desktop tools. Navigation is no longer interpreted by local phrase rules: every request goes to the model, and the model returns a tool call that FRIDAY validates and executes. Interruption and confirmation stay local. Restart any old running FRIDAY instance to load the new build.

## Using it

- Say **open Notepad**, **minimize Notepad**, **maximize Notepad**, **close Notepad**, **open Settings**, **next tab**, **go back**, or **go forward** directly. The on-device grammar captures the phrase, the agent selects a desktop tool, and the action runs locally. These requests use the Groq API.
- Say **Hey Friday** for the saved-address greeting, then ask a question. Or say **Hey Friday, what is RAM?** in one utterance. The Windows recognizer produces the text locally; the agent handles both questions and desktop actions.
- After an answer, follow-up questions are accepted for 15 seconds without another wake phrase. Idle, unaddressed conversation is ignored. Hi/hello alone are not wake phrases; include Friday.
- Say **Friday, stop**, **Friday, wait**, or **Hold on, Friday** to cancel a turn. Bare stop/wait/cancel only interrupts while FRIDAY is busy. Saying **Friday** while busy cancels the previous turn and opens listening again; **Friday, explain it simply** replaces it with the new request.
- Tap the island to interrupt when busy, or wake conversation when idle. The separate pause button cancels current work and pauses native capture until resumed.
- Risky tool calls ask for confirmation. The app shows the pending command; say **Friday, confirm** within 10 seconds, or **Friday, cancel**. Shell/terminal launches and low-confidence voice requests always require confirmation. The spoken prompt deliberately does not read the confirmation phrase aloud.
- Ctrl+Shift+P opens the text panel. Typed requests use the same agent and desktop tools, without a wake requirement. Code stays in the panel and is removed from spoken output.

## Request flow

1. `LocalNavigationListener` converts speech to text on-device using one `SpeechRecognitionEngine`.
2. `InputRouter` classifies the text as wake, interruption, confirmation, question/command, or ignored. This decision has no network dependency.
3. `FridayAssistant.RunAgentAsync` builds the system prompt, history, saved preferences and the four tool definitions, then calls the configured provider.
4. If the model returns tool calls, `MainWindow.ExecuteToolAsync` validates each one and executes it through `DesktopTools` and `WindowsNavigationExecutor`. Results are appended as `tool` messages.
5. The loop repeats (up to four rounds) until the model returns a final text answer, which is spoken and shown in the panel.

## Boundaries

`LocalNavigationListener` owns one native `SpeechRecognitionEngine` using the Windows default input device. It loads configurable command grammar, deliberate addressing with inline dictation, and English conversation dictation. The command grammar is now a **capture aid only** — its recognized text goes to the agent instead of being executed locally. Conversation dictation is enabled only during the awake follow-up window. The command grammar is disabled while busy; addressing remains enabled for interruption. `LiveMicrophoneService`, `WakeWordListener`, and `GroqSpeechToText` remain available as older source components but are not instantiated by this desktop path.

`InputRouter` no longer performs local navigation parsing. It distinguishes wake, interruption, confirmation, question/command, and ignored input. `MainWindow` coordinates routing, preferences, UI, speech, tool execution and the conversation window. `FridayAssistant` runs the agent loop and owns conversation history and preference commands.

`TurnCoordinator` invalidates canceled/replaced turns. Every awaited answer checks current ownership before it can update output or start speaking. Cancellation also clears pending confirmation/selection and invalidates queued voice deliveries. The assistant releases its wait even if a provider fails to honor cancellation; a late response cannot enter its conversation history or replace the next answer. Agent turns have a 60-second deadline. Cancelling an HTTP request cannot refund provider usage already incurred.

`WindowsVoiceService.Stop` stops playback and releases render/playback completion waits so a canceled turn does not occupy the next speech slot. Native microphone recognition remains eligible for deliberate addressing during API waits and speech. Recent spoken text is used to reduce matching self-echo. This is not acoustic echo cancellation: headphones or a quiet input path may still be needed for reliable interruption through laptop speakers.

## Tool execution and safeguards

- Four tools are exposed: `open_app`, `list_installed_apps`, `control_window` and `browser_navigation`. The model cannot run arbitrary commands, read files, type text, or address applications outside this contract.
- Tool arguments are converted to a structured navigation request and parsed against `config/navigation.json` before execution. Malformed arguments, unsupported actions and unsafe characters are rejected.
- Installed application names/IDs come from the local Windows catalog, with editable aliases. A model-supplied app name must resolve to a catalog entry.
- Stored recipes are checked against the currently resolved catalog matches before launch. A wrong cached mapping cannot bypass name resolution.
- Unqualified window commands bind the foreground HWND and process at speech detection. Focus changes or process mismatches reject execution. Typed panel actions may use the remembered external window.
- Named window matching uses executable identity/product metadata, not arbitrary browser-page/document title words. Ambiguous matches ask for clarification instead of guessing.
- Close requests use `WM_CLOSE`, allowing unsaved-work dialogs; processes are never force-killed. A stop cannot undo an action Windows already accepted or close an application that was already opened.
- The installed-app catalog is a session snapshot reused by commands. Restart FRIDAY after installing new applications or editing grammar settings.
- Next page is still ambiguous and asks for clarification; arbitrary web-page buttons and multi-step web automation are not implemented.
- Shell/terminal launches (`cmd`, PowerShell, Terminal, Registry Editor) and uncertain voice requests are held for explicit confirmation. The pending action is executed on **Friday, confirm** without a second model call.
- The agent turn extends the captured foreground context for up to 30 seconds so model latency does not expire a valid command. Cancellation still stops the turn immediately.

No recognizer can guarantee that every spoken app name is transcribed correctly. Strict confidence checks, confirmation, shell protection and target validation reduce wrong actions; live acoustic testing remains necessary.

## API usage

Every navigation request is a model round trip: typically one tool call plus one short final reply, and sometimes a second round for clarification. This consumes the same Groq quota as chat. There is no offline navigation in V1. A missing key, exhausted quota, or network failure disables desktop control; the panel shows the error. Use a tool-capable model such as `openai/gpt-oss-20b`.

## Configuration and storage

`config/navigation.json` owns command phrases (used by the capture grammar), app aliases, Windows Settings destinations, confirmation wording and browser shortcuts. `config/interaction.json` owns wake/interruption phrases, confirmation policy, confidence thresholds and follow-up timing. Restart after edits; these files are copied into the build output.

The `.env` loader supplies the Groq key/model and data directory without logging secrets. Preferences and navigation recipes remain in local SQLite files; no recordings are persisted. Technical `voice-diagnostics.log` messages contain confidence, grammar, timing and input levels, not raw transcripts or keys.

## Validation

The desktop is built with .NET 10 and verified against the live Groq API for the tool-call round trip (`open_app`, `control_window`, `browser_navigation`). The agent loop, tool schema serialization, tool-result messages and final spoken reply are exercised. Live microphone accuracy and device-specific echo/latency still require real use.
