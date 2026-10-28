# Monitor Auto-Switch

**Turn a cheap USB switch into a full KVM.** Press the button on your USB switch and your monitors follow, switching to the computer that now has your keyboard, webcam and headset. You don't have to touch the monitor buttons.

It works by running a small background agent on each computer, one for Windows and one for macOS. The agent watches for a "sentinel" USB device that sits behind the switch. When the sentinel arrives or leaves, the agent tells each monitor to change input over DDC/CI, the standard control channel built into most monitors.

```
            ┌───────────── USB switch ─────────────┐
            │  webcam · mic · headset (sentinel)   │
            └──────┬────────────────────────┬──────┘
                   │                        │
               Windows PC                  Mac
           (mas.exe tray agent)     (mas menu bar agent)
                   │  DisplayPort           │  HDMI
                   └──────► Monitors ◄──────┘
                        DDC/CI: VCP 0x60
```

| Event | Windows agent | macOS agent |
|---|---|---|
| Sentinel connects | monitors → PC input | monitors → Mac input |
| Sentinel disconnects | monitors → Mac input | monitors → PC input |

Both agents always agree on the target, which is the computer that just gained the sentinel. Running both gives redundancy: if one computer is asleep or can't reach a monitor, the other one still switches.

## Features

- **Fast**: on Windows a real switch took about 0.5 s, verified by reading the input back.
- **Safe to run on both computers**: each agent reads the current input first and skips monitors that are already on the target, so the two agents never fight or make the monitors bounce between inputs.
- **Debounce, retries and verification**: it coalesces the burst of USB events a switch produces, retries writes with backoff, and confirms each switch by reading the input back.
- **Matches monitors by EDID** (manufacturer, model, serial), not by enumeration order, so one config works on both computers.
- **Sentinel group**: list several devices; the sentinel counts as present if any of them is connected.
- **Per-monitor quirks**: input values, VCP code (for example LG's `0xF4`), and `reliable_readback = false` for monitors that report the wrong input.
- **Tray / menu bar UI**: Switch to PC now, Switch to Mac now, Pause, Open config, Open log, Start at login.
- **Diagnostic CLI** on both platforms, so you can find the right values without guessing.
- **Rotating log** explaining why each switch did or didn't happen.
- **No dependencies**: the Windows agent builds with the C# compiler that ships with Windows, and the macOS agent is a single Swift binary.

## Quick start

### Windows

```powershell
cd windows
.\build.ps1                    # -> bin\mas.exe (CLI) + bin\MonitorAutoSwitch.exe (tray)
.\bin\mas.exe init-config      # writes a config with your monitors pre-filled
.\bin\mas.exe list-usb         # pick your sentinel device(s)
.\bin\mas.exe list-monitors    # check DDC works, see current input values
.\build.ps1 -Install           # start at login + launch the tray agent
```

See [windows/README.md](windows/README.md).

### macOS (Apple Silicon)

```sh
cd macos
./install.sh                   # builds and installs ~/.local/bin/mas
mas init-config
mas list-usb
mas list-monitors
./install.sh --agent           # start at login + launch the menu bar agent
```

See [macos/README.md](macos/README.md).

## Configuration

Both agents use the same TOML file, so you can copy it between machines:

- Windows: `%APPDATA%\MonitorAutoSwitch\config.toml`
- macOS: `~/Library/Application Support/MonitorAutoSwitch/config.toml`

```toml
# USB device(s) behind the switch, as VID:PID in hex (from `mas list-usb`)
sentinels = ["0bda:132d", "0d8c:0005"]

debounce_ms     = 100
retries         = 3
retry_delays_ms = [300, 600, 1200]
verify_readback = true

on_connect    = true   # sentinel arrives here  -> switch monitors to THIS computer
on_disconnect = true   # sentinel leaves        -> switch monitors to the OTHER computer

[[monitors]]
name      = "Left"
match     = { manufacturer = "AOC", model = "24G1WG4", serial = "" }
vcp_code  = 0x60
mac_input = 0x12       # HDMI 2
pc_input  = 0x0F       # DisplayPort 1
reliable_readback = false   # this model always reports 0x11

[[monitors]]
name      = "Right"
match     = { manufacturer = "AOC", model = "C27G4Z", serial = "" }
vcp_code  = 0x60
mac_input = 0x11       # HDMI 1
pc_input  = 0x0F       # DisplayPort 1
```

Common VCP `0x60` input values are `0x0F` DisplayPort 1, `0x10` DisplayPort 2, `0x11` HDMI 1, `0x12` HDMI 2 and `0x1B` USB-C. Monitors differ, so confirm yours with `mas get-input <monitor>` while each computer is on screen.

The macOS agent also accepts `ddc_backend` (`auto`, `native` or `external`), `external_command` (a fallback such as [m1ddc](https://github.com/waydabber/m1ddc)) and a per-monitor `i2c_address`. The Windows agent ignores these keys.

## CLI

The same commands work on both platforms (`mas.exe` on Windows):

```
mas agent                 run the tray / menu bar agent
mas watch                 run in the foreground with verbose logging
mas list-usb              USB devices with VID:PID (sentinels marked)
mas list-monitors         monitors, EDID info, DDC reachable?, current input
mas get-input <mon>       read the current input
mas set-input <mon> <v>   set the input, e.g. 0x11
mas get-vcp <mon> <code>  read any VCP code (0x10 = brightness) to test DDC
mas to-mac | to-pc        switch all monitors now
mas init-config [--force] write a starter config
mas install-agent | uninstall-agent
```

## Compatibility and known limitations

- **Monitors** must support DDC/CI, and it must be enabled in the OSD (often under *Extra* or *Setup*).
- **Windows**: tested with two AOC monitors (C27G4Z, 24G1WG4) connected to a desktop GPU over DisplayPort. Some monitors drop the inactive DisplayPort link when showing another input, so the PC can't reach them. The agent logs this and leaves the switch to the other agent.
- **macOS on Apple Silicon** has no public DDC API. The agent uses the private `IOAVService` calls that [MonitorControl](https://github.com/MonitorControl/MonitorControl) and [m1ddc](https://github.com/waydabber/m1ddc) use. DDC through USB-C→HDMI converter cables and hubs often doesn't work, because many converter chips don't pass DDC through. It failed with the author's cable and hub. USB-C→DisplayPort and native HDMI ports are more likely to work. Run `mas list-monitors` to check your setup.
- **If only one side can do DDC**, which is usually the PC, that agent still handles both directions while that computer is awake. Turn on the monitors' auto input detection to cover the case where that computer is asleep: with no signal from it, the monitor falls back to the live input on its own.

## How it compares

[display-switch](https://github.com/haimgel/display-switch) is the original "USB switch as KVM" tool and inspired this one. Monitor Auto-Switch adds per-monitor quirk handling, a read-first check so both agents can run without fighting, readback verification, and a tray / menu bar UI. It is also deliberately small: no runtime or SDK to install.

## Project layout

```
macos/     Swift package: menu bar agent + CLI (IOKit USB notifications, IOAVService DDC)
windows/   C# (.NET Framework 4.8, built with the in-box csc): tray agent + CLI
           (WM_DEVICECHANGE USB notifications, dxva2 DDC/CI, EDID from the registry)
```

## Contributing

Issues and pull requests are welcome, especially reports of monitors, cables and hubs that do or don't pass DDC on Apple Silicon. Please include the output of `mas list-monitors`.

## License

[MIT](LICENSE)
