# FRIDAY — Version 1

> A JARVIS-inspired personal AI companion for Windows — on-device voice recognition, Groq-powered agent control, and a hands-free desktop experience.

![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![UI](https://img.shields.io/badge/UI-WPF-68217A)
![Version](https://img.shields.io/badge/version-1.0-00C8FF)
![Status](https://img.shields.io/badge/status-active-brightgreen)

FRIDAY is a desktop assistant that lives in a small top-center "island" on your screen. It listens for **"Hey Friday"**, understands English, Tamil and Tanglish, answers questions through the Groq API, and controls Windows applications through a small, fixed set of tools. The model chooses an action; FRIDAY validates and performs it locally.

This repository contains the **Version 1** source. Version 2 development continues on top of it.

---

## Table of contents

- [Highlights](#highlights)
- [How it works](#how-it-works)
- [Architecture](#architecture)
- [Repository layout](#repository-layout)
- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Using FRIDAY](#using-friday)
- [Voice commands](#voice-commands)
- [Preferences](#preferences)
- [Configuration reference](#configuration-reference)
- [Safety and privacy](#safety-and-privacy)
- [Version 1 limitations](#version-1-limitations)
- [Troubleshooting](#troubleshooting)
- [Roadmap](#roadmap)
- [Credits](#credits)

---

## Highlights

- **Wake-word conversation** — say *"Hey Friday"* (or *"Hey Friday, what is RAM?"* in one breath) and ask anything. Follow-up questions are accepted for 15 seconds without repeating the wake phrase.
- **Agent-driven Windows control** — open, close, minimize, maximize and restore applications; switch tabs; navigate back/forward. The Groq model selects one of four scoped tools, and FRIDAY's local executor validates and performs the action. Navigation uses API credits just like chat.
- **One agent for questions and actions** — fast, low-latency Groq replies with English, Tamil and Tanglish understanding.
- **Natural desktop presence** — an always-on-top island with a live waveform, an output panel, animated captions, and spoken replies.
- **Interruptible speech** — say **"stop"**, **"Friday, stop"**, **"wait"** or **"cancel"** while FRIDAY is speaking to cut it off immediately. `Esc` and the island itself are also instant interrupts.
- **Confirmation before risky actions** — launching terminals/shells or low-confidence commands requires an explicit *"Friday, confirm"*.
- **Persistent preferences** — preferred name, default language (English / Tamil / Tanglish), answer length and speech rate are stored in SQLite.
- **Session-aware** — the latest 20 messages of conversation are kept as context; `/clear` resets it in the console host.
- **Privacy first** — no recordings are saved, diagnostics contain recognition scores and timings (not transcripts or keys), `.env` is git-ignored, and desktop actions execute locally.
- **Console host included** — `Friday.Host` is a lightweight terminal chat client for testing `Friday.Core` without the WPF shell. It is chat-only and exposes no desktop tools.

---

## How it works

1. You say **"Hey Friday"** (or open the text panel with `Ctrl+Shift+P`).
2. The local Windows speech recognizer (`System.Speech`, en-GB preferred) produces text on-device.
3. `InputRouter` classifies the input as a **wake word**, **interrupt**, **confirmation**, **question/command**, or **ignored noise** — this decision requires no network.
4. The agent sends the request to Groq together with four desktop tools and your saved preferences.
5. If the model returns a tool call, FRIDAY validates it (target, foreground window, confirmation policy) and executes it with `WindowsNavigationExecutor`, then sends the result back to the model.
6. The model produces the final reply, which is spoken through `WindowsVoiceService` while captions track the spoken words.

The model cannot run arbitrary commands, read files, or execute anything on its own. Only the four declared tools are available, and every action is validated by local code before it reaches Windows.

---

## Architecture

```text
┌──────────────────────────── Windows desktop ────────────────────────────┐
│  Friday.Desktop (WPF, net10.0-windows)                                  │
│                                                                         │
│  Island · Output panel · Captions · Waveform                            │
│                     │                                                   │
│  MainWindow + TurnCoordinator + InputRouter                             │
│   • wake / interrupt / confirmation routing                             │
│   • turn cancellation · conversation window                             │
│         │                                    │                          │
│  LocalNavigationListener              WindowsVoiceService               │
│  (System.Speech, on-device)           (System.Speech → NAudio)          │
│         │                                    │                          │
│  DesktopTools → WindowsNavigationExecutor                               │
│  (open/list apps · window control · browser navigation)                 │
└─────────────────────────────────┬───────────────────────────────────────┘
                                  │ agent request + tool results
┌─────────────────────────────────▼───────────────────────────────────────┐
│  Friday.Core (net10.0)                                                  │
│  FridayAssistant agent loop · preferences (SQLite) · recipes (SQLite)   │
└─────────────────────────────────┬───────────────────────────────────────┘
                                  │ HTTPS (chat completions + tools)
                                  ▼
                        api.groq.com/openai/v1
```

### Projects

| Project | Target | Purpose |
| --- | --- | --- |
| `backend/contracts/Friday.Contracts` | `net10.0` | Shared chat, tool-call and navigation contracts and interfaces |
| `backend/core/Friday.Core` | `net10.0` | Assistant, agent/tool loop, Groq provider, navigation parser, preferences, SQLite storage |
| `backend/host/Friday.Host` | `net10.0` | Console chat host for the core (chat-only) |
| `frontend/desktop/Friday.Desktop` | `net10.0-windows` (WPF) | Island, panels, captions, recognizer, desktop tools, speech output |

---

## Repository layout

```text
FRIDAY/
├─ backend/
│  ├─ contracts/Friday.Contracts/        # Shared DTOs + interfaces
│  ├─ core/Friday.Core/                  # Assistant, providers, navigation, persistence
│  ├─ host/Friday.Host/                  # Console chat host
│  └─ engines/voice/                     # Python 3.10 venv for IndicF5 experiments
├─ frontend/
│  └─ desktop/Friday.Desktop/            # WPF desktop app
├─ config/
│  ├─ interaction.json                   # Wake, interrupt, confirmation, timing rules
│  └─ navigation.json                    # Local command phrases, aliases, Settings targets
├─ data/
│  └─ database/                          # SQLite + diagnostics (git-ignored)
├─ docs/
│  └─ interaction-architecture.md        # Detailed agent and routing design notes
├─ models/
│  └─ text-to-speech/indicf5/            # Experimental IndicF5 model assets
├─ .env.example                          # Environment variable template
├─ .gitignore
└─ README.md
```

---

## Requirements

- **Windows 10 or Windows 11** (x64)
- **.NET 10 SDK** — [download](https://dotnet.microsoft.com/download)
- A **Windows speech recognizer** with an English language pack. `en-GB` is preferred and falls back to any installed English recognizer.
- An installed **English TTS voice** (Windows ships with one).
- A **Groq API key** — free tier available at [console.groq.com/keys](https://console.groq.com/keys)
- Internet access for chat and desktop control (the Groq model interprets every request).
- *Optional:* Python 3.10 for the experimental IndicF5 TTS assets in `models/`.

> The desktop app targets `net10.0-windows`. Older SDKs cannot build it.

---

## Getting started

### 1. Clone

```powershell
git clone https://github.com/riyasharavind-dev/Friday-V2.git
cd Friday-V2
```

### 2. Configure secrets

Copy the template and fill in your key:

```powershell
Copy-Item .env.example .env
```

```env
FRIDAY_GROQ_API_KEY=gsk_your_key_here
FRIDAY_GROQ_MODEL=openai/gpt-oss-20b
```

`FRIDAY_GROQ_MODEL` must be a model available to your Groq account (for example `openai/gpt-oss-20b` or `llama-3.3-70b-versatile`).

### 3. Build

```powershell
dotnet build frontend/desktop/Friday.Desktop/Friday.Desktop.csproj -c Debug
```

### 4. Run

```powershell
dotnet run --project frontend/desktop/Friday.Desktop/Friday.Desktop.csproj
```

Or launch the built executable directly:

```text
frontend\desktop\Friday.Desktop\bin\Debug\net10.0-windows\Friday.Desktop.exe
```

The island appears at the top-center of your primary display. The first launch may take a few seconds while the recognizer loads.

### Console host (optional)

```powershell
dotnet run --project backend/host/Friday.Host/Friday.Host.csproj
```

Type questions in the terminal; `/clear` resets the conversation, `/exit` closes it.

---

## Using FRIDAY

### Wake and conversation

- Say **"Hey Friday"** — FRIDAY greets you in your saved language.
- Say **"Hey Friday, what is RAM?"** — one-shot question.
- After an answer, you have **15 seconds** to ask a follow-up without the wake phrase.
- Idle, unaddressed conversation is ignored.

### Desktop navigation

Say commands like:

- *"Open Notepad"*
- *"Open Settings"*
- *"Minimize Notepad"*
- *"Maximize this window"*
- *"Close Notepad"*
- *"Next tab"* / *"Previous tab"*
- *"Go back"* / *"Go forward"*

The on-device recognizer captures the phrase, the Groq agent selects a desktop tool, FRIDAY validates the action locally, and `WindowsNavigationExecutor` performs it. Tamil/Tanglish variants work too, for example *"Notepad open pannu"* and *"back pannu"*. Application names come from the installed Start-menu catalogue; aliases live in `config/navigation.json`.

> Each navigation request is a model round trip — usually one tool call followed by a short confirmation. It consumes Groq usage just like a chat question, and desktop control does not work without an API key and internet connection.

### Interrupting FRIDAY

While FRIDAY is thinking or speaking:

| Action | Result |
| --- | --- |
| Say **"stop"**, **"stop talking"**, **"wait"**, **"cancel"**, **"be quiet"** | Stops the current answer immediately |
| Say **"Friday, stop"** / **"Friday, wait"** | Same, with the wake word |
| Press **Esc** | Stops the current turn (closes the app only when idle) |
| Click the **island** | Stops the current turn |
| Click the **pause button** | Cancels the turn and pauses microphone capture |

Bare interrupt words are accepted **only during an active turn**, so ambient conversation does not stop FRIDAY while it is idle.

### Text mode

Press **Ctrl+Shift+P** to open the output/chat panel and type instead of speaking. Typed navigation commands and typed questions both work.

### Confirmations

- Risky tool calls are confirmed by the app before they execute.
- Low-confidence voice commands are treated as uncertain and require confirmation.
- Shell/terminal launches (`cmd`, PowerShell, Terminal, Registry Editor) always require confirmation.
- Say **"Friday, confirm"** within 10 seconds, or **"Friday, cancel"**. FRIDAY shows and speaks the pending command without reading the confirmation phrase aloud.

### Reading long answers

Long replies are shown in the output panel. If a reply contains code, the code stays in the panel and is skipped by the spoken voice.

---

## Voice commands

### Navigation actions

| Action | Example phrases |
| --- | --- |
| Open | `open {app}`, `launch {app}`, `start {app}`, `{app} open pannu` |
| Close | `close {app}`, `exit {app}`, `{app} close pannu`, `close this window` |
| Minimize | `minimize {app}`, `minimise {app}`, `minimize this` |
| Maximize | `maximize {app}`, `maximise {app}`, `maximize this` |
| Restore | `restore {app}`, `restore this window` |
| Tabs | `next tab`, `previous tab` |
| Browser | `go back`, `go forward`, `back pannu`, `forward pannu` |

Settings shortcuts include Wi-Fi, Bluetooth, Sound, Display, Windows Update, Installed apps, Default apps, Privacy, Microphone, Taskbar and About PC.

### Preference commands

FRIDAY can remember these across restarts:

| Preference | Examples |
| --- | --- |
| Preferred address | *"Call me Riyash"*, *"enna Riyash nu koopidu"*, *"forget my preferred name"* |
| Default language | *"Set default language to Tamil"*, *"Inime Tanglish la pesu"* |
| Answer length | *"Keep your answers short"*, *"Give detailed answers"* |
| Speech rate | *"Speak slower"*, *"Speak faster"*, *"Speak at normal speed"*, *"Set speech rate to 2"* |
| Review | *"Show my preferences"*, *"List my preferences"* |
| Reset | *"Reset my preferences"* |

Temporary requests such as *"answer in Tamil just this once"* do not overwrite saved preferences.

---

## Configuration reference

### Environment variables (`.env`)

| Variable | Required | Description |
| --- | --- | --- |
| `FRIDAY_GROQ_API_KEY` | Yes (chat + desktop control) | Groq API key used for chat completions and tool calls |
| `FRIDAY_GROQ_MODEL` | Yes (chat + desktop control) | Groq model ID with tool-calling support, e.g. `openai/gpt-oss-20b` |
| `FRIDAY_GEMINI_API_KEY` | No | Reserved for future providers |
| `FRIDAY_OPENROUTER_API_KEY` | No | Reserved for future providers |
| `FRIDAY_LOCAL_ENABLED` | No | Reserved for future local-model support |
| `FRIDAY_LOCAL_MODEL` | No | Reserved for future local-model support |
| `FRIDAY_DATA_DIRECTORY` | No | Overrides the data root (defaults to `<repo>/data`) |
| `FRIDAY_MIC_DEVICE` | No | Input device index for the legacy live-capture service (`-1` = default) |

> The V1 desktop path uses **Groq only**, for both chat and tool calling. Provider placeholders exist so V2 can add fallback routing without changing the file layout.

### `config/interaction.json`

Controls wake phrases, interrupt requests, confirmation requirements, follow-up timing, and recognition confidence floors.

### `config/navigation.json`

Controls local command phrases, application aliases, ignored target words, Windows Settings destinations, browser process names, keyboard shortcuts, and confirmation wording.

Both files are copied into the build output. **Restart FRIDAY after editing them.**

### Local data

| Path | Contents |
| --- | --- |
| `data/database/preferences.db` | Saved user preferences |
| `data/database/local-navigation.db` | Learned launch recipes |
| `data/database/voice-diagnostics.log` | Recognition confidence, grammar and timing diagnostics |

All three are git-ignored. Diagnostics never store raw transcripts or API keys.

---

## Safety and privacy

- **Scoped tools, local execution.** The model can only choose from four fixed tools: `open_app`, `list_installed_apps`, `control_window` and `browser_navigation`. It cannot run arbitrary commands, read files, or control applications outside those tools.
- **Every action is validated locally.** Target names are resolved against the installed-app catalogue, unqualified window commands are bound to the foreground window captured at speech time, and browser shortcuts are sent only to supported browsers.
- **Explicit confirmation** for shell/terminal launches and low-confidence voice requests.
- **Graceful window closing.** Close requests use `WM_CLOSE` so applications can prompt about unsaved work; FRIDAY never force-kills processes.
- **Secrets stay local.** `.env` is ignored by git; the app never logs API keys.
- **No recordings.** Audio is streamed to the recognizer and discarded; nothing is written to disk.
- **Local storage.** Conversation history lives in memory (last 20 messages). Preferences and navigation recipes are stored in local SQLite files.

---

## Version 1 limitations

- **No acoustic echo cancellation.** FRIDAY can hear its own voice through laptop speakers. Voice interruptions are most reliable with headphones or a narrow beam/close-talk microphone.
- **Recognizer-dependent.** The local engine needs the Windows English speech pack. Strong accents or very quiet microphones reduce accuracy.
- **Installed-app snapshot.** The app catalog is built at startup; restart FRIDAY after installing new applications.
- **No installer yet.** V1 runs from a source build (`dotnet run` or the built executable).
- **Single machine, single instance.** Only one FRIDAY process may run at a time.
- **No arbitrary web automation.** "Next page" is ambiguous and asks for clarification; page-button automation is not implemented.
- **Groq required for chat and desktop control.** There is no offline mode in V1.
- **API usage for actions.** Each navigation request is a model round trip, and an ambiguous request may need a second round. Free-tier quota applies to navigation as well as chat.
- **Bounded tool set.** The model can only request the four declared actions. Arbitrary shell commands, file access, typing text, media/volume/brightness control and multi-step web automation are not available.
- **Local grammar still gates capture.** Commands outside the configured phrase/app vocabulary need the wake phrase so dictation can capture them.
- **IndicF5 TTS is experimental.** The `models/` assets and Python venv are research material and are not wired into the V1 desktop speech path (which uses Windows SAPI voices).

---

## Troubleshooting

| Problem | What to check |
| --- | --- |
| **"FRIDAY is already running"** | Close the existing instance (island → pause → Esc, or Task Manager). |
| **No voice response / no recognition** | Windows Settings → Privacy → Microphone = Allow; correct default input device; English speech pack installed. Watch `data/database/voice-diagnostics.log` for `READY` and microphone peaks. |
| **FRIDAY doesn't hear "stop" over its own voice** | Use headphones, speak during a brief pause, or press `Esc` / click the island. |
| **"Groq configuration is missing"** | `FRIDAY_GROQ_API_KEY` and `FRIDAY_GROQ_MODEL` must be set in `.env` at the repo root, then restart. |
| **Groq HTTP 401 / 429** | Invalid key, model access, quota reached, or a model without tool-calling support. Check console.groq.com and retry. |
| **Navigation doesn't run** | Desktop control needs the same key and internet connection as chat. Check the panel for an auth/quota error and use a tool-capable model such as `openai/gpt-oss-20b`. |
| **No speech output** | Install an English TTS voice: Settings → Time & Language → Speech → Add voices. |
| **App list is stale** | Restart FRIDAY after installing or uninstalling apps. |
| **Commands hit the wrong window** | Bring the intended window to the foreground and repeat the command. FRIDAY rejects actions when focus changes. |

---

## Roadmap

Version 2 ideas:

- Multi-provider AI router with automatic key rotation and failover
- Full-duplex barge-in with echo cancellation
- Expanded system control (media, volume, brightness, screenshots, typed text)
- Better app discovery and web-page navigation
- Packaging: installer, auto-start, tray integration
- Optional local model support
- Richer long-term memory

---

## Credits

Built by **Riyash Aravind** ([@riyasharavind-dev](https://github.com/riyasharavind-dev)).

Powered by:

- [.NET 10](https://dotnet.microsoft.com/) and WPF
- [Groq](https://groq.com/) for chat completions and tool calling
- [Microsoft System.Speech](https://learn.microsoft.com/dotnet/api/system.speech.recognition) for on-device recognition and synthesis
- [NAudio](https://github.com/naudio/NAudio) for audio playback and capture
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) for local storage
- [IndicF5](https://huggingface.co/ai4bharat/IndicF5) model assets (experimental)

---

<p align="center"><strong>FRIDAY V1</strong> — “Yes, I'm listening.”</p>
