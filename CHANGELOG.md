# Changelog

All notable changes to the **Bluetooth Device Battery Plugin** for InfoPanel will be documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.2.1] - 2025-04-03

### Changed
- **Optimized GATT Implementation:** Removed the separate background monitoring task (`StartMonitoringAsync`) and integrated update logic directly into `UpdateAsync`, relying on InfoPanel's update cycle.
- **Connection Caching:** Implemented caching for `BluetoothLEDevice` objects. The plugin now only attempts to establish a new LE connection (using the device's Bluetooth Address) if the cached object is null or reports as disconnected, reducing overhead.
- **Improved Resource Management:** Enhanced disposal logic for `BluetoothLEDevice` objects during updates, resets, and plugin disposal using `IDisposable` and `using` statements where appropriate.
- **Refined Status Reporting:** Updated status messages to be more specific (e.g., "Connected (No Batt Svc)", "Device Unreachable", "LE Connect Error").
- **Code Cleanup:** Removed redundant methods and variables associated with the background task and previous PnP/PInvoke attempts. Standardized comments to `//`. Corrected namespace to `InfoPanel.BTData`.

### Fixed
- Addressed CS8600 nullable reference type warnings during configuration loading.

### Notes
- This version focuses solely on the standard GATT Battery Service approach. Attempts to use Windows PnP properties (via UWP or P/Invoke) were unsuccessful due to API limitations or unreliability in retrieving the necessary data and have been removed.
- **iOS devices remain unsupported** due to iOS restrictions on exposing the GATT Battery Service to Windows.

## [1.2.0] - 2025-04-01

*(Based on code comments in the original v1.2.0 provided)*

### Changed
- **Focus on GATT:** Explicitly dropped attempts to support RFCOMM/HFP protocols. Focus shifted solely to Bluetooth LE and the standard GATT Battery Service (`0x180F`).
- **iOS Incompatibility Noted:** Acknowledged that modern iOS versions restrict access to the Battery Service over BLE from Windows PCs, making monitoring infeasible.

### Fixed
- Addressed potential UI freezes during plugin reload/disable by making the shutdown process (`DisposeAsync`) asynchronous and non-blocking.
- Resolved potential deadlocks during plugin re-enabling by using locks correctly around sensor access and managing monitoring state.
- Corrected C# compiler warning CS1996 by ensuring `await` calls were not used inside `lock` blocks where not appropriate.

## [1.0.0] - 2025-03-31

### Added
- Initial release of the Bluetooth Device Battery Plugin.
- Multi-device monitoring for all paired Bluetooth LE devices.
- Separate UI containers for each device with:
  - `Name`: Displays the device’s friendly name.
  - `Status`: Shows "Connected", "Disconnected", or "Error".
  - `Battery Level`: Reports battery percentage (0-100%) for connected devices with a battery service (UUID `0x180F`).
- Configurable refresh interval via `.ini` file (default: 5 minutes).
- Background task (`StartMonitoringAsync`) for device polling and updates.
- Basic retry logic for Bluetooth operations.
- Initial implementation of `IDisposable` for resource cleanup.

### Notes
- Battery level reporting depends on device support for the standard GATT Battery Service.