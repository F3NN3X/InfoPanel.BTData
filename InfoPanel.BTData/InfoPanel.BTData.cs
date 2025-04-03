using System;
using System.Collections.Concurrent;
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
using System.IO;

/*
 * Plugin: InfoPanel.BTData
 * Version: 1.2.7 (Final-Final Nullability Fix)
 * Author: F3NN3X / Themely.dev
 * Description: Monitors Bluetooth LE devices supporting the standard GATT Battery Service (UUID 0x180F). Caches connections. NOTE: iOS devices (iPhone/iPad) are NOT supported due to restricted BLE access from Windows. Updates periodically (configurable via INI).
 * Changelog:
 *   - v1.2.7 (Apr 03, 2025): Fixed CS8602 warning in ResetAllSensorsInternal using explicit null check.
 *   - v1.2.6 (Apr 03, 2025): Fixed CS8602 warning in ResetAllSensorsInternal using null-forgiving operator.
 *   - v1.2.5 (Apr 03, 2025): Fixed CS8600 nullability warning in device name retrieval. Removed finalizer to resolve CS8602.
 * Note: Requires Bluetooth capability in InfoPanel. iOS devices do not expose Battery Service to Windows PCs, making monitoring infeasible via this method. Will only work for devices implementing standard GATT BAS.
 */

// Enable nullable reference types for better compile-time checking
#nullable enable

namespace InfoPanel.BTData
{
    public class BluetoothBatteryPlugin : BasePlugin, IDisposable
    {
        private string? _configFilePath;
        private int _refreshIntervalMinutes = 5;
        private bool _debugMode = false;
        private DateTime _lastUpdateAttempt = DateTime.MinValue;
        private CancellationTokenSource? _cts;

        private readonly ConcurrentDictionary<string, (PluginText Name, PluginText Status, PluginSensor BatteryLevel, BluetoothLEDevice? Device)> _deviceSensors = new();

        private static readonly Guid BatteryServiceUuid = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
        private static readonly Guid BatteryLevelCharacteristicUuid = new Guid("00002a19-0000-1000-8000-00805f9b34fb");

        private bool _isDisposed = false;

        public BluetoothBatteryPlugin()
            : base("bluetooth-battery-plugin", "Bluetooth Device Battery (GATT)", "Monitors BLE GATT Battery Service - v1.2.7") // Updated version
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
                Console.WriteLine("Bluetooth GATT Plugin: Initialized.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bluetooth GATT Plugin: Initialize failed: {ex.Message}");
                if (_debugMode) Console.WriteLine(ex.ToString());
                throw;
            }
        }

        private void LoadConfig()
        {
            string configSectionName = this.Name ?? "Bluetooth Device Battery (GATT)";
            if (this.Name == null)
            {
                Console.WriteLine($"Bluetooth GATT Plugin: Warning - Plugin Name empty, using default section '{configSectionName}'.");
            }

            try
            {
                string? configDir = null;
                try
                {
                    string? assemblyLocation = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(assemblyLocation))
                    {
                        configDir = Path.GetDirectoryName(assemblyLocation);
                    }
                    if (string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir))
                    {
                        configDir = AppContext.BaseDirectory;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Bluetooth GATT Plugin: Error determining config directory: {ex.Message}. Using defaults.");
                }

                if (string.IsNullOrEmpty(configDir))
                {
                    Console.WriteLine($"Bluetooth GATT Plugin: Error - Could not resolve valid config directory. Using defaults.");
                    _refreshIntervalMinutes = 5;
                    _debugMode = false;
                    _configFilePath = null;
                    return;
                }

                _configFilePath = Path.Combine(configDir, $"{this.Id}.ini");
                Console.WriteLine($"Bluetooth GATT Plugin: Config file path set to: {_configFilePath}");

                var parser = new FileIniDataParser();
                IniData config;

                if (!File.Exists(_configFilePath))
                {
                    config = new IniData();
                    config[configSectionName]["RefreshIntervalMinutes"] = "5";
                    config[configSectionName]["Debug"] = "false";
                    try
                    {
                        string? dirName = Path.GetDirectoryName(_configFilePath);
                        if (!string.IsNullOrEmpty(dirName) && !Directory.Exists(dirName))
                        {
                             Directory.CreateDirectory(dirName);
                        }
                        parser.WriteFile(_configFilePath, config);
                        Console.WriteLine($"Bluetooth GATT Plugin: Default config file created at {_configFilePath}");
                    }
                    catch (IOException ioEx)
                    {
                         Console.WriteLine($"Bluetooth GATT Plugin: IO Error writing default config '{_configFilePath}': {ioEx.Message}");
                    }
                    catch (UnauthorizedAccessException authEx)
                    {
                         Console.WriteLine($"Bluetooth GATT Plugin: Permissions Error writing default config '{_configFilePath}': {authEx.Message}");
                    }
                    catch (Exception ex)
                    {
                         Console.WriteLine($"Bluetooth GATT Plugin: General Error writing default config '{_configFilePath}': {ex.Message}");
                    }
                    _refreshIntervalMinutes = 5;
                    _debugMode = false;
                }
                else
                {
                    try
                    {
                        config = parser.ReadFile(_configFilePath);
                        string? refreshValue = config[configSectionName]?["RefreshIntervalMinutes"];
                        if (!int.TryParse(refreshValue, out _refreshIntervalMinutes) || _refreshIntervalMinutes <= 0)
                        {
                            Console.WriteLine($"Bluetooth GATT Plugin: Invalid/missing RefreshIntervalMinutes in section '{configSectionName}', using default 5.");
                            _refreshIntervalMinutes = 5;
                        }
                        string? debugValue = config[configSectionName]?["Debug"];
                        if (!bool.TryParse(debugValue ?? "false", out _debugMode))
                        {
                             Console.WriteLine($"Bluetooth GATT Plugin: Invalid/missing Debug value in section '{configSectionName}', using default false.");
                            _debugMode = false;
                        }
                    }
                     catch (IOException ioEx)
                    {
                        Console.WriteLine($"Bluetooth GATT Plugin: IO Error reading config '{_configFilePath}': {ioEx.Message}. Using defaults.");
                        _refreshIntervalMinutes = 5;
                        _debugMode = false;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Bluetooth GATT Plugin: Error parsing config '{_configFilePath}': {ex.Message}. Using defaults.");
                        _refreshIntervalMinutes = 5;
                        _debugMode = false;
                    }
                }
                Console.WriteLine($"Bluetooth GATT Plugin: Refresh interval set to {_refreshIntervalMinutes} minutes, Debug mode: {_debugMode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bluetooth GATT Plugin: Unexpected error loading config: {ex.Message}. Using defaults.");
                if (_debugMode) Console.WriteLine(ex.ToString());
                _refreshIntervalMinutes = 5;
                _debugMode = false;
                _configFilePath = null;
            }
        }

        private async Task<List<(string Id, string Name)>> GetPairedDevicesAsync(CancellationToken cancellationToken)
        {
            var result = new List<(string Id, string Name)>();
            try
            {
                var devicesInfo = await DeviceInformation.FindAllAsync(
                    BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                    new[] { "System.ItemNameDisplay" },
                    DeviceInformationKind.AssociationEndpoint)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var deviceInfo in devicesInfo)
                {
                    string name;
                    if (deviceInfo.Properties.TryGetValue("System.ItemNameDisplay", out object? nameObj) &&
                        nameObj is string nameStr &&
                        !string.IsNullOrEmpty(nameStr))
                    {
                        name = nameStr;
                    }
                    else
                    {
                        name = deviceInfo.Name ?? string.Empty;
                    }

                    if (!string.IsNullOrEmpty(deviceInfo.Id) && !string.IsNullOrEmpty(name))
                    {
                        result.Add((deviceInfo.Id, name));
                    }
                    else if (string.IsNullOrEmpty(name) && _debugMode)
                    {
                        Console.WriteLine($"Bluetooth GATT Plugin: Skipping device with missing Name (ID: {deviceInfo.Id ?? "N/A"})");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                 Console.WriteLine("Bluetooth GATT Plugin: Device enumeration cancelled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bluetooth GATT Plugin: Error detecting paired devices: {ex.Message}");
                if (_debugMode) Console.WriteLine(ex.ToString());
            }
            return result;
        }

        public override void Load(List<IPluginContainer> containers)
        {
            Console.WriteLine("Bluetooth GATT Plugin: Loading devices (synchronously)...");
            List<(string Id, string Name)> pairedDevices;
            try
            {
                 pairedDevices = GetPairedDevicesAsync(CancellationToken.None)
                                    .ConfigureAwait(false)
                                    .GetAwaiter()
                                    .GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bluetooth GATT Plugin: Failed to get paired devices during Load: {ex.Message}");
                if (_debugMode) Console.WriteLine(ex.ToString());
                pairedDevices = new List<(string Id, string Name)>();
            }

            Console.WriteLine($"Bluetooth GATT Plugin: Found {pairedDevices.Count} paired devices during Load.");
            ResetAllSensorsInternal();

            if (pairedDevices.Any())
            {
                foreach (var (id, name) in pairedDevices)
                {
                    int idHash = id.GetHashCode();
                    var nameSensor = new PluginText($"name_{idHash}", "Name", name);
                    var statusSensor = new PluginText($"status_{idHash}", "Status", "Unknown");
                    var batterySensor = new PluginSensor($"battery_{idHash}", "Battery Level", 0, "%");

                    _deviceSensors.TryAdd(id, (nameSensor, statusSensor, batterySensor, null));

                    var container = new PluginContainer($"BT - {name}");
                    container.Entries.Add(nameSensor);
                    container.Entries.Add(statusSensor);
                    container.Entries.Add(batterySensor);
                    containers.Add(container);
                }
            }
            else
            {
                var container = new PluginContainer("Bluetooth Devices");
                container.Entries.Add(new PluginText("status_none", "Status", "No paired devices found"));
                containers.Add(container);
                Console.WriteLine("Bluetooth GATT Plugin: No paired Bluetooth devices found during Load.");
            }
            Console.WriteLine("Bluetooth GATT Plugin: Load complete.");
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            if (_isDisposed) return;

            CancellationToken internalToken = _cts?.Token ?? CancellationToken.None;
            if (cancellationToken.IsCancellationRequested) return;
            if (internalToken.IsCancellationRequested) return;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(internalToken, cancellationToken);
            var combinedToken = linkedCts.Token;

            if (_lastUpdateAttempt != DateTime.MinValue && (DateTime.UtcNow - _lastUpdateAttempt) < UpdateInterval) return;

            var startTime = DateTime.UtcNow;
            _lastUpdateAttempt = DateTime.UtcNow;

            var deviceIdsToUpdate = _deviceSensors.Keys.ToList();
            if (!deviceIdsToUpdate.Any()) return;

            int successCount = 0;
            int failedCount = 0;

            if (_debugMode) Console.WriteLine($"Bluetooth GATT Plugin: Starting update cycle for {deviceIdsToUpdate.Count} device(s)...");

            foreach (var deviceId in deviceIdsToUpdate)
            {
                if (combinedToken.IsCancellationRequested)
                {
                    Console.WriteLine("Bluetooth GATT Plugin: UpdateAsync cycle cancelled mid-loop.");
                    break;
                }

                bool deviceSuccess = false;
                try
                {
                    deviceSuccess = await UpdateDeviceGattDataAsync(deviceId, combinedToken).ConfigureAwait(false);
                }
                catch(OperationCanceledException) when (combinedToken.IsCancellationRequested)
                {
                     deviceSuccess = false;
                     Console.WriteLine($"Bluetooth GATT Plugin: Update cancelled for device ID {deviceId}.");
                     // Optionally break;
                }
                catch (Exception ex)
                {
                    deviceSuccess = false;
                    Console.WriteLine($"Bluetooth GATT Plugin: Unhandled error during update loop for device ID {deviceId}: {ex.Message}");
                    if (_debugMode) Console.WriteLine(ex.ToString());
                    if (_deviceSensors.TryGetValue(deviceId, out var sensors))
                    {
                        sensors.Status.Value = "Update Error";
                        sensors.BatteryLevel.Value = 0;
                    }
                }
                finally
                {
                    if (!combinedToken.IsCancellationRequested || deviceSuccess)
                    {
                         if (deviceSuccess) successCount++; else failedCount++;
                    }
                }
            }

             var duration = DateTime.UtcNow - startTime;
            // Log summary if debug, errors occurred, or cancelled
            if (_debugMode || failedCount > 0 || combinedToken.IsCancellationRequested)
            {
                 string cancelledMsg = combinedToken.IsCancellationRequested ? " (Cancelled)" : "";
                 Console.WriteLine($"Bluetooth GATT Plugin: UpdateAsync completed in {duration.TotalSeconds:F2} seconds{cancelledMsg}. Success: {successCount}, Failed: {failedCount}.");
            }
        }


        private async Task<bool> UpdateDeviceGattDataAsync(string deviceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = false;

            if (!_deviceSensors.TryGetValue(deviceId, out var sensors)) return success;
            var (nameSensor, statusSensor, batterySensor, cachedBleDevice) = sensors;
            string deviceName = nameSensor.Value ?? "Unknown Device";

            BluetoothLEDevice? deviceToUse = cachedBleDevice;
            bool needsConnection = cachedBleDevice == null || cachedBleDevice.ConnectionStatus != BluetoothConnectionStatus.Connected;
            bool isNewlyConnected = false;

            if (needsConnection)
            {
                if (cachedBleDevice != null)
                {
                    cachedBleDevice.Dispose();
                    deviceToUse = null;
                    if (_deviceSensors.TryGetValue(deviceId, out var currentSensorsCheck) && ReferenceEquals(currentSensorsCheck.Device, cachedBleDevice))
                    {
                        _deviceSensors.TryUpdate(deviceId, (nameSensor, statusSensor, batterySensor, null), currentSensorsCheck);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                ulong bluetoothAddress = 0;
                try
                {
                    using var classicDevice = await BluetoothDevice.FromIdAsync(deviceId).AsTask(cancellationToken).ConfigureAwait(false);
                    if (classicDevice == null) { statusSensor.Value = "Device Not Found"; batterySensor.Value = 0; return success; }
                    bluetoothAddress = classicDevice.BluetoothAddress;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { statusSensor.Value = "Device Resolve Error"; batterySensor.Value = 0; Console.WriteLine($"GATT Plugin: Error getting BTDevice for {deviceName}: {ex.Message} (0x{ex.HResult:X8})"); if (_debugMode) Console.WriteLine(ex.ToString()); return success; }

                if (bluetoothAddress == 0) { statusSensor.Value = "Address Error"; batterySensor.Value = 0; return success; }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    deviceToUse = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask(cancellationToken).ConfigureAwait(false);
                    if (deviceToUse == null)
                    {
                        try { using var tempDev = await BluetoothDevice.FromIdAsync(deviceId).AsTask(CancellationToken.None).ConfigureAwait(false); statusSensor.Value = (tempDev?.ConnectionStatus == BluetoothConnectionStatus.Connected) ? "Connected (No BLE)" : "Disconnected"; } catch { statusSensor.Value = "Disconnected"; } finally { batterySensor.Value = 0; }
                        return success;
                    }
                    isNewlyConnected = true;
                    if (_deviceSensors.TryGetValue(deviceId, out var currentSensorsPreCache)) { if (!_deviceSensors.TryUpdate(deviceId, (nameSensor, statusSensor, batterySensor, deviceToUse), currentSensorsPreCache)) { deviceToUse.Dispose(); return success; } currentSensorsPreCache.Device?.Dispose(); } else { deviceToUse.Dispose(); return success; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { statusSensor.Value = "LE Connect Error"; batterySensor.Value = 0; deviceToUse?.Dispose(); if (_deviceSensors.TryGetValue(deviceId, out var check) && check.Device != null) { check.Device.Dispose(); _deviceSensors.TryUpdate(deviceId, (check.Name, check.Status, check.BatteryLevel, null), check); } Console.WriteLine($"GATT Plugin: Error connecting BLE for {deviceName}: {ex.Message} (0x{ex.HResult:X8})"); if (_debugMode) Console.WriteLine(ex.ToString()); return success; }
            }

            if (deviceToUse == null) { statusSensor.Value = "Device Unavailable"; batterySensor.Value = 0; return success; }

            cancellationToken.ThrowIfCancellationRequested();
            GattDeviceService? batteryService = null;
            bool connectionFailedDuringGatt = false;
            string failureReason = "GATT Error";

            try
            {
                var servicesResult = await deviceToUse.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
                if (servicesResult.Status == GattCommunicationStatus.Success && servicesResult.Services.Any())
                {
                    using (batteryService = servicesResult.Services[0])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var characteristicsResult = await batteryService.GetCharacteristicsForUuidAsync(BatteryLevelCharacteristicUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
                        if (characteristicsResult.Status == GattCommunicationStatus.Success && characteristicsResult.Characteristics.Any())
                        {
                            var batteryCharacteristic = characteristicsResult.Characteristics[0];
                            cancellationToken.ThrowIfCancellationRequested();
                            var readResult = await batteryCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
                            if (readResult.Status == GattCommunicationStatus.Success && readResult.Value != null && readResult.Value.Length > 0)
                            {
                                using var reader = DataReader.FromBuffer(readResult.Value); byte batteryLevel = reader.ReadByte(); batterySensor.Value = batteryLevel; statusSensor.Value = $"Connected ({batteryLevel}%)"; if (_debugMode) Console.WriteLine($"GATT Plugin: Success! Bat: {batteryLevel}% for {deviceName}"); success = true; return success;
                            } else { statusSensor.Value = "Read Error"; batterySensor.Value = 0; failureReason = $"Read Fail ({readResult?.Status})"; Console.WriteLine($"GATT Plugin: Read Fail {deviceName} (Stat: {readResult?.Status}, HRes: 0x{readResult?.ProtocolError?.ToString("X8") ?? "N/A"})"); }
                        } else { statusSensor.Value = "Characteristic Error"; batterySensor.Value = 0; failureReason = "No Batt Char"; Console.WriteLine($"GATT Plugin: Char Fail {deviceName} (Stat: {characteristicsResult?.Status}, HRes: 0x{characteristicsResult?.ProtocolError?.ToString("X8") ?? "N/A"})"); }
                    } batteryService = null;
                } else { switch(servicesResult.Status) { case GattCommunicationStatus.Unreachable: statusSensor.Value = "Device Unreachable"; connectionFailedDuringGatt = true; failureReason = "Unreachable (GetSvc)"; break; case GattCommunicationStatus.AccessDenied: statusSensor.Value = "Access Denied"; connectionFailedDuringGatt = true; failureReason = "Access Denied (GetSvc)"; break; case GattCommunicationStatus.ProtocolError when isNewlyConnected: statusSensor.Value = "Protocol Error"; connectionFailedDuringGatt = true; failureReason = "Protocol Error (GetSvc)"; break; default: statusSensor.Value = "Service Error"; failureReason = "No Batt Svc"; break; } batterySensor.Value = 0; Console.WriteLine($"GATT Plugin: Svc Fail {deviceName} (Stat: {servicesResult?.Status}, HRes: 0x{servicesResult?.ProtocolError?.ToString("X8") ?? "N/A"})"); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when ((uint)ex.HResult == 0x80070005) { statusSensor.Value = "Access Denied"; batterySensor.Value = 0; connectionFailedDuringGatt = true; failureReason = "Access Denied (Exception)"; Console.WriteLine($"GATT Plugin: Access Denied {deviceName}. HResult: 0x{ex.HResult:X8}"); if (_debugMode) Console.WriteLine(ex.ToString()); }
            catch (Exception ex) when (IsUnreachableException(ex)) { statusSensor.Value = "Device Unreachable"; batterySensor.Value = 0; connectionFailedDuringGatt = true; failureReason = "Unreachable (Exception)"; Console.WriteLine($"GATT Plugin: Unreachable {deviceName}. HResult: 0x{ex.HResult:X8}. Msg: {ex.Message}"); if (_debugMode) Console.WriteLine(ex.ToString()); }
            catch (Exception ex) { statusSensor.Value = "GATT Error"; batterySensor.Value = 0; connectionFailedDuringGatt = true; failureReason = $"GATT Error ({ex.GetType().Name})"; Console.WriteLine($"GATT Plugin: GATT Error {deviceName}. HResult: 0x{ex.HResult:X8}. Msg: {ex.Message}"); if (_debugMode) Console.WriteLine(ex.ToString()); }
            finally { batteryService?.Dispose(); if (connectionFailedDuringGatt && deviceToUse != null) { if (_debugMode) Console.WriteLine($"GATT Plugin: Disposing BLE cache {deviceName} fail ({failureReason})."); if (_deviceSensors.TryGetValue(deviceId, out var current)) { if (ReferenceEquals(current.Device, deviceToUse)) { _deviceSensors.TryUpdate(deviceId, (current.Name, current.Status, current.BatteryLevel, null), current); } } deviceToUse.Dispose(); } }
            return success;
        }


        #region Helper and Dispose Methods

        private static bool IsUnreachableException(Exception ex)
        {
            uint hr = (uint)ex.HResult;
            return hr == 0x80070490 || hr == 0x8007274c || hr == 0x80000013 || hr == 0x800706BA || hr == 0x800706BE ||
                   ex.Message.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("Device unreachable", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("element not found", StringComparison.OrdinalIgnoreCase);
        }

        private void ResetAllSensorsInternal()
        {
            if (_isDisposed) return;

            // FIX CS8602: Add explicit null check before accessing _deviceSensors
            if (_deviceSensors == null)
            {
                // This should logically never happen due to readonly initialization
                if (_debugMode) Console.WriteLine("Bluetooth GATT Plugin: Warning - _deviceSensors was null during ResetAllSensorsInternal.");
                return;
            }

            int disposeCount = 0;
            // Remove '!' - null check above handles it
            List<BluetoothLEDevice?> devicesToDispose = _deviceSensors.Values.Select(v => v.Device).ToList();

            _deviceSensors.Clear();

            foreach (var device in devicesToDispose)
            {
                try { device?.Dispose(); if (device != null) disposeCount++; }
                catch (Exception ex) { Console.WriteLine($"GATT Plugin: Error disposing device during Reset: {ex.Message}"); if (_debugMode) Console.WriteLine(ex.ToString()); }
            }
             if (_debugMode) Console.WriteLine($"Bluetooth GATT Plugin: Internal Reset: Cleared sensors and disposed {disposeCount} BLE devices.");
        }


        public override void Close()
        {
            if (_debugMode) Console.WriteLine("Bluetooth GATT Plugin: Close() called.");
            Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                if (_debugMode) Console.WriteLine("Bluetooth GATT Plugin: Disposing managed resources...");
                if (_cts != null)
                {
                    if (!_cts.IsCancellationRequested) { try { _cts.Cancel(); } catch(ObjectDisposedException) { /* Ignore */ } }
                    try { _cts.Dispose(); } catch(ObjectDisposedException) { /* Ignore */ }
                    _cts = null;
                }
                 Console.WriteLine("Bluetooth GATT Plugin: Disposing cached devices and clearing sensors.");
                ResetAllSensorsInternal();
            }
            _isDisposed = true;
        }

        // Finalizer removed

        public override void Update()
        {
            throw new NotImplementedException("Use UpdateAsync for Bluetooth GATT Plugin. Synchronous Update is not supported.");
        }

        #endregion // Helper and Dispose Methods
    }
}
#nullable restore