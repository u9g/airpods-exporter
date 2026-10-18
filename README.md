# airpods-exporter

A Prometheus exporter for AirPods (and other Bluetooth headphones) on Windows.

It watches three things at once, so that when audio crackles, stutters or cuts out you can tell where it went wrong:

- **What Windows sends.** A WASAPI loopback tap on the headphones' output measures level, clipping,
  audio-engine glitches, gaps and click-like jumps in the exact audio handed to Bluetooth.
- **Windows' view of the device.** Whether the stereo output exists and is the default, whether an app has
  the mic open (which forces low-quality "call mode"), and which apps are streaming to it.
- **The radio.** Whether the classic Bluetooth link is connected, plus signal strength, battery and whether
  they are being worn, from the headphones' own Bluetooth LE broadcasts.
- **What else was going on at the PC.** Whether the display was on and anyone was using the keyboard or mouse,
  volume and mute (for the output and per app, since dictation tools mute other apps), Wi-Fi channel and
  traffic, CPU time spent in drivers (DPCs and interrupts), the other paired Bluetooth devices, and devices
  being plugged in or removed.

If the audio Windows sends is clean while you hear problems, the fault is in the radio link or the headphones,
not the PC. No admin rights are needed.

## Requirements

- Windows 10 version 2004 (build 19041) or later
- .NET 9 SDK to build

## Build

```powershell
dotnet publish src -c Release -o out
```

## Run

Interactively, printing a line per second and a summary with hints when you press Ctrl+C:

```powershell
out\AirPodsExporter.exe
```

As a Prometheus exporter, quietly in the background:

```powershell
out\AirPodsExporter.exe --metrics-port 9410 --quiet
```

| Option | Meaning |
| --- | --- |
| `--name <text>` | Match audio and Bluetooth devices whose name contains this. Default `AirPods Max`. |
| `--model <hex>` | Apple model ID to read battery and signal from BLE broadcasts. Default `202D`. |
| `--metrics-port <port>` | Serve metrics on `http://127.0.0.1:<port>/metrics`. |
| `--loki-url <url>` | Also send events to Loki at this base URL, e.g. `http://127.0.0.1:3100`. |
| `--quiet` | Don't print a line every second. Events and the summary are still printed. |
| `--seconds <n>` | Stop after `n` seconds instead of running until Ctrl+C. |
| `--csv <path>` | Per-second CSV, with a `.log` next to it. On by default unless `--metrics-port` is set. |
| `--wav <path>` | Also record the exact audio sent to the headphones as a 32-bit float WAV. |

To find your headphones' model ID, open Device Manager, find the headphones under Bluetooth, and look for
`PID&xxxx` in Details → Hardware Ids. If the exporter hears no broadcasts for the model you gave it, its
summary lists the Apple model IDs it did hear.

## Prometheus and Grafana

Add the scrape job from [`prometheus/scrape-config.yml`](prometheus/scrape-config.yml) to your Prometheus
config. [`grafana/airpods.json`](grafana/airpods.json) is a ready-made dashboard; it expects a Prometheus data
source with the UID `prometheus` and a Loki data source with the UID `loki`, so change those UIDs in the file
if yours differ.

## Events

Besides metrics, the exporter notices events: the Bluetooth link changing, the headphones being put on or
taken off or changing other broadcast flags, the default output or volume changing, an app being muted,
the display turning on or off, the PC going idle or being used again, Wi-Fi roaming or changing band,
another Bluetooth device connecting, devices being plugged in or removed, and which apps are sending.
They are printed, and with `--loki-url` also sent to Loki as `{job="airpods", service_name="airpods-exporter"}`.
The dashboard shows them in an Events panel and as annotations on every graph.

## Metrics

| Metric | Type | Meaning |
| --- | --- | --- |
| `airpods_bluetooth_connected` | gauge | 1 while the classic Bluetooth link (which carries the audio) is connected |
| `airpods_output_present` | gauge | 1 while the stereo output exists in Windows |
| `airpods_output_is_default` | gauge | 1 while it is the default output |
| `airpods_hands_free_enabled` | gauge | 1 while the Hands-Free (call mode) devices are enabled |
| `airpods_call_mode` | gauge | 1 while an app has the mic open, forcing call-quality audio |
| `airpods_audio_sending` | gauge | 1 if any audio was sent in the last second |
| `airpods_audio_coverage_ratio` | gauge | Audio sent in the last second relative to real time |
| `airpods_audio_peak_dbfs` | gauge | Peak level sent in the last second (-120 = digital silence) |
| `airpods_audio_rms_dbfs` | gauge | RMS level sent in the last second (-120 = digital silence) |
| `airpods_audio_sample_rate_hertz` | gauge | Sample rate of the output |
| `airpods_app_sending{app}` | gauge | 1 for each app with an active stream to the headphones |
| `airpods_signal_rssi_dbm` | gauge | Strength of the headphones' BLE broadcasts at the PC |
| `airpods_signal_age_seconds` | gauge | Seconds since a broadcast was last heard |
| `airpods_battery_percent` | gauge | Battery level from the broadcasts, in 10% steps |
| `airpods_audio_seconds_total` | counter | Seconds of audio sent |
| `airpods_audio_gap_seconds_total` | counter | Seconds missing from the stream, including normal pauses |
| `airpods_audio_glitches_total` | counter | Windows audio-engine glitches (data discontinuities) |
| `airpods_audio_clicks_total` | counter | Click-like jumps in the audio sent (heuristic) |
| `airpods_audio_clipped_samples_total` | counter | Samples sent at or above full scale |
| `airpods_bluetooth_link_changes_total` | counter | Times the Bluetooth link connected or disconnected |
| `airpods_ble_broadcasts_total` | counter | Headphone broadcasts heard |
| `airpods_broadcast_rssi_dbm` | histogram | Signal strength of every broadcast heard, so dips between scrapes are kept |
| `airpods_worn` | gauge | 1 while the headphones report being worn |
| `airpods_ble_status` | gauge | Raw status byte of the broadcasts, for decoding other flags |
| `airpods_output_volume_ratio` | gauge | The output's volume slider in Windows (0-1) |
| `airpods_output_muted` | gauge | 1 while the output is muted in Windows |
| `airpods_app_volume_ratio{app}` | gauge | Each sending app's slider in the Windows volume mixer |
| `airpods_app_muted{app}` | gauge | 1 while a sending app is muted in the mixer |
| `airpods_app_peak_dbfs{app}` | gauge | Each sending app's current peak level (-120 = silent) |
| `airpods_pc_display_state` | gauge | The PC's display: 0 = off, 1 = on, 2 = dimmed |
| `airpods_pc_idle_seconds` | gauge | Seconds since the last keyboard or mouse input |
| `airpods_pc_cpu_seconds_total{cpu,mode}` | counter | CPU time per CPU in `idle`, `user`, `system`, `dpc` and `interrupt` |
| `airpods_pc_wifi_connected` | gauge | 1 while connected to Wi-Fi |
| `airpods_pc_wifi_info{ssid,bssid,band}` | gauge | The network and access point in use |
| `airpods_pc_wifi_channel` | gauge | Wi-Fi channel (1-14 share the 2.4 GHz band with Bluetooth) |
| `airpods_pc_wifi_signal_percent` | gauge | Wi-Fi signal quality |
| `airpods_pc_wifi_receive_rate_mbps`, `airpods_pc_wifi_transmit_rate_mbps` | gauge | Wi-Fi link rates |
| `airpods_pc_network_bytes_total{interface,type,direction}` | counter | Bytes moved by each network adapter |
| `airpods_pc_bluetooth_device_connected{device,transport}` | gauge | 1 for each paired Bluetooth device that is connected |
| `airpods_loki_queued_lines`, `airpods_loki_push_failures_total`, `airpods_loki_dropped_lines_total` | | Event delivery to Loki, with `--loki-url` |

## Caveats

- Signal strength comes from the headphones' low-power status broadcasts, not from the audio stream itself.
  Treat it as a relative gauge: changes matter more than the absolute number.
- Loopback only sees audio while something is playing, so gaps include ordinary pauses in playback.
- Click detection is a heuristic. It ignores ordinary music, but it has not been validated against real
  glitch clicks.
- Battery and signal decoding follows Apple's proximity-pairing broadcast format and only works for Apple
  headphones. "Worn" is the format's in-ear bit (`0x02` of the status byte); on AirPods Max it was seen set
  while on a head (`0x2B`) and clear once taken off (`0x29`, `0x21`).
- Nothing on the PC can see audio that Windows sent but the headphones never played, for example while they
  were switched to another Apple device. If the audio sent was clean and you still heard nothing, that is where
  it went missing.
