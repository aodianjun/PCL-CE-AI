# PCL CE-AI

> A PCL CE-based Minecraft launcher with a pure AI home page.
> The home page keeps only the AI chat, account, version and launch controls; everything else lives in Settings.

PCL CE-AI lets the AI assistant drive the launcher: ask it to "launch the game", "install 1.21.1 with Fabric" or "disable the incompatible mods", and it calls built-in tools to do the work. When the game crashes, the crash report is submitted to the AI automatically.

## Features

- **AI chat home page**: streaming replies, tool calling, cancel and clear.
- **Multiple conversations**: start new chats, switch from the conversation list, persisted locally.
- **Context compression**: compress manually, or automatically summarize when the context grows too long.
- **Context usage**: the chat header shows estimated tokens / limit / percentage; click for details.
- **Model list and auto parameters**: fetch `/models` in AI setup; selecting a model fills the context limit and max output automatically.
- **Background execution**: tool calls, log reads and streaming run on background threads; each tool times out after 60s, and the model after 60s without a response or 90s of stream silence.
- **Visible process**: three rotating grass blocks appear while busy; reasoning content and every tool call (name, arguments, result) are shown as bubbles.
- **Auto-collapse**: reasoning and tool execution collapse into a one-line header when finished; click to expand.
- **No forced navigation**: the AI only reports where a page is by default; it switches pages only when you explicitly ask to open one.
- **Only four controls at the bottom**: Settings (bottom-left), Account, Version, Launch.
- **AI can**:
  - launch the game and switch versions
  - open the install page with a version preselected
  - list / enable / disable / delete mods
  - read the latest game log and crash report
  - open any launcher page
- **Automatic crash analysis**: crash reports are submitted to the AI and the result appears on the home page.
- **DeepSeek harness**: works with any OpenAI-compatible `/chat/completions` endpoint; defaults to `deepseek-chat`. The API key is stored encrypted locally.
- **First-run onboarding**: opens Settings → AI setup automatically when no key is configured.
- **Settings consolidation**: downloads, mods, modpacks, resource packs, shaders, worlds, datapacks, instance settings, logs, tasks and tools all live under Settings.
- **Microsoft sign-in**: the Azure client ID is embedded at build time, so every device running the build can sign in directly.
- **GitHub Releases updates**: no longer uses the official update source, so the build cannot be replaced by upstream releases.

## Conversations and context

- Buttons at the top-right of the chat: new chat, conversation list, compress context, clear chat.
- Conversations are stored in `PCL/AiChat/conversations.json` and restored on restart.
- When the context exceeds the threshold (16000 characters by default) it is compressed automatically; you can also compress manually at any time.

## AI setup

1. Open the launcher → gear at the bottom-left → **AI setup**.
2. Fill in:
   - API base URL: `https://api.deepseek.com` (or any compatible relay)
   - Model: `deepseek-chat`
   - API key: created in the DeepSeek console
3. Click "Save and test", then return to the home page and chat.

The key is stored in the local encrypted config and is only sent to the API endpoint you configure.

## Build

> The repository uses `LangVersion 15.0` and `net10.0`; a **.NET 11 preview SDK** is required to compile, and the .NET 10 runtime is required to run.

```powershell
dotnet build PCL.Launcher/PCL.Launcher.csproj -c Debug
```

### Build with Microsoft sign-in

You need your own Azure app registration (public client, device code flow, `XboxLive.signin offline_access`). Embed the client ID at build time:

```powershell
./scripts/build-with-ms-login.ps1 -ClientId "<your Azure client ID>"
```

The script disables shared compilation, forces a rebuild and verifies that the client ID was written into `PCL.Core.dll`.

> Third-party apps must pass Mojang's [AppID review](https://aka.ms/mce-reviewappid) before calling the Minecraft API. Until then, Microsoft sign-in fails with 403 at the final step. Offline or third-party login still works.

## Updates

Settings → Update checks GitHub Releases of this repository. Publish a release tagged like `v2.15.2` and the launcher will detect it and offer the download link.

## Feedback

- Issues: <https://github.com/aodianjun/PCL-CE-AI/issues>
- Settings → Feedback reads issues from this repository and groups them by label; unlabelled issues fall back to "Waiting".

## Credits and license

- Upstream: [PCL-Community/PCL-CE](https://github.com/PCL-Community/PCL-CE), [PCL2](https://github.com/Meloong-Git/PCL)
- Original author: LTCat and the PCL Community contributors
- Maintainer of this build: aodianjun
- License: [Apache License 2.0](LICENSE)

This is a community personal build, not affiliated with Mojang Studios or Microsoft. Minecraft is a trademark of Mojang Studios.
