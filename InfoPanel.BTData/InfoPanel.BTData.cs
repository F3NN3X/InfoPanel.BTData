using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
 * Description: An optimized InfoPanel plugin to monitor Bluetooth LE device status and battery levels. Tracks connection status and battery percentage for all detected devices, with updates every 5 minutes (configurable via INI). Features event-driven detection, robust retry logic, and proper resource cleanup.
 * Changelog (Recent):
 *   - v1.0.0 (Mar 31, 2025): Initial release with multi-device monitoring, added Name sensor to containers.
 * Note: Full history in CHANGELOG.md. Requires Bluetooth capability in the host application (InfoPanel).
 */

namespace InfoPanel.Extras
{
    public class BluetoothBatteryPlugin : BasePlugin, IDisposable
    {
        // Configuration settings and plugin state
        private string? _configFilePath; // Path to the INI configuration file
        private int _refreshIntervalMinutes = 5; // Default refresh interval in minutes
        private DateTime _lastUpdate = DateTime.MinValue; // Timestamp of the last UI update
        private CancellationTokenSource? _cts; // Manages cancellation for async tasks
        private volatile bool _isMonitoring; // Indicates if the plugin is actively monitoring devices
        private readonly Dictionary<string, (PluginText Name, PluginText Status, PluginSensor BatteryLevel)> _deviceSensors = new(); // Maps device IDs to their UI sensors

        // Constants for retry logic and timing
        private const int RetryAttempts = 3; // Number of retries for Bluetooth operations
        private const int RetryDelayMs = 1000; // Delay between retries in milliseconds

        // Constructor initializing plugin metadata
        public BluetoothBatteryPlugin()
            : base(
                "bluetooth-battery-plugin",
                "Bluetooth Device Battery",
                "Monitors Bluetooth LE device status and battery levels - v1.0.0"
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

        // Loads configuration settings from an INI file, creates a default if none exists
        private void LoadConfig()
        {
            _configFilePath = $"{Assembly.GetExecutingAssembly().ManifestModule.FullyQualifiedName}.ini";
            var parser = new FileIniDataParser();
            IniData config;

            if (!File.Exists(_configFilePath))
            {
                config = new IniData();
                config["Bluetooth Battery Plugin"]["RefreshIntervalMinutes"] = "5";
                parser.WriteFile(_configFilePath, config);
                _refreshIntervalMinutes = 5;
            }
            else
            {
                config = parser.ReadFile(_configFilePath);
                if (!int.TryParse(config["Bluetooth Battery Plugin"]["RefreshIntervalMinutes"], out _refreshIntervalMinutes) || _refreshIntervalMinutes <= 0)
                    _refreshIntervalMinutes = 5;
            }

            Console.WriteLine("Bluetooth Plugin: Refresh interval: {0} minutes", _refreshIntervalMinutes);
        }

        // Detects paired Bluetooth LE devices and returns their info (ID and friendly name)
        private async Task<List<(string Id, string Name)>> GetDetectedDevicesAsync()
        {
            try
            {
                string[] requestedProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" };
                var devices = await DeviceInformation.FindAllAsync(
                    BluetoothLEDevice.GetDeviceSelector(),
                    requestedProperties
                );

                return devices
                    .Where(d => !string.IsNullOrEmpty(d.Name))
                    .Select(d => (d.Id, d.Name))
                    .ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Bluetooth Plugin: Error detecting devices: {0}", ex.ToString());
                return new List<(string, string)>();
            }
        }

        // Registers the plugin's sensors with InfoPanel's UI containers, one per detected device
        public override void Load(List<IPluginContainer> containers)
        {
            var detectedDevices = GetDetectedDevicesAsync().GetAwaiter().GetResult(); // Synchronous call during load

            if (detectedDevices.Any())
            {
                foreach (var (id, name) in detectedDevices)
                {
                    var nameSensor = new PluginText($"name_{id.GetHashCode()}", "Name", name);
                    var statusSensor = new PluginText($"status_{id.GetHashCode()}", "Status", "Disconnected");
                    var batterySensor = new PluginSensor($"battery_{id.GetHashCode()}", "Battery Level", 0, "%");
                    _deviceSensors[id] = (nameSensor, statusSensor, batterySensor);

                    var container = new PluginContainer($"Bluetooth Device - {name}");
                    container.Entries.Add(nameSensor);
                    container.Entries.Add(statusSensor);
                    container.Entries.Add(batterySensor);
                    containers.Add(container);
                }
            }
            else
            {
                var container = new PluginContainer("Bluetooth Device - None Detected");
                container.Entries.Add(new PluginText("name_none", "Name", "None"));
                container.Entries.Add(new PluginText("status_none", "Status", "No devices found"));
                container.Entries.Add(new PluginSensor("battery_none", "Battery Level", 0, "%"));
                containers.Add(container);
            }
        }

        // Updates the UI sensors periodically, respecting the refresh interval
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            if (_cts?.IsCancellationRequested != false)
                return; // Skip if cancelled

            DateTime now = DateTime.UtcNow;
            if ((now - _lastUpdate).TotalMinutes < _refreshIntervalMinutes)
                return; // Throttle updates to avoid unnecessary work

            var detectedDevices = await GetDetectedDevicesAsync();
            foreach (var (id, _) in detectedDevices)
            {
                if (_deviceSensors.ContainsKey(id))
                {
                    await UpdateDeviceDataAsync(id, cancellationToken);
                }
            }

            _lastUpdate = now;
            foreach (var (id, (name, status, battery)) in _deviceSensors)
            {
                Console.WriteLine(
                    "Bluetooth Plugin Update - Device: {0}, Status: {1}, Battery: {2}%",
                    name.Value,
                    status.Value,
                    battery.Value
                );
            }
            await Task.CompletedTask;
        }

        // Runs a continuous background loop to monitor all detected devices
        private async Task StartMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var detectedDevices = await GetDetectedDevicesAsync();
                    if (!detectedDevices.Any() && _isMonitoring)
                    {
                        ResetAllSensors();
                        _isMonitoring = false;
                        Console.WriteLine("Bluetooth Plugin: No devices detected; monitoring stopped");
                    }
                    else if (detectedDevices.Any() && !_isMonitoring)
                    {
                        ResetAllSensors();
                        _cts?.Cancel();
                        _cts = new CancellationTokenSource();
                        _isMonitoring = true;
                        Console.WriteLine("Bluetooth Plugin: Starting monitoring for {0} device(s)", detectedDevices.Count);
                        foreach (var (id, _) in detectedDevices)
                        {
                            if (_deviceSensors.ContainsKey(id))
                            {
                                _ = MonitorDeviceWithRetryAsync(id, _cts.Token); // Start monitoring each device
                            }
                        }
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

        // Attempts to monitor a device with retry logic in case of transient failures
        private async Task MonitorDeviceWithRetryAsync(string deviceId, CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= RetryAttempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break; // Exit retry loop if cancelled

                try
                {
                    Console.WriteLine("Bluetooth Plugin: Starting monitor for device ID: {0} (Attempt {1}/{2})", deviceId, attempt, RetryAttempts);
                    await UpdateDeviceDataAsync(deviceId, cancellationToken);
                    break; // Exit loop on success
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Bluetooth Plugin: Monitor failed for device ID: {0} (attempt {1}/{2}): {3}", deviceId, attempt, RetryAttempts, ex.ToString());
                    if (attempt == RetryAttempts)
                        ResetDeviceSensors(deviceId); // All attempts failed, reset sensors
                    await Task.Delay(RetryDelayMs, cancellationToken); // Wait before retrying
                }
            }
        }

        // Updates a device's connection status and battery level
        private async Task UpdateDeviceDataAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || !_deviceSensors.ContainsKey(deviceId))
                return; // Skip if cancelled or device not in UI

            var (_, statusSensor, batterySensor) = _deviceSensors[deviceId];
            BluetoothLEDevice? device = null;
            try
            {
                device = await BluetoothLEDevice.FromIdAsync(deviceId);
                if (device == null)
                {
                    statusSensor.Value = "Failed to connect";
                    batterySensor.Value = 0;
                    Console.WriteLine("Bluetooth Plugin: Failed to connect to device ID: {0}", deviceId);
                    return;
                }

                statusSensor.Value = device.ConnectionStatus == BluetoothConnectionStatus.Connected ? "Connected" : "Disconnected";

                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    batterySensor.Value = 0;
                    Console.WriteLine("Bluetooth Plugin: Device ID: {0} is disconnected", deviceId);
                    return;
                }

                // Query the Battery Service (UUID 0x180F)
                var gattServices = await device.GetGattServicesForUuidAsync(
                    new Guid("0000180F-0000-1000-8000-00805F9B34FB"),
                    BluetoothCacheMode.Uncached
                );

                if (gattServices.Status == GattCommunicationStatus.Success && gattServices.Services.Count > 0)
                {
                    var batteryService = gattServices.Services[0];
                    // Query the Battery Level characteristic (UUID 0x2A19)
                    var characteristics = await batteryService.GetCharacteristicsForUuidAsync(
                        new Guid("00002A19-0000-1000-8000-00805F9B34FB"),
                        BluetoothCacheMode.Uncached
                    );

                    if (characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0)
                    {
                        var characteristic = characteristics.Characteristics[0];
                        var result = await characteristic.ReadValueAsync();
                        if (result.Status == GattCommunicationStatus.Success)
                        {
                            var reader = Windows.Storage.Streams.DataReader.FromBuffer(result.Value);
                            byte batteryLevel = reader.ReadByte();
                            batterySensor.Value = batteryLevel;
                            Console.WriteLine("Bluetooth Plugin: Battery level for device ID: {0}: {1}%", deviceId, batteryLevel);
                        }
                        else
                        {
                            batterySensor.Value = 0;
                            Console.WriteLine("Bluetooth Plugin: Failed to read battery level for device ID: {0}: {1}", deviceId, result.Status);
                        }
                    }
                    else
                    {
                        batterySensor.Value = 0;
                        Console.WriteLine("Bluetooth Plugin: Battery Level characteristic not found for device ID: {0}", deviceId);
                    }
                }
                else
                {
                    batterySensor.Value = 0;
                    Console.WriteLine("Bluetooth Plugin: Battery Service not found or inaccessible for device ID: {0}", deviceId);
                }
            }
            catch (Exception ex)
            {
                statusSensor.Value = "Error";
                batterySensor.Value = 0;
                Console.WriteLine("Bluetooth Plugin: Error updating device ID: {0}: {1}", deviceId, ex.ToString());
            }
            finally
            {
                device?.Dispose(); // Clean up the device object to free resources
            }
        }

        // Resets all sensor values for a specific device to their defaults
        private void ResetDeviceSensors(string deviceId)
        {
            if (_deviceSensors.ContainsKey(deviceId))
            {
                var (_, status, battery) = _deviceSensors[deviceId];
                status.Value = "Disconnected";
                battery.Value = 0;
            }
        }

        // Resets all sensors for all devices to their defaults
        private void ResetAllSensors()
        {
            foreach (var (_, status, battery) in _deviceSensors.Values)
            {
                status.Value = "Disconnected";
                battery.Value = 0;
            }
        }

        // Cleans up resources when the plugin is closed
        public override void Close() => Dispose();

        // Implements IDisposable to ensure proper cleanup
        public void Dispose()
        {
            ResetAllSensors();
            _cts?.Cancel();
            _cts?.Dispose();
            _deviceSensors.Clear();
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