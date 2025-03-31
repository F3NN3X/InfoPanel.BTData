# Bluetooth Device Battery Plugin for InfoPanel

**Version**: 1.0.0  
**Author**: F3NN3X / Themely.dev  
**License**: MIT
**Repository**: [GitHub](https://github.com/F3NN3X/InfoPanel.BTData) (replace with your repo URL)

## Overview

The **Bluetooth Device Battery Plugin** is an optimized plugin for [InfoPanel](https://github.com/habibrehmansg/infopanel) that monitors the status and battery levels of Bluetooth Low Energy (LE) devices connected to your Windows system. It displays each detected device in a separate UI container, showing the device name, connection status, and battery percentage (if available). The plugin updates every 5 minutes by default (configurable via an INI file) and includes event-driven detection, robust retry logic, and proper resource cleanup.

### Features

- **Multi-Device Monitoring**: Tracks all paired Bluetooth LE devices simultaneously.
- **UI Containers**: Each device gets its own section with:
  - `Name`: The device’s friendly name.
  - `Status`: Connection status ("Connected", "Disconnected", or "Error").
  - `Battery Level`: Battery percentage (0-100%) for connected devices with a battery service.
- **Configurable Refresh Interval**: Set via `InfoPanel.BTData.dll.ini` (default: 5 minutes).
- **Robust Design**: Handles device disconnections, retries failed operations, and cleans up resources properly.

## Installation

1. **Download the Release**:
   - Go to the [Releases](https://github.com/F3NN3X/InfoPanel.BTData/releases) page.
   - Download the latest ZIP file for version `1.0.0` (e.g., `InfoPanel.BTData-v1.0.0.zip`).

2. **Import into InfoPanel**:
   - Open InfoPanel.
   - Navigate to the **Plugins** page.
   - Click **Import Plugin** and select the downloaded ZIP file.
   - InfoPanel will extract and load the plugin automatically.

3. **Verify**:
   - Ensure Bluetooth is enabled on your system and at least one Bluetooth LE device is paired.
   - Check the InfoPanel UI to see your devices listed under "Bluetooth Device Battery".

## Configuration

The plugin generates a `InfoPanel.BTData.dll.ini` file in its directory upon first load. You can customize the refresh interval:

### `InfoPanel.BTData.dll.ini`
```ini
[Bluetooth Battery Plugin]
RefreshIntervalMinutes=5
```
RefreshIntervalMinutes: The interval (in minutes) at which the plugin updates device data. Default is 5. Set to any positive integer (e.g., 1 for 1 minute, 10 for 10 minutes).
To apply changes:

Edit the INI file in a text editor.
Save the file.
Reload the plugin in InfoPanel (e.g., by restarting InfoPanel or re-reload on the plugin page.)

### Requirements
**Operating System:** Windows 10 or later (with Bluetooth support).
**InfoPanel:** Latest version with plugin support.
**Bluetooth:** Enabled with paired Bluetooth LE devices. Devices must support the Battery Service (UUID 0x180F) for battery level reporting.

### Building from Source
Clone the repository:
```bash
git clone https://github.com/F3NN3X/InfoPanel.BTData.git
cd InfoPanel.BTData
```
Open the solution in Visual Studio 2022 (or later).
Restore NuGet packages:
ini-parser-netstandard (v2.5.2)
Microsoft.Windows.CsWinRT (v2.2.0)
Build the project targeting net8.0-windows10.0.22621.0 (or adjust to your Windows SDK version).
Copy the output (e.g., bin\Debug\InfoPanel.BTData.dll) to your InfoPanel plugins directory or package it into a ZIP.

### Troubleshooting
**No Devices Detected:** Ensure Bluetooth is enabled and devices are paired in Windows Settings > Devices > Bluetooth & other devices.
**Battery Level Always 0%:** The device must support the Bluetooth Battery Service (UUID 0x180F). Not all devices expose this.
**Plugin Not Loading:** Verify the Windows SDK is installed and the Microsoft.Windows.CsWinRT package is compatible with your target framework.

### Contributing
Contributions are welcome! Please:

Fork the repository.
Create a feature branch (git checkout -b feature/your-feature).
Commit your changes (git commit -m "Add your feature").
Push to the branch (git push origin feature/your-feature).
Open a Pull Request.