using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams; // Added for DataReader
using InfoPanel.Plugins;
using IniParser;
using IniParser.Model;

/*
 * Plugin: InfoPanel.BTData
 * Version: 1.2.0
 * Author: F3NN3X / Themely.dev
 * Description: A streamlined InfoPanel plugin to monitor Bluetooth LE devices supporting the GATT Battery Service (UUID 0x180F). Tracks connection status and battery percentage, updating every 5 minutes (configurable via INI). Designed for devices like headsets; iOS devices (iPhone/iPad) are not supported due to restricted BLE access.
 * Changelog:
 *   - v1.2.0 (Apr 01, 2025): Dropped RFCOMM/HFP support for iOS devices; now BLE-only for GATT Battery Service. Simplified code, improved stability.
 *   - v1.0.0 (Mar 31, 2025): Initial release with multi-device monitoring, added Name sensor to containers.
 * Note: Requires Bluetooth capability in the host application (InfoPanel). iOS devices no longer expose Battery Service to Windows PCs as of modern versions (e.g., iOS 17+), making BLE monitoring infeasible without proprietary workarounds.
 */

namespace InfoPanel.Extras
{
    public class BluetoothBatteryPlugin : BasePlugin, IDisposable
    {
        private string? _configFilePath;
        private int _refreshIntervalMinutes = 5;
        private DateTime _lastUpdate = DateTime.MinValue;
        private CancellationTokenSource? _cts;
        private volatile bool _isMonitoring;
        private readonly Dictionary<string, (PluginText Name, PluginText Status, PluginSensor BatteryLevel)> _deviceSensors = new();
        private const int RetryAttempts = 3;
        private const int RetryDelayMs = 1000;

        public BluetoothBatteryPlugin()
            : base("bluetooth-battery-plugin", "Bluetooth Device Battery", "Monitors BLE devices with GATT Battery Service - v1.2.0")
        {
        }

        public override string? ConfigFilePath => _configFilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(_refreshIntervalMinutes);

        public override void Initialize()
        {
            _cts = new CancellationTokenSource();
            LoadConfig();
            _ = StartMonitoringAsync(_cts.Token);
        }

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

        // Detects paired BLE devices; only those with potential GATT Battery Service support are included
        private async Task<List<(string Id, string Name, BluetoothDevice Device)>> GetDetectedDevicesAsync()
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
                var result = new List<(string, string, BluetoothDevice)>();
                foreach (var deviceInfo in devices)
                {
                    var device = await BluetoothDevice.FromIdAsync(deviceInfo.Id);
                    if (device != null && !string.IsNullOrEmpty(device.Name))
                        result.Add((deviceInfo.Id, device.Name, device));
                }
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Bluetooth Plugin: Error detecting devices: {0}", ex.Message);
                return new List<(string, string, BluetoothDevice)>();
            }
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var detectedDevices = GetDetectedDevicesAsync().GetAwaiter().GetResult();
            if (detectedDevices.Any())
            {
                foreach (var (id, name, _) in detectedDevices)
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
            Console.WriteLine("Bluetooth Plugin: Loaded successfully");
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            if (_cts?.IsCancellationRequested != false || (DateTime.UtcNow - _lastUpdate).TotalMinutes < _refreshIntervalMinutes)
                return;

            var detectedDevices = await GetDetectedDevicesAsync();
            foreach (var (id, _, _) in detectedDevices)
            {
                if (_deviceSensors.ContainsKey(id))
                    await UpdateDeviceDataAsync(id, cancellationToken);
            }

            _lastUpdate = DateTime.UtcNow;
            foreach (var (id, (name, status, battery)) in _deviceSensors)
            {
                Console.WriteLine("Bluetooth Plugin Update - Device: {0}, Status: {1}, Battery: {2}%", name.Value, status.Value, battery.Value);
            }
        }

        private async Task StartMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var detectedDevices = await GetDetectedDevicesAsync();
                    Console.WriteLine("Bluetooth Plugin: Detected {0} devices", detectedDevices.Count);
                    foreach (var (id, name, _) in detectedDevices)
                        Console.WriteLine(" - {0} (ID: {1})", name, id);

                    if (!detectedDevices.Any() && _isMonitoring)
                    {
                        ResetAllSensors();
                        _isMonitoring = false;
                    }
                    else if (detectedDevices.Any() && !_isMonitoring)
                    {
                        ResetAllSensors();
                        _cts?.Cancel();
                        _cts = new CancellationTokenSource();
                        _isMonitoring = true;
                        Console.WriteLine("Bluetooth Plugin: Starting monitoring for {0} device(s)", detectedDevices.Count);
                        foreach (var (id, _, _) in detectedDevices)
                            if (_deviceSensors.ContainsKey(id))
                                _ = MonitorDeviceWithRetryAsync(id, _cts.Token);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine("Bluetooth Plugin: Monitoring loop error: {0}", ex.Message);
            }
        }

        private async Task MonitorDeviceWithRetryAsync(string deviceId, CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= RetryAttempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    Console.WriteLine("Bluetooth Plugin: Monitoring device ID: {0} (attempt {1}/{2})", deviceId, attempt, RetryAttempts);
                    await UpdateDeviceDataAsync(deviceId, cancellationToken);
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Bluetooth Plugin: Monitor failed for device ID: {0} (attempt {1}/{2}): {3}", deviceId, attempt, RetryAttempts, ex.Message);
                    if (attempt == RetryAttempts)
                        ResetDeviceSensors(deviceId);
                    await Task.Delay(RetryDelayMs, cancellationToken);
                }
            }
        }

        // Updates device data using BLE GATT Battery Service only
        private async Task UpdateDeviceDataAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || !_deviceSensors.ContainsKey(deviceId))
                return;

            var (nameSensor, statusSensor, batterySensor) = _deviceSensors[deviceId];
            BluetoothDevice? device = null;
            BluetoothLEDevice? bleDevice = null;
            try
            {
                device = await BluetoothDevice.FromIdAsync(deviceId);
                if (device == null)
                {
                    statusSensor.Value = "Failed to connect";
                    batterySensor.Value = 0;
                    Console.WriteLine("Bluetooth Plugin: Failed to connect to {0}", nameSensor.Value);
                    return;
                }

                statusSensor.Value = device.ConnectionStatus == BluetoothConnectionStatus.Connected ? "Connected" : "Disconnected";
                Console.WriteLine("Bluetooth Plugin: Device {0} status: {1}, Address: {2}", nameSensor.Value, statusSensor.Value, device.BluetoothAddress.ToString("X12"));

                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    batterySensor.Value = 0;
                    return;
                }

                // BLE-only approach targeting GATT Battery Service (0x180F)
                bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(device.BluetoothAddress);
                if (bleDevice == null)
                {
                    Console.WriteLine("Bluetooth Plugin: BLE init failed for {0} - null device", nameSensor.Value);
                    statusSensor.Value += " (No Battery Service)";
                    batterySensor.Value = 0;
                    return;
                }

                var gattServices = await bleDevice.GetGattServicesForUuidAsync(
                    new Guid("0000180F-0000-1000-8000-00805F9B34FB"), // Battery Service
                    BluetoothCacheMode.Uncached
                );

                if (gattServices.Status != GattCommunicationStatus.Success || gattServices.Services.Count == 0)
                {
                    Console.WriteLine("Bluetooth Plugin: Battery Service not found for {0}", nameSensor.Value);
                    statusSensor.Value += " (No Battery Service)";
                    batterySensor.Value = 0;
                    return;
                }

                var batteryService = gattServices.Services[0];
                var characteristics = await batteryService.GetCharacteristicsForUuidAsync(
                    new Guid("00002A19-0000-1000-8000-00805F9B34FB"), // Battery Level
                    BluetoothCacheMode.Uncached
                );

                if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
                {
                    Console.WriteLine("Bluetooth Plugin: Battery Level characteristic not found for {0}", nameSensor.Value);
                    statusSensor.Value += " (No Battery Service)";
                    batterySensor.Value = 0;
                    return;
                }

                var characteristic = characteristics.Characteristics[0];
                var result = await characteristic.ReadValueAsync();
                if (result.Status == GattCommunicationStatus.Success)
                {
                    var reader = DataReader.FromBuffer(result.Value);
                    byte batteryLevel = reader.ReadByte();
                    batterySensor.Value = batteryLevel;
                    statusSensor.Value += $", Battery: {batteryLevel}%";
                    Console.WriteLine("Bluetooth Plugin: BLE Battery level for {0}: {1}%", nameSensor.Value, batteryLevel);
                }
                else
                {
                    Console.WriteLine("Bluetooth Plugin: Failed to read BLE battery level for {0}: {1}", nameSensor.Value, result.Status);
                    statusSensor.Value += " (No Battery Data)";
                    batterySensor.Value = 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Bluetooth Plugin: Error updating {0}: {1}", nameSensor.Value, ex.Message);
                statusSensor.Value = "Error";
                batterySensor.Value = 0;
            }
            finally
            {
                bleDevice?.Dispose();
                device?.Dispose();
            }
        }

        private void ResetDeviceSensors(string deviceId)
        {
            if (_deviceSensors.ContainsKey(deviceId))
            {
                var (_, status, battery) = _deviceSensors[deviceId];
                status.Value = "Disconnected";
                battery.Value = 0;
            }
        }

        private void ResetAllSensors()
        {
            foreach (var (_, status, battery) in _deviceSensors.Values)
            {
                status.Value = "Disconnected";
                battery.Value = 0;
            }
        }

        public override void Close() => Dispose();

        public void Dispose()
        {
            ResetAllSensors();
            _cts?.Cancel();
            _cts?.Dispose();
            _deviceSensors.Clear();
            _isMonitoring = false;
            Console.WriteLine("Bluetooth Plugin: Disposed");
            GC.SuppressFinalize(this);
        }

        ~BluetoothBatteryPlugin()
        {
            Dispose();
        }

        public override void Update() => throw new NotImplementedException();
    }
}