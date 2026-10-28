# Monitor Auto-Switch — macOS agent

This is the macOS half of `monitor-auto-switch-spec.pdf`. It watches for a "sentinel" USB device that sits behind the UGREEN USB switch. When the sentinel appears on this Mac, it switches every configured monitor to its Mac input (HDMI) over DDC/CI. When the sentinel disappears, it switches them to the PC input (DisplayPort).

It is a single Swift binary (`mas`) that runs either as a menu bar agent or as a diagnostic CLI. It is built for arm64, so the same binary runs on the M2 Pro and on the M5.

## Install

```sh
./install.sh            # builds release, installs ~/.local/bin/mas
mas init-config         # writes config pre-filled with the connected monitors' EDID
mas list-usb            # pick sentinel VID:PIDs, put them in `sentinels`
mas list-monitors       # check DDC is reachable and see the current input values
./install.sh --agent    # start at login (LaunchAgent) + launch the menu bar agent now
```

The binary is unsigned. If you build it on one Mac and copy it to the other, clear quarantine with `xattr -d com.apple.quarantine ~/.local/bin/mas`, or right-click → Open once if you put it in an app folder.

Requirements: Xcode command line tools (Swift 5.10+), Apple Silicon, macOS 13+.

## Files

| What | Where |
|---|---|
| Config | `~/Library/Application Support/MonitorAutoSwitch/config.toml` |
| Log (rotating, 2 MB × 3) | `~/Library/Logs/MonitorAutoSwitch/mas.log` |
| LaunchAgent | `~/Library/LaunchAgents/com.monitorautoswitch.agent.plist` |

## Commands

```
mas agent                 menu bar agent (started by the LaunchAgent)
mas watch                 foreground, verbose logging to the terminal
mas list-usb              USB devices with VID:PID (sentinels marked)
mas list-monitors         monitors, EDID, DDC reachable?, current input
mas get-input <mon>       read current input
mas set-input <mon> <v>   write input value, e.g. 0x0F
mas get-vcp <mon> <code>  read any VCP code (0x10 = brightness) to test DDC
mas to-mac | to-pc        switch all configured monitors now
mas install-agent | uninstall-agent
```

The menu bar menu shows the sentinel state and the last switch result. It also has these items: Switch to Mac now, Switch to PC now, Pause, Open config, Reload config, Open log, Start at login, and Quit.

## Behaviour (spec §3.2)

- **Debounce**: acts on the settled sentinel state `debounce_ms` after the last USB event. It only acts when the settled state actually changed, so it never switches at startup or on wake.
- **Idempotent**: it reads the input VCP code first and skips the write if the monitor is already on the target input.
- **Retry with backoff + verification**: it retries on the `retry_delays_ms` schedule (default 300/600/1200 ms) and reads the input back after each write. Displays are re-enumerated on every attempt. A monitor that drops off after being switched away counts as "sent, not verified", not as a failure.
- **Monitor identification**: by EDID manufacturer + model (product name or numeric product ID) + serial (numeric or alphanumeric; leave it empty to match any).
- **Sentinel group**: the group counts as present if any listed VID:PID is connected.
- **Unreliable readout**: set `reliable_readback = false` on a monitor whose VCP 0x60 readout can't be trusted. The AOC 24G1WG4 reports `0x11` whatever input it is on. The agent then always writes and doesn't verify. In this setup the 24G1WG4 also uses `mac_input = 0x12` (HDMI 2); see `../windows/README.md`.
- **LG 0xF4**: set `vcp_code = 0xF4`. The DDC sub-address then defaults to 0x50, as MonitorControl and m1ddc do. You can override it with `i2c_address`.
- **Fallback**: with `ddc_backend = "auto"`, a failed native write falls back to `external_command`. The default command is m1ddc (`brew install m1ddc`). For BetterDisplay, set something like `external_command = "betterdisplaycli set --name='{product}' --ddc --vcp=0x60 --value={value}"`. Check the exact syntax with `betterdisplaycli help`.
- **Start at login**: the LaunchAgent restarts the agent after a crash. Quitting from the menu keeps it quit.

## Troubleshooting DDC

If `mas list-monitors` shows `NOT reachable … 0xe0114000`, the monitor did not acknowledge the I2C transfer. In order of likelihood:
1. DDC/CI is disabled in the monitor's OSD. On AOC monitors it is under OSD → Extra → DDC/CI → Yes.
2. The cable or hub doesn't pass DDC. Swap the two monitors' paths (cable ↔ hub) to find out which one is at fault.
3. The monitor doesn't support DDC on that input. The Windows agent can still do both directions over DisplayPort, but only while the PC is awake.
