using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using InfoPanel.Plugins;
using IniParser;
using IniParser.Model;

/*
 * Plugin: InfoPanel.BTData
 * Version: 1.2.0
 * Author: F3NN3X / Themely.dev
 * Description: Monitors Bluetooth LE devices supporting the GATT Battery Service (UUID 0x180F). Tracks connection status and battery percentage, updating every 5 minutes (configurable via INI). iOS devices (iPhone/iPad) excluded due to restricted BLE access.
 * Changelog:
 *   - v1.2.0 (Apr 01, 2025): Dropped RFCOMM/HFP and iOS support; BLE-only for GATT Battery Service. Fixed UI freeze on reload/disable by making shutdown async and non-blocking. Fixed freeze on re-enable by locking device sensors and sequential monitoring. Fixed CS1996 by moving await outside lock blocks.
 *   - v1.0.0 (Mar 31, 2025): Initial release with multi-device monitoring, added Name sensor to containers.
 * Note: Requires Bluetooth capability in InfoPanel. iOS devices no longer expose Battery Service to Windows PCs as of modern versions (e.g., iOS 17+), making BLE monitoring infeasible.
 */

namespace InfoPanel.BTData
{
    public class BluetoothBatteryPlugin : BasePlugin, IDisposable
    {
        private string? _configFilePath;
        private int _refreshIntervalMinutes = 5;
        private DateTime _lastUpdate = DateTime.MinValue;
        private CancellationTokenSource? _cts;
        private Task? _monitoringTask;
        private volatile bool _isMonitoring;
        private readonly Dictionary<string, (PluginText Name, PluginText Status, PluginSensor BatteryLevel)> _deviceSensors = new();
        private readonly object _sensorLock = new();
        private const int RetryAttempts = 3;
        private const int RetryDelayMs = 1000;

        private static readonly Guid BatteryServiceUuid = new Guid("0000180F-0000-1000-8000-00805F9B34FB");
        private static readonly Guid BatteryLevelCharacteristicUuid = new Guid("00002A19-0000-1000-8000-00805F9B34FB");

        public BluetoothBatteryPlugin()
            : base("bluetooth-battery-plugin", "Bluetooth Device Battery", "Monitors BLE devices with GATT Battery Service - v1.2.0")
        {
        }

        public override string? ConfigFilePath => _configFilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(_refreshIntervalMinutes);

        public override void Initialize()
        {
            try
            {
                _cts = new CancellationTokenSource();
                LoadConfig();
                _monitoringTask = Task.Run(() => StartMonitoringAsync(_cts.Token));
                Console.WriteLine("Bluetooth Plugin: Initialized monitoring task");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Bluetooth Plugin: Initialize failed: {0}", ex.Message);
                throw;
            }
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

        private async Task<List<(string Id, string Name, BluetoothDevice Device)>> GetDetectedDevicesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
                var result = new List<(string, string, BluetoothDevice)>();
                foreach (var deviceInfo in devices)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var device = await BluetoothDevice.FromIdAsync(deviceInfo.Id);
                    if (device != null && !string.IsNullOrEmpty(device.Name))
                        result.Add((deviceInfo.Id, device.Name, device));
                    else
                        device?.Dispose();
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
            var detectedDevices = GetDetectedDevicesAsync(CancellationToken.None).GetAwaiter().GetResult();
            lock (_sensorLock)
            {
                _deviceSensors.Clear();
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
            }
            Console.WriteLine("Bluetooth Plugin: Loaded successfully");
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            if (_cts?.IsCancellationRequested != false || (DateTime.UtcNow - _lastUpdate).TotalMinutes < _refreshIntervalMinutes)
                return;

            var detectedDevices = await GetDetectedDevicesAsync(cancellationToken);
            var deviceIdsToUpdate = new List<string>();
            lock (_sensorLock)
            {
                foreach (var (id, _, _) in detectedDevices)
                {
                    if (_deviceSensors.ContainsKey(id))
                        deviceIdsToUpdate.Add(id);
                }
            }

            foreach (var id in deviceIdsToUpdate)
            {
                await UpdateDeviceDataAsync(id, cancellationToken);
            }

            _lastUpdate = DateTime.UtcNow;
            lock (_sensorLock)
            {
                foreach (var (id, (name, status, battery)) in _deviceSensors)
                {
                    Console.WriteLine("Bluetooth Plugin Update - Device: {0}, Status: {1}, Battery: {2}%", name.Value, status.Value, battery.Value);
                }
            }
        }

        private async Task StartMonitoringAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var detectedDevices = await GetDetectedDevicesAsync(cancellationToken);
                    Console.WriteLine("Bluetooth Plugin: Detected {0} devices", detectedDevices.Count);
                    foreach (var (id, name, _) in detectedDevices)
                        Console.WriteLine(" - {0} (ID: {1})", name, id);

                    if (!detectedDevices.Any() && _isMonitoring)
                    {
                        lock (_sensorLock)
                        {
                            ResetAllSensors();
                            _isMonitoring = false;
                        }
                        Console.WriteLine("Bluetooth Plugin: Stopped monitoring - no devices");
                    }
                    else if (detectedDevices.Any() && !_isMonitoring)
                    {
                        lock (_sensorLock)
                        {
                            ResetAllSensors();
                            _isMonitoring = true;
                        }
                        Console.WriteLine("Bluetooth Plugin: Starting monitoring for {0} device(s)", detectedDevices.Count);
                        foreach (var (id, _, _) in detectedDevices)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                break;
                            bool shouldMonitor;
                            lock (_sensorLock)
                            {
                                shouldMonitor = _deviceSensors.ContainsKey(id);
                            }
                            if (shouldMonitor)
                                await MonitorDeviceWithRetryAsync(id, cancellationToken);
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Bluetooth Plugin: Monitoring task canceled");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Bluetooth Plugin: Monitoring loop error: {0}", ex.Message);
            }
            finally
            {
                _isMonitoring = false;
                Console.WriteLine("Bluetooth Plugin: Monitoring task exited");
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

        private async Task UpdateDeviceDataAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            PluginText nameSensor;
            PluginText statusSensor;
            PluginSensor batterySensor;
            lock (_sensorLock)
            {
                if (!_deviceSensors.ContainsKey(deviceId))
                    return;
                (nameSensor, statusSensor, batterySensor) = _deviceSensors[deviceId];
            }

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

                bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(device.BluetoothAddress);
                if (bleDevice == null)
                {
                    Console.WriteLine("Bluetooth Plugin: BLE init failed for {0} - null device", nameSensor.Value);
                    statusSensor.Value += " (No Battery Service)";
                    batterySensor.Value = 0;
                    return;
                }

                var servicesResult = await bleDevice.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Uncached);
                if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
                {
                    Console.WriteLine("Bluetooth Plugin: Battery Service not found for {0} (Status: {1})", nameSensor.Value, servicesResult.Status);
                    statusSensor.Value += " (No Battery Service)";
                    batterySensor.Value = 0;
                    return;
                }

                var batteryService = servicesResult.Services[0];
                var characteristicsResult = await batteryService.GetCharacteristicsForUuidAsync(BatteryLevelCharacteristicUuid, BluetoothCacheMode.Uncached);
                if (characteristicsResult.Status != GattCommunicationStatus.Success || characteristicsResult.Characteristics.Count == 0)
                {
                    Console.WriteLine("Bluetooth Plugin: Battery Level characteristic not found for {0} (Status: {1})", nameSensor.Value, characteristicsResult.Status);
                    statusSensor.Value += " (No Battery Service)";
                    batterySensor.Value = 0;
                    return;
                }

                var batteryCharacteristic = characteristicsResult.Characteristics[0];
                var readResult = await batteryCharacteristic.ReadValueAsync();
                if (readResult.Status == GattCommunicationStatus.Success)
                {
                    var reader = DataReader.FromBuffer(readResult.Value);
                    byte batteryLevel = reader.ReadByte();
                    batterySensor.Value = batteryLevel;
                    statusSensor.Value += $", Battery: {batteryLevel}%";
                    Console.WriteLine("Bluetooth Plugin: BLE Battery level for {0}: {1}%", nameSensor.Value, batteryLevel);
                }
                else
                {
                    Console.WriteLine("Bluetooth Plugin: Failed to read battery level for {0} (Status: {1})", nameSensor.Value, readResult.Status);
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
            lock (_sensorLock)
            {
                if (_deviceSensors.ContainsKey(deviceId))
                {
                    var (_, status, battery) = _deviceSensors[deviceId];
                    status.Value = "Disconnected";
                    battery.Value = 0;
                }
            }
        }

        private void ResetAllSensors()
        {
            lock (_sensorLock)
            {
                foreach (var (_, status, battery) in _deviceSensors.Values)
                {
                    status.Value = "Disconnected";
                    battery.Value = 0;
                }
            }
        }

        public override void Close()
        {
            Console.WriteLine("Bluetooth Plugin: Close called");
            DisposeAsync().GetAwaiter().GetResult();
        }

        private async Task DisposeAsync()
        {
            Console.WriteLine("Bluetooth Plugin: Starting async dispose");
            lock (_sensorLock)
            {
                ResetAllSensors();
            }
            if (_cts != null)
            {
                _cts.Cancel();
                if (_monitoringTask != null)
                {
                    try
                    {
                        await Task.WhenAny(_monitoringTask, Task.Delay(2000));
                        if (_monitoringTask.IsCompleted)
                            Console.WriteLine("Bluetooth Plugin: Monitoring task completed during dispose");
                        else
                            Console.WriteLine("Bluetooth Plugin: Monitoring task timed out during dispose");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Bluetooth Plugin: Error during async dispose: {0}", ex.Message);
                    }
                }
                _cts.Dispose();
                _cts = null;
            }
            lock (_sensorLock)
            {
                _deviceSensors.Clear();
            }
            _isMonitoring = false;
            Console.WriteLine("Bluetooth Plugin: Disposed");
        }

        public void Dispose()
        {
            Console.WriteLine("Bluetooth Plugin: Dispose called");
            DisposeAsync().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }

        ~BluetoothBatteryPlugin()
        {
            Dispose();
        }

        public override void Update() => throw new NotImplementedException();
    }
}