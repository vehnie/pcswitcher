# Monitor Auto-Switch — Windows agent

This is the Windows half of `monitor-auto-switch-spec.pdf`, and it mirrors the macOS agent in `../macos`: same config schema, same commands, same switching logic. It watches the sentinel USB device(s) behind the USB switch. When the sentinel appears on this PC, every configured monitor goes to `pc_input` (DisplayPort). When it disappears, they go to `mac_input` (HDMI).

It is built with the C# compiler that ships with Windows (.NET Framework 4.8), so there is no SDK to install and nothing for the exe to depend on.

## Build and install

```powershell
.\build.ps1              # -> bin\mas.exe (CLI) and bin\MonitorAutoSwitch.exe (tray agent, no console window)
.\bin\mas.exe init-config
.\bin\mas.exe list-usb       # put the sentinel VID:PIDs in `sentinels`
.\bin\mas.exe list-monitors  # check DDC is reachable and see the current input values
.\build.ps1 -Install     # copy to %LOCALAPPDATA%\Programs\MonitorAutoSwitch, start at login, start the agent
```

Start at login uses a Task Scheduler logon task called `MonitorAutoSwitch`, with no time limit and running on battery too. If creating the task is not allowed, it falls back to the `HKCU\...\Run` registry key.

## Files

| What | Where |
|---|---|
| Config | `%APPDATA%\MonitorAutoSwitch\config.toml` (same file format as the Mac's, so you can copy it across) |
| Log (rotating, 2 MB × 3) | `%APPDATA%\MonitorAutoSwitch\logs\mas.log` |

`MAS_CONFIG_DIR` overrides the config and log folder, which is useful for testing.

## Commands

```
mas agent | watch | list-usb | list-monitors | get-input <mon> | set-input <mon> <v>
mas get-vcp <mon> <code> | to-mac | to-pc | init-config [--force] | install-agent | uninstall-agent
```

The tray menu has: sentinel state, last switch result, Switch to Mac now, Switch to PC now, Pause, Open config, Reload config, Open log, Start at login, and Quit. The icon turns grey while paused.

## Notes

- Monitors are identified by EDID (manufacturer, model or product ID, and serial), read from the registry. The values match what macOS reports, so the same `[[monitors]]` entries work on both machines.
- Monitors and their handles are re-enumerated on every attempt. A monitor that has dropped off the desktop is logged as "not found", and the Mac agent covers that case.
- After a successful write, a monitor that rejects DDC while it changes input is given until the next check (300/600/1200 ms) before the write is sent again.
- `reliable_readback = false` (per monitor) is for monitors whose VCP 0x60 readout can't be trusted. The AOC 24G1WG4 reports `0x11` whatever input it is on. With this setting the agent always writes, never skips the monitor as "already on target", and doesn't verify.
- Input values differ per monitor and per port. In this setup the Mac is on HDMI 2 (`0x12`) of the 24G1WG4 and HDMI 1 (`0x11`) of the C27G4Z. A monitor that goes black and then returns to the other computer is being switched to an empty port, after which its auto input detection falls back to the live signal.
- Tested on this PC with AOC C27G4Z and AOC 24G1WG4 over DisplayPort: DDC works on both, and a real switch took 546 ms and was verified by reading back.
