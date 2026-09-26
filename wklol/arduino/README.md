# wk_hid — the firmware that presses every key and click

The bot drives an Arduino that Windows has enumerated as an ordinary keyboard and mouse, so
nothing it presses carries the `LLMHF_INJECTED` flag `SendInput` stamps on every event it
produces. It is wk's Hardware-mode firmware plus one thing: a second, **relative** pointer that
clicks where the cursor already is, without moving it (protocol v2). wk works with it unchanged;
a board flashed by wk (v1) has to be flashed once more before this bot can click.

- `wk_hid/wk_hid.ino` — the firmware. Composite HID (keyboard + absolute mouse) behind a
  line-based serial protocol.
- `flash.ps1` — fetches a portable `arduino-cli` into `.toolchain/` (git-ignored), compiles and
  uploads. Installs nothing onto the machine.

Normally there is no need to run this by hand: the bot's **Setup** runs this same script from its
Firmware step, picking the board definition from the port's USB ids and streaming the progress to
the log. By hand:

```bash
powershell -ExecutionPolicy Bypass -File flash.ps1                       # auto-detect
powershell -ExecutionPolicy Bypass -File flash.ps1 -Fqbn arduino:avr:leonardo -Port COM7
powershell -ExecutionPolicy Bypass -File flash.ps1 -CompileOnly          # no board needed
```

Needs an **ATmega32U4** board (Pro Micro / Leonardo / Micro) and a **data** USB cable. Many Pro
Micro clones carry the Leonardo bootloader and want `-Fqbn arduino:avr:leonardo`.

## Protocol

Line-based ASCII, one command per line, `\n` terminated, exactly one reply per command.

| Command | Meaning | Reply |
|---|---|---|
| `P` | identify | `WKHID 2` |
| `KD <usage>` / `KU <usage>` | key down / up, by HID usage id | `OK` |
| `KR` | release every key | `OK` |
| `MM <x> <y>` | absolute move, 0…32767 each | `OK` |
| `MD <btn>` / `MU <btn>` | button down / up on the absolute pointer (1=left, 2=right, 3=middle) — lands where the board last put the cursor | `OK` |
| `CD <btn>` / `CU <btn>` | button down / up **in place**, on the relative pointer — the cursor does not move (v2) | `OK` |
| `MW <delta>` | wheel, −127…127 detents | `OK` |
| `MR` | release every button | `OK` |
| `R` | release **everything** | `OK` |
| `H` | heartbeat | `OK` |
| `S` | state, for diagnostics | `ST <mods> <keys> <btns> <x> <y>` |

The board releases everything by itself if nothing arrives for 2.5 s while something is held, and
the moment its port is closed. While a key is deliberately held the bot sends `H` every 700 ms.

**Never open the port at 1200 baud** — that resets a 32U4 into its bootloader.
