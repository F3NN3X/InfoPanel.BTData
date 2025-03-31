All notable changes to the **Bluetooth Device Battery Plugin** for InfoPanel will be documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.0.0] - 2025-03-31

### Added
- Initial release of the Bluetooth Device Battery Plugin.
- Multi-device monitoring for all paired Bluetooth LE devices.
- Separate UI containers for each device with:
  - `Name`: Displays the device’s friendly name.
  - `Status`: Shows "Connected", "Disconnected", or "Error".
  - `Battery Level`: Reports battery percentage (0-100%) for connected devices with a battery service (UUID `0x180F`).
- Configurable refresh interval via `PluginInfo.ini` (default: 5 minutes).
- Event-driven detection with a 5-second polling loop to detect device changes.
- Robust retry logic (3 attempts, 1-second delay) for Bluetooth operations.
- Proper resource cleanup using `IDisposable` and device disposal.

### Changed
- N/A (initial release).

### Fixed
- N/A (initial release).

### Notes
- Requires Bluetooth capability in the InfoPanel host application.
- Battery level reporting depends on device support for the Battery Service.
- Device list is static (set during plugin load); dynamic updates require reloading the plugin.