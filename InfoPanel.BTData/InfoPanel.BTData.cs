using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using InfoPanel.Plugins;
using IniParser;
using IniParser.Model;

/*
 * Plugin: InfoPanel.BTData
 * Version: 1.0.0
 * Author: F3NN3X / Themely.dev
 * Description: An optimized InfoPanel plugin to monitor Bluetooth LE device status and battery level.
 * Tracks connection status and battery percentage for a device specified by friendly name in an INI file. 
 * Updates every 5 minutes with event-driven detection, robust retry logic, and proper resource cleanup.
 * Changelog (Recent):
 *   - v1.0.0 (Mar 31, 2025): Initial release with device status and battery level monitoring.
 * Note: Full history in CHANGELOG.md. Requires Bluetooth capability in the host application (InfoPanel).
 */

namespace InfoPanel.Extras
{
    public class BluetoothBatteryPlugin : BasePlugin, IDisposable
    {
        // Sensors displayed in the InfoPanel UI
        private readonly PluginText _deviceName = new("device_name", "Device Name", "-");
        private readonly PluginText _status = new("status", "Status", "Disconnected");
        private readonly PluginSensor _batteryLevel = new("battery_level", "Battery Level", 0, "%");

        // Configuration settings and plugin state
        private string? _targetFriendlyName; // Friendly name of the target Bluetooth device from INI
        private string? _configFilePath; // Path to the INI configuration file
        private int _refreshIntervalMinutes = 5; // Default refresh interval in minutes
        private DateTime _lastUpdate = DateTime.MinValue; // Timestamp of the last UI update
        private CancellationTokenSource? _cts; // Manages cancellation for async tasks
        private volatile bool _isMonitoring; // Indicates if the plugin is actively monitoring a device
        private volatile string? _currentDeviceId; // ID of the currently monitored device

        // Constants for retry logic and timing
        private const int RetryAttempts = 3; // Number of retries for Bluetooth operations
        private const int RetryDelayMs = 1000; // Delay between retries in milliseconds

        // Constructor initializing plugin metadata
        public BluetoothBatteryPlugin()
            : base(
                "bluetooth-battery-plugin",
                "Bluetooth Device Battery",
                "Monitors Bluetooth LE device status and battery level - v1.0.0"
            )
        {
        }

        // Property exposing the INI file path to InfoPanel
        public override string? ConfigFilePath => _configFilePath;

        // Property defining the update interval for InfoPanel
        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(_refreshIntervalMinutes);

        // Initializes the plugin by loading config and starting background monitoring
        public override void Initialize()
        {
            _cts = new CancellationTokenSource();
            LoadConfig();
            _ = StartMonitoringAsync(_cts.Token); // Kick off the monitoring loop in the background
        }

        // Loads configuration settings from an INI file, creating a default if none exists
        private void LoadConfig()
        {
            _configFilePath = $"{Assembly.GetExecutingAssembly().ManifestModule.FullyQualifiedName}.ini";
            var parser = new FileIniDataParser();
            IniData config;

            if (!File.Exists(_configFilePath))
            {
                config = new IniData();
                config["Bluetooth Battery Plugin"]["FriendlyName"] = "";
                config["Bluetooth Battery Plugin"]["RefreshIntervalMinutes"] = "5";
                parser.WriteFile(_configFilePath, config);
                _targetFriendlyName = "";
                _refreshIntervalMinutes = 5;
            }
            else
            {
                config = parser.ReadFile(_configFilePath);
                _targetFriendlyName = config["Bluetooth Battery Plugin"]["FriendlyName"] ?? "";
                if (!int.TryParse(config["Bluetooth Battery Plugin"]["RefreshIntervalMinutes"], out _refreshIntervalMinutes) || _refreshIntervalMinutes <= 0)
                    _refreshIntervalMinutes = 5;
            }

            Console.WriteLine("Bluetooth Plugin: Target device: '{0}', Refresh interval: {1} minutes", _targetFriendlyName, _refreshIntervalMinutes);
        }

        // Registers the plugin's sensors with InfoPanel's UI container
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Bluetooth Device");
            container.Entries.AddRange([_deviceName, _status, _batteryLevel]);
            containers.Add(container);
        }

        // Updates the UI sensors periodically, respecting the refresh interval
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            if (_cts?.IsCancellationRequested != false)
                return; // Skip if cancelled

            DateTime now = DateTime.UtcNow;
            if ((now - _lastUpdate).TotalMinutes < _refreshIntervalMinutes)
                return; // Throttle updates to avoid unnecessary work

            if (string.IsNullOrEmpty(_targetFriendlyName))
            {
                ResetSensors();
                _status.Value = "No device specified";
                Console.WriteLine("Bluetooth Plugin: No target device specified in config");
            }
            else if (_currentDeviceId != null && _isMonitoring)
            {
                await UpdateDeviceDataAsync(_currentDeviceId, cancellationToken);
            }

            _lastUpdate = now;
            Console.WriteLine(
                "Bluetooth Plugin Update - Device: {0}, Status: {1}, Battery: {2}%",
                _deviceName.Value,
                _status.Value,
                _batteryLevel.Value
            );
            await Task.CompletedTask;
        }

        // Runs a continuous background loop to monitor device presence
        private async Task StartMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (string.IsNullOrEmpty(_targetFriendlyName))
                    {
                        ResetSensors();
                        _status.Value = "No device specified";
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        continue;
                    }

                    string? deviceId = await FindTargetDeviceAsync(cancellationToken);
                    if (deviceId == null && _currentDeviceId != null)
                    {
                        // Device is no longer available
                        ResetSensors();
                        _cts?.Cancel();
                        _cts = new CancellationTokenSource();
                        _currentDeviceId = null;
                        _isMonitoring = false;
                        Console.WriteLine("Bluetooth Plugin: Target device '{0}' not found; monitoring stopped", _targetFriendlyName);
                    }
                    else if (deviceId != null && !_isMonitoring)
                    {
                        // New device detected, start monitoring
                        ResetSensors();
                        _cts?.Cancel();
                        _cts = new CancellationTokenSource();
                        _currentDeviceId = deviceId;
                        _deviceName.Value = _targetFriendlyName;
                        Console.WriteLine("Bluetooth Plugin: Starting monitoring for device '{0}' (ID: {1})", _targetFriendlyName, deviceId);
                        await MonitorDeviceWithRetryAsync(deviceId, _cts.Token);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); // Check every 5 seconds
                }
            }
            catch (TaskCanceledException) { } // Normal on cancellation
            catch (Exception ex)
            {
                Console.WriteLine("Bluetooth Plugin: Monitoring loop error: {0}", ex.ToString());
            }
        }

        // Searches for the target Bluetooth device by its friendly name among paired devices
        private async Task<string?> FindTargetDeviceAsync(CancellationToken cancellationToken)
        {
            try
            {
                string[] requestedProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" };
                var devices = await DeviceInformation.FindAllAsync(
                    BluetoothLEDevice.GetDeviceSelector(),
                    requestedProperties,
                    cancellationToken
                );

                return devices.FirstOrDefault(d => d.Name.Equals(_targetFriendlyName, StringComparison.OrdinalIgnoreCase))?.Id;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Bluetooth Plugin: Error finding device '{0}': {1}", _targetFriendlyName, ex.ToString());
                return null;
            }
        }

        // Attempts to monitor the device with retry logic in case of transient failures
        private async Task MonitorDeviceWithRetryAsync(string deviceId, CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= RetryAttempts; attempt++)
            {
                try
                {
                    Console.WriteLine("Bluetooth Plugin: Starting monitor for device ID: {0} (Attempt {1}/{2})", deviceId, attempt, RetryAttempts);
                    _isMonitoring = true;
                    await UpdateDeviceDataAsync(deviceId, cancellationToken);
                    break; // Exit loop on success
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Bluetooth Plugin: Monitor failed (attempt {0}/{1}): {2}", attempt, RetryAttempts, ex.ToString());
                    _isMonitoring = false;
                    if (attempt < RetryAttempts)
                        await Task.Delay(RetryDelayMs, cancellationToken); // Wait before retrying
                    else
                        ResetSensors(); // All attempts failed, reset sensors
                }
            }
        }

        // Updates the device's connection status and battery level
        private async Task UpdateDeviceDataAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return; // Skip if cancelled

            BluetoothLEDevice? device = null;
            try
            {
                device = await BluetoothLEDevice.FromIdAsync(deviceId, cancellationToken);
                if (device == null)
                {
                    _status.Value = "Failed to connect";
                    _batteryLevel.Value = 0;
                    Console.WriteLine("Bluetooth Plugin: Failed to connect to device '{0}'", _targetFriendlyName);
                    return;
                }

                _status.Value = device.ConnectionStatus == BluetoothConnectionStatus.Connected ? "Connected" : "Disconnected";

                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    _batteryLevel.Value = 0;
                    Console.WriteLine("Bluetooth Plugin: Device '{0}' is disconnected", _targetFriendlyName);
                    return;
                }

                // Query the Battery Service (UUID 0x180F)
                var gattServices = await device.GetGattServicesForUuidAsync(
                    new Guid("0000180F-0000-1000-8000-00805F9B34FB"),
                    cancellationToken
                );

                if (gattServices.Status == GattCommunicationStatus.Success && gattServices.Services.Count > 0)
                {
                    var batteryService = gattServices.Services[0];
                    // Query the Battery Level characteristic (UUID 0x2A19)
                    var characteristics = await batteryService.GetCharacteristicsForUuidAsync(
                        new Guid("00002A19-0000-1000-8000-00805F9B34FB"),
                        cancellationToken
                    );

                    if (characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0)
                    {
                        var characteristic = characteristics.Characteristics[0];
                        var result = await characteristic.ReadValueAsync(cancellationToken);
                        if (result.Status == GattCommunicationStatus.Success)
                        {
                            var reader = Windows.Storage.Streams.DataReader.FromBuffer(result.Value);
                            byte batteryLevel = reader.ReadByte();
                            _batteryLevel.Value = batteryLevel;
                            Console.WriteLine("Bluetooth Plugin: Battery level for '{0}': {1}%", _targetFriendlyName, batteryLevel);
                        }
                        else
                        {
                            _batteryLevel.Value = 0;
                            Console.WriteLine("Bluetooth Plugin: Failed to read battery level: {0}", result.Status);
                        }
                    }
                    else
                    {
                        _batteryLevel.Value = 0;
                        Console.WriteLine("Bluetooth Plugin: Battery Level characteristic not found");
                    }
                }
                else
                {
                    _batteryLevel.Value = 0;
                    Console.WriteLine("Bluetooth Plugin: Battery Service not found or inaccessible");
                }
            }
            catch (Exception ex)
            {
                _status.Value = "Error";
                _batteryLevel.Value = 0;
                Console.WriteLine("Bluetooth Plugin: Error updating device '{0}': {1}", _targetFriendlyName, ex.ToString());
            }
            finally
            {
                device?.Dispose(); // Clean up the device object to free resources
            }
        }

        // Resets all sensor values to their defaults
        private void ResetSensors()
        {
            _deviceName.Value = "-";
            _status.Value = "Disconnected";
            _batteryLevel.Value = 0;
        }

        // Cleans up resources when the plugin is closed
        public override void Close() => Dispose();

        // Implements IDisposable to ensure proper cleanup
        public void Dispose()
        {
            ResetSensors();
            _cts?.Cancel();
            _cts?.Dispose();
            _currentDeviceId = null;
            _isMonitoring = false;
            Console.WriteLine("Bluetooth Plugin: Disposed; sensors reset, monitoring stopped");
            GC.SuppressFinalize(this);
        }

        // Finalizer ensures cleanup if Dispose isn’t called
        ~BluetoothBatteryPlugin()
        {
            Dispose();
        }

        // Synchronous update not implemented; use UpdateAsync instead
        public override void Update() => throw new NotImplementedException();
    }
}