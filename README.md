# airpods-exporter

A Prometheus exporter for AirPods (and other Bluetooth headphones) on Windows.

It watches three things at once, so that when audio crackles, stutters or cuts out you can tell where it went wrong:

- **What Windows sends.** A WASAPI loopback tap on the headphones' output measures level, clipping,
  audio-engine glitches, gaps and click-like jumps in the exact audio handed to Bluetooth.
- **Windows' view of the device.** Whether the stereo output exists and is the default, whether an app has
  the mic open (which forces low-quality "call mode"), and which apps are streaming to it.
- **The radio.** Whether the classic Bluetooth link is connected, plus signal strength and battery from the
  headphones' own Bluetooth LE broadcasts.

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
source with the UID `prometheus`, so change that UID in the file if yours differs.

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

## Caveats

- Signal strength comes from the headphones' low-power status broadcasts, not from the audio stream itself.
  Treat it as a relative gauge: changes matter more than the absolute number.
- Loopback only sees audio while something is playing, so gaps include ordinary pauses in playback.
- Click detection is a heuristic. It ignores ordinary music, but it has not been validated against real
  glitch clicks.
- Battery and signal decoding follows Apple's proximity-pairing broadcast format and only works for Apple
  headphones.
