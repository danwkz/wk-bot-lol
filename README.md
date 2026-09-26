# WK LoL

A minimal bot built on wk's core and interface, driven entirely through an Arduino that Windows
sees as an ordinary keyboard and mouse. One module so far: the **Orb Walker**.

## The Orb Walker

While the master key is held (a global hotkey, swallowed so the game never sees it):

1. the **key to hold** goes down, and stays down;
2. the **key to press** is tapped;
3. the **delay after the press** is waited, in ms;
4. the mouse is **clicked at the cursor**;
5. the **delay after the click** is waited, in ms;

steps 2–5 repeat until the master key comes up, and then the held key is released — the press and
the click taking turns, each with its own wait. Letting go during either wait ends the run there,
without whatever would have followed. The click never moves the cursor.

With **Toggle** on, the master key does not have to be held: one press starts the cycle and the
next press stops it. Changing the mode ends a run in progress. The switch refuses to turn
on until every key is set and distinct, the delay is in range, Setup is finished and the board
answers with the current firmware. With **Trace** on, the Log shows every step with its timing.

## Setup

A four-step flow the app opens on until it has been walked once: find the board (an ATmega32U4 —
Pro Micro, Leonardo or Micro), put the `wk_hid` firmware on it (the first flash downloads a
portable Arduino toolchain into `wklol/arduino/.toolchain`, and asks first), check it end to end,
done. The firmware is wk's plus a relative pointer for clicking in place (protocol v2): wk still
works with it, but a board flashed by wk has to be flashed once more — see
`wklol/arduino/README.md`.

## Building

.NET Framework 4.8.1, VB.NET, WinForms hosting a WebView2 page. Open `wk-bot-lol.sln` in Visual
Studio, or:

```bash
MSBuild wklol/wklol.vbproj -restore -p:Configuration=Debug
```

The interface in `wklol/web` is embedded into `wklol.exe`. Settings are kept in `settings.json`
and session logs in `logs\`, both beside the executable.

## Layout

| Folder | What it holds |
|---|---|
| `Core` | Module contract (`IBotModule`), engine, module host, logger |
| `Helpers` | Arduino link, flasher and key map, input manager, global key hook, settings, screen state |
| `Modules` | `OrbWalkerModule` |
| `Host` | `BotHost` — the module's switch and the master key's hook |
| `UI` | The window, the WebView2 shell and the page bridge |
| `web` | The interface |
| `arduino` | Firmware and upload script |
