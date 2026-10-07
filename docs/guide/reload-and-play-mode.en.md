# Script recompiles and Play mode

[User guide](../USER-GUIDE.en.md) > Script recompiles and Play mode

- Changing a script triggers a domain reload. Just before it, the panel ends the CLI process and saves the session ID, then reconnects to the same session automatically afterwards (banner "Reconnecting to the previous session..." followed by "Session resumed."). Restarting the editor restores the previous session the same way.
- If a reload happens in the middle of a turn, the banner "The last turn was interrupted by a script reload." appears. Press "Continue" and the agent carries on.

  ![Interruption banner and the "Continue" button](../images/guide/14-interrupted.png)
- If entering or leaving Play mode, or regaining focus after a script save, interrupts turns often, turn on "Continue automatically after an interruption" under Settings > Unity integration > Unity operations (UapOps). The panel then sends "Continue" for you (it is shown in the conversation log). There is no cap on the number of times. It stops only when the CLI process has exited 4 times in a row and reconnection is suspended (see the "Reconnect" banner below), or when you press the stop button.
- To have the agent wait for compilation to finish after it writes C# and then keep working, use "Continue automatically after a compile" in the same place. If the continuation turn commits more scripts, it continues again after that reload too, so the write, compile, fix-errors loop can run unattended. There is no cap, and it stops under the same conditions as "Continue automatically after an interruption" (the agent stops committing, reconnection is suspended, or you press the stop button).
- If Unity tools time out during Play mode, review "Enter Play Mode Settings" under Project Settings > Editor (Settings has an "Open Project Settings > Editor" button).
- If the CLI process dies, the banner "The agent process exited." and the reconnect button in the header appear. The panel reconnects automatically up to 3 times; after 4 consecutive exits it stops reconnecting and waits for "Reconnect" ([Troubleshooting](troubleshooting.en.md)).

---

[← Scene view markers, pins and sketches](scene-tools.en.md) · [User guide contents](../USER-GUIDE.en.md) · [Settings reference →](settings.en.md)
