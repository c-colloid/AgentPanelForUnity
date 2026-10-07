# Signing in to Codex

[User guide](../../USER-GUIDE.en.md) > [Agents: installing and signing in](README.en.md) > Codex

Uses OpenAI's Codex through the ACP adapter `codex-acp`. Signing in with a ChatGPT account is recommended.

**What you need**: a ChatGPT account (some tools, such as image generation, require a Plus plan or higher) and Node.js (the CLI is installed with npm). To use an API key instead, set the environment variable `CODEX_API_KEY` or `OPENAI_API_KEY`.

## 1. Switch to Codex

In Settings (⚙) > Connection > Agent, choose **Codex (codex-acp)** under **"Agent"**. If "Some changes will apply the next time you reconnect." appears at the top, press **"Reconnect now"**.

## 2. Install the CLI

If `codex-acp` is not found, the chat area and the Agent card show "Codex (codex-acp) not found".

![The chat card "Codex (codex-acp) not found", with an install button and a command field](../../images/guide/agents/codex-01-chat-missing.png)

1. Press **"Install Codex (codex-acp)"**.
2. A confirmation dialog shows the command that will run. Press **"Install"**.

   ![Confirmation dialog showing npm install -g @openai/codex @agentclientprotocol/codex-acp](../../images/guide/agents/codex-03-install-confirm.png)

   If Node.js is missing, a button that opens the Node.js download page ("Get Node.js") appears instead. After installing it, restart the editor.
3. While installing, the card shows the elapsed seconds.

   ![Settings > Connection > Agent. "Installing Codex (codex-acp)... 4s"](../../images/guide/agents/codex-04-installing.png)

If you installed it yourself, run the same command in a terminal, then press "Re-detect" (in the chat area) or "Reconnect" (in Settings).

## 3. Sign in

When the installation finishes, the panel connects to Codex. If you are not signed in yet, Codex starts its own browser flow (your browser opens and the card shows "Waiting for the Codex (codex-acp) sign-in in your browser..."). Log in to ChatGPT in the browser and the panel continues automatically.

If the browser did not open or you abandoned the flow, the card changes to "Not signed in". In that case, sign in as follows.

![The "Not signed in" card, with a "Sign in" button and a note that `codex login` will run](../../images/guide/agents/codex-05-signed-out.png)

1. Press **"Sign in"**. The panel runs `codex login` inside the panel.
2. The card shows the sign-in URL and the CLI's output. Press **"Open browser"**, or paste the URL you **"Copy"**-ed into your browser.

   ![The signing-in card, with "Cancel", "Open browser", "Copy" and an auth.openai.com sign-in URL](../../images/guide/agents/codex-06-login-url.png)

3. Log in to your ChatGPT account in the browser and allow Codex access.
4. When `codex login` finishes, the panel reconnects and the card shows "Connected to Codex (codex-acp)." The sign-in method that was used is shown below it.

> The sign-in callback is received on this computer (`localhost`). If the browser is on a different machine, such as when you use a remote desktop, run `codex login --device-auth` in a terminal and then press "Reconnect" (the CLI output gives the same hint).

## Using an API key

To use an API key instead of a ChatGPT account, set the OS environment variable `CODEX_API_KEY` (or `OPENAI_API_KEY`) and restart the editor (and Unity Hub too, if you launch the editor from it). API key usage is pay-as-you-go. The panel does not store the key.

## When it does not work

| Symptom | What to do |
|---|---|
| The conversation shows "... sign-in failed: ChatGPT: ... spawn xdg-open ENOENT" | The environment has no command to open a browser (Linux). Paste the URL shown by "Sign in" into a browser by hand, or use `codex login --device-auth` |
| When you ask for image generation, you are told it is "not available in this environment" | The ChatGPT Free plan does not provide the image generation tool. It becomes available on Plus or higher (if it does not take effect, sign in again with "Switch account") ([Usage examples by agent](../../AGENT-SHOWCASE.en.md)) |
| "... is not signed in, so the connection was not retried." | Sign in, then press "Reconnect" |

---

[← Signing in to Claude Code](claude-code.en.md) · [User guide contents](../../USER-GUIDE.en.md) · [Signing in to Grok Build →](grok-build.en.md)
