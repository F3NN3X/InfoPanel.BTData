using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile; // Needed for GATT
using Windows.Devices.Enumeration;                     // Needed for DeviceInformation
using Windows.Storage.Streams;                         // Needed for DataReader
using InfoPanel.Plugins;
using IniParser;
using IniParser.Model;
using System.IO;

/*
 * Plugin: InfoPanel.BTData
 * Version: 1.2.1 (GATT Optimized)
 * Author: F3NN3X / Themely.dev
 * Description: Monitors Bluetooth LE devices supporting the standard GATT Battery Service (UUID 0x180F). Caches connections. NOTE: iOS devices (iPhone/iPad) are NOT supported due to restricted BLE access from Windows. Updates periodically (configurable via INI).
 * Changelog:
 *   - v1.2.1 (Apr 03, 2025): Optimized GATT version. Removed background task, caches BluetoothLEDevice objects, improved resource handling and error reporting. Corrected namespace and fixed final warnings.
 *   - v1.2.0 (Apr 01, 2025): Dropped RFCOMM/HFP and iOS support; BLE-only for GATT Battery Service. Fixed UI freeze on reload/disable by making shutdown async and non-blocking. Fixed freeze on re-enable by locking device sensors and sequential monitoring. Fixed CS1996 by moving await outside lock blocks.
 * Note: Requires Bluetooth capability in InfoPanel. iOS devices do not expose Battery Service to Windows PCs, making monitoring infeasible via this method. Will only work for devices implementing standard GATT BAS.
 */

// Corrected Namespace
namespace InfoPanel.BTData
{
    public class BluetoothBatteryPlugin : BasePlugin, IDisposable
    {
        private string? _configFilePath;
        private int _refreshIntervalMinutes = 5;
        private DateTime _lastUpdateAttempt = DateTime.MinValue; // Track last update start time
        private CancellationTokenSource? _cts;

        // Dictionary stores sensors and the associated BLE device object (if connected)
        // Key: UWP DeviceInformation.Id string (from AssociationEndpoint)
        private readonly Dictionary<string, (PluginText Name, PluginText Status, PluginSensor BatteryLevel, BluetoothLEDevice? Device)> _deviceSensors = new();
        private readonly object _sensorLock = new(); // Lock for accessing _deviceSensors

        // Standard GATT Service and Characteristic UUIDs
        private static readonly Guid BatteryServiceUuid = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
        private static readonly Guid BatteryLevelCharacteristicUuid = new Guid("00002a19-0000-1000-8000-00805f9b34fb");

        public BluetoothBatteryPlugin()
            // Set version to 1.2.1
            : base("bluetooth-battery-plugin", "Bluetooth Device Battery (GATT)", "Monitors BLE GATT Battery Service - v1.2.1")
        {
        }

        public override string? ConfigFilePath => _configFilePath;
        // UpdateInterval is used by InfoPanel framework; internal logic uses _lastUpdateAttempt
        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(_refreshIntervalMinutes);

        public override void Initialize()
        {
            try
            {
                _cts = new CancellationTokenSource();
                LoadConfig(); // Load configuration from INI file
                Console.WriteLine("Bluetooth GATT Plugin: Initialized.");
                // Device loading happens in Load method called by InfoPanel
            }
            catch (Exception ex)
            {
                Console.WriteLine("Bluetooth GATT Plugin: Initialize failed: {0}", ex.Message);
                throw; // Propagate exception to InfoPanel
            }
        }

        // Loads configuration settings from the INI file.
        private void LoadConfig()
        {
            string? configSectionName = this.Name; // Use plugin name if available
            if (string.IsNullOrEmpty(configSectionName)) {
                configSectionName = "Bluetooth Device Battery (GATT)"; // Fallback section name
                Console.WriteLine($"Bluetooth GATT Plugin: Warning - Plugin Name empty, using default section '{configSectionName}'.");
            }
            try {
                string baseDirectory = AppContext.BaseDirectory; string? assemblyDirectory = null;
                try { assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { /* Ignore errors getting assembly path */ }
                string effectiveDirectory = (!string.IsNullOrEmpty(assemblyDirectory) && Directory.Exists(assemblyDirectory)) ? assemblyDirectory : baseDirectory;
                if (string.IsNullOrEmpty(effectiveDirectory)) { Console.WriteLine($"Bluetooth GATT Plugin: Error - Cannot determine config directory. Using defaults."); _refreshIntervalMinutes = 5; _configFilePath = null; return; }
                // Use plugin ID for unique INI filename
                string potentialConfigPath = Path.Combine(effectiveDirectory, $"{this.Id}.ini");
                if (!string.IsNullOrEmpty(potentialConfigPath)) { _configFilePath = potentialConfigPath; Console.WriteLine($"Bluetooth GATT Plugin: Config file path: {_configFilePath}"); }
                else { Console.WriteLine($"Bluetooth GATT Plugin: Error - Path.Combine failed. Using defaults."); _refreshIntervalMinutes = 5; _configFilePath = null; return; }
                if (string.IsNullOrEmpty(_configFilePath)) { Console.WriteLine("Bluetooth GATT Plugin: Config path null/empty. Using defaults."); _refreshIntervalMinutes = 5; return; }
                var parser = new FileIniDataParser(); IniData config;
                if (!File.Exists(_configFilePath)) {
                    config = new IniData(); config[configSectionName]["RefreshIntervalMinutes"] = "5"; // Default value
                    try { parser.WriteFile(_configFilePath, config); Console.WriteLine($"Bluetooth GATT Plugin: Config file created at {_configFilePath}"); }
                    catch (Exception ex) { Console.WriteLine($"Bluetooth GATT Plugin: Error writing default config {_configFilePath}: {ex.Message}"); }
                    _refreshIntervalMinutes = 5;
                } else {
                    try {
                        config = parser.ReadFile(_configFilePath);
                        // Check INI value, handle null/empty/invalid before TryParse
                        string? refreshValue = config[configSectionName]?["RefreshIntervalMinutes"];
                        if (string.IsNullOrEmpty(refreshValue) || !int.TryParse(refreshValue, out _refreshIntervalMinutes) || _refreshIntervalMinutes <= 0) {
                            Console.WriteLine($"Bluetooth GATT Plugin: Invalid/missing RefreshIntervalMinutes in section '{configSectionName}', using default 5.");
                             _refreshIntervalMinutes = 5; // Apply default
                        }
                    } catch (Exception ex) { Console.WriteLine($"Bluetooth GATT Plugin: Error reading config {_configFilePath}: {ex.Message}. Using default 5."); _refreshIntervalMinutes = 5; }
                }
                Console.WriteLine("Bluetooth GATT Plugin: Refresh interval set to {0} minutes", _refreshIntervalMinutes);
            } catch (Exception ex) { Console.WriteLine($"Bluetooth GATT Plugin: Unexpected error loading config: {ex.ToString()}. Using default 5."); _refreshIntervalMinutes = 5; }
        }

        // Gets basic info (ID, Name) about paired Bluetooth devices using UWP DeviceInformation.
        private async Task<List<(string Id, string Name)>> GetPairedDevicesAsync(CancellationToken cancellationToken)
        {
            var result = new List<(string Id, string Name)>();
            try {
                // Find paired AssociationEndpoint devices
                var devicesInfo = await DeviceInformation.FindAllAsync(
                    BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                    new[] { "System.ItemNameDisplay" },
                    DeviceInformationKind.AssociationEndpoint);

                // Console.WriteLine($"Bluetooth GATT Plugin: Found {devicesInfo.Count} paired AEP devices."); // Optional verbose log
                foreach (var deviceInfo in devicesInfo) {
                    if (cancellationToken.IsCancellationRequested) break;
                    string name = deviceInfo.Name; // Use DeviceInformation.Name as fallback
                    // Prefer System.ItemNameDisplay if available
                    if(deviceInfo.Properties.TryGetValue("System.ItemNameDisplay", out object nameObj) && nameObj is string nameStr && !string.IsNullOrEmpty(nameStr)) name = nameStr;
                    // Add if we have a valid ID and Name
                    if (!string.IsNullOrEmpty(deviceInfo.Id) && !string.IsNullOrEmpty(name)) result.Add((deviceInfo.Id, name));
                    // else Console.WriteLine($"Bluetooth GATT Plugin: Skipping device with missing ID or Name (ID: {deviceInfo.Id})"); // Optional verbose log
                }
            } catch (Exception ex) { Console.WriteLine("Bluetooth GATT Plugin: Error detecting paired devices: {0}", ex.Message); }
            return result;
        }

        // Populates initial containers based on paired devices found via UWP enumeration.
        public override void Load(List<IPluginContainer> containers)
        {
            Console.WriteLine("Bluetooth GATT Plugin: Loading devices...");
            var pairedDevices = GetPairedDevicesAsync(CancellationToken.None).GetAwaiter().GetResult(); // Sync call needed for Load
            Console.WriteLine($"Bluetooth GATT Plugin: Found {pairedDevices.Count} paired devices during Load.");
            lock (_sensorLock) {
                ResetAllSensorsInternal(); // Clear previous state and dispose devices

                if (pairedDevices.Any()) {
                    foreach (var (id, name) in pairedDevices) {
                        // Use Device ID hash for sensor uniqueness
                        int idHash = id.GetHashCode();
                        var nameSensor = new PluginText($"name_{idHash}", "Name", name);
                        var statusSensor = new PluginText($"status_{idHash}", "Status", "Unknown"); // Initial status
                        var batterySensor = new PluginSensor($"battery_{idHash}", "Battery Level", 0, "%"); // Initial value

                        // Store sensors with null device initially
                        _deviceSensors[id] = (nameSensor, statusSensor, batterySensor, null);

                        // Create UI container
                        var container = new PluginContainer($"BT - {name}"); // Prefix for clarity
                        container.Entries.Add(nameSensor);
                        container.Entries.Add(statusSensor);
                        container.Entries.Add(batterySensor);
                        containers.Add(container);
                        // Console.WriteLine($"Bluetooth GATT Plugin: Added container for '{name}' (ID: {id})"); // Optional verbose log
                    }
                } else {
                    // Add a placeholder if no devices found
                    var container = new PluginContainer("Bluetooth Devices");
                    container.Entries.Add(new PluginText("status_none", "Status", "No paired devices found"));
                    containers.Add(container);
                    Console.WriteLine("Bluetooth GATT Plugin: No paired Bluetooth devices found during Load.");
                }
            }
            Console.WriteLine("Bluetooth GATT Plugin: Load complete.");
        }

        // Periodically attempts to connect and read GATT battery level for known devices.
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            // Link internal CTS with the one passed by InfoPanel
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None, cancellationToken);
            var combinedToken = linkedCts.Token;
            if (combinedToken.IsCancellationRequested) return;

            // Check update interval based on last successful/attempted update
            if ((DateTime.UtcNow - _lastUpdateAttempt) < UpdateInterval) return; // Not time yet
            _lastUpdateAttempt = DateTime.UtcNow; // Record the time we started this update attempt

            // Console.WriteLine("Bluetooth GATT Plugin: Starting UpdateAsync cycle."); // Optional verbose log
            List<string> deviceIdsToUpdate;
            lock (_sensorLock) { deviceIdsToUpdate = _deviceSensors.Keys.ToList(); } // Snapshot of keys

            if (!deviceIdsToUpdate.Any()) { /* Console.WriteLine("Bluetooth GATT Plugin: No devices loaded to update."); */ return; } // Optional verbose log

            // Process each known device sequentially
            foreach (var deviceId in deviceIdsToUpdate) {
                if (combinedToken.IsCancellationRequested) break;
                await UpdateDeviceGattDataAsync(deviceId, combinedToken);
            }

            // if (!combinedToken.IsCancellationRequested) Console.WriteLine("Bluetooth GATT Plugin: UpdateAsync cycle finished."); // Optional verbose log
            // else Console.WriteLine("Bluetooth GATT Plugin: UpdateAsync cancelled."); // Optional verbose log
        }

        // Attempts connection and GATT read for a specific device ID (AEP ID).
        // Caches the BluetoothLEDevice object. Uses Bluetooth Address for LE connection.
        private async Task UpdateDeviceGattDataAsync(string deviceId, CancellationToken cancellationToken)
        {
            PluginText? nameSensor, statusSensor; PluginSensor? batterySensor;
            BluetoothLEDevice? cachedBleDevice;
            string deviceName = "Unknown"; bool deviceFound = false;

            // Safely get current state from dictionary
            lock (_sensorLock) {
                if (_deviceSensors.TryGetValue(deviceId, out var sensors)) {
                    (nameSensor, statusSensor, batterySensor, cachedBleDevice) = sensors;
                    deviceName = nameSensor.Value; deviceFound = true;
                } else { return; } // Device was removed from tracking
            }
            // Exit if sensors somehow null after retrieval (shouldn't happen)
            if (!deviceFound || nameSensor == null || statusSensor == null || batterySensor == null) return;

            // Check if cached device exists and is connected
            bool needsConnection = cachedBleDevice == null || cachedBleDevice.ConnectionStatus != BluetoothConnectionStatus.Connected;
            BluetoothLEDevice? deviceToUse = cachedBleDevice; // Start with the cached device

            if (needsConnection) {
                // Dispose old cached object if it exists but is disconnected
                if (cachedBleDevice != null) {
                    cachedBleDevice.Dispose(); deviceToUse = null;
                    // Update dictionary to reflect disposed device
                    lock (_sensorLock) { if (_deviceSensors.TryGetValue(deviceId, out var s)) _deviceSensors[deviceId] = (s.Name, s.Status, s.BatteryLevel, null); }
                }

                // Attempt to connect/reconnect
                ulong bluetoothAddress = 0;
                // 1. Get address from AEP ID
                try {
                    using var classicDevice = await BluetoothDevice.FromIdAsync(deviceId);
                    if (classicDevice != null) bluetoothAddress = classicDevice.BluetoothAddress;
                } catch (Exception ex) { Console.WriteLine($"GATT Plugin: Error get BTDevice for {deviceName}: {ex.Message}"); statusSensor.Value = "Device Error"; batterySensor.Value = 0; return; }
                // Exit if address couldn't be found
                if (bluetoothAddress == 0) { statusSensor.Value = "Address Error"; batterySensor.Value = 0; return; }

                // 2. Get LE Device from address
                try {
                    // Console.WriteLine($"Bluetooth GATT Plugin: Attempting get/connect BLE for {deviceName} Addr {bluetoothAddress:X12}..."); // Optional verbose log
                    deviceToUse = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
                    if (deviceToUse == null) {
                        // Failed to get LE object (non-LE, iOS restriction, etc.)
                         bool isConnectedClassic = false; try { using var tempDevice = await BluetoothDevice.FromIdAsync(deviceId); isConnectedClassic = tempDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected; } catch {}
                         statusSensor.Value = isConnectedClassic ? "Connected (No LE/GATT)" : "Disconnected"; batterySensor.Value = 0;
                         // Ensure cache is null
                         lock (_sensorLock) { if (_deviceSensors.TryGetValue(deviceId, out var s)) _deviceSensors[deviceId] = (s.Name, s.Status, s.BatteryLevel, null); }
                         return; // Stop trying GATT for this device this cycle
                    }
                    // Cache the new device object
                    lock (_sensorLock) {
                        if (_deviceSensors.TryGetValue(deviceId, out var s)) {
                            s.Device?.Dispose(); // Dispose previous if any race condition
                            _deviceSensors[deviceId] = (s.Name, s.Status, s.BatteryLevel, deviceToUse);
                        } else { deviceToUse?.Dispose(); return; } // Sensor removed while connecting
                    }
                } catch (Exception ex) { Console.WriteLine($"GATT Plugin: Error get/connect BLE for {deviceName}: {ex.Message}"); statusSensor.Value = "LE Connect Error"; batterySensor.Value = 0; deviceToUse?.Dispose(); lock (_sensorLock) { if (_deviceSensors.TryGetValue(deviceId, out var s)) { s.Device?.Dispose(); _deviceSensors[deviceId] = (s.Name, s.Status, s.BatteryLevel, null); } } return; }
            }

            // If we still don't have a device object, something went wrong
            if (deviceToUse == null) {
                 if (!statusSensor.Value.Contains("Error") && !statusSensor.Value.Contains("No LE")) statusSensor.Value = "Internal Error";
                 batterySensor.Value = 0; return;
            }

            // Attempt GATT Read (Implicit Connection might happen)
            GattDeviceService? batteryService = null;
            bool connectionFailedDuringGatt = false;
            try {
                // Console.WriteLine($"Bluetooth GATT Plugin: Querying GATT services for {deviceName}..."); // Optional verbose log
                var servicesResult = await deviceToUse.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Uncached);
                if (cancellationToken.IsCancellationRequested) return;

                // Check status after attempting to get services
                if (servicesResult.Status != GattCommunicationStatus.Success && servicesResult.Status != GattCommunicationStatus.ProtocolError) {
                    if(servicesResult.Status == GattCommunicationStatus.Unreachable) { connectionFailedDuringGatt = true; }
                    else { Console.WriteLine($"GATT Plugin: GetSvc status {deviceName}: {servicesResult.Status}."); } // Log other errors
                }

                // Proceed if connection seems okay and service was found
                if (!connectionFailedDuringGatt && servicesResult.Status == GattCommunicationStatus.Success && servicesResult.Services.Any()) {
                    using (batteryService = servicesResult.Services[0]) { // Use using to ensure disposal
                        var characteristicsResult = await batteryService.GetCharacteristicsForUuidAsync(BatteryLevelCharacteristicUuid, BluetoothCacheMode.Uncached);
                        if (cancellationToken.IsCancellationRequested) return;

                        if (characteristicsResult.Status == GattCommunicationStatus.Success && characteristicsResult.Characteristics.Any()) {
                            var batteryCharacteristic = characteristicsResult.Characteristics[0];
                            var readResult = await batteryCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                            if (cancellationToken.IsCancellationRequested) return;

                            if (readResult.Status == GattCommunicationStatus.Success && readResult.Value.Length > 0) {
                                using var reader = DataReader.FromBuffer(readResult.Value); byte batteryLevel = reader.ReadByte();
                                batterySensor.Value = batteryLevel; statusSensor.Value = $"Connected ({batteryLevel}%)";
                                Console.WriteLine($"GATT Plugin: Success! Battery for {deviceName}: {batteryLevel}%"); // Keep success log
                            } else { Console.WriteLine($"GATT Plugin: Failed read Level for {deviceName} (Status: {readResult?.Status})"); statusSensor.Value = "Connected (Read Fail)"; batterySensor.Value = 0; }
                        } else { Console.WriteLine($"GATT Plugin: Level char not found for {deviceName} (Status: {characteristicsResult?.Status})"); statusSensor.Value = "Connected (No Batt Char)"; batterySensor.Value = 0; }
                    } // batteryService disposed here
                    batteryService = null; // Clear reference after using block
                } else if (!connectionFailedDuringGatt) { // Service not found, but connection was okay
                     if(servicesResult.Status != GattCommunicationStatus.Unreachable) { /* Console.WriteLine($"GATT Plugin: Batt Service not found {deviceName} (Status: {servicesResult?.Status})"); */ } // Less verbose log
                     if(servicesResult.Status == GattCommunicationStatus.Success || servicesResult.Status == GattCommunicationStatus.ProtocolError) { statusSensor.Value = "Connected (No Batt Svc)"; } else if (!statusSensor.Value.StartsWith("Connected")) { statusSensor.Value = $"Error ({servicesResult.Status})"; }
                     batterySensor.Value = 0;
                }
            }
            // Handle specific exceptions
            catch (Exception ex) when ((uint)ex.HResult == 0x80070005) { Console.WriteLine($"GATT Plugin: Access Denied {deviceName}. Check manifest/capabilities."); statusSensor.Value = "Access Denied"; batterySensor.Value = 0; connectionFailedDuringGatt = true; }
            catch (Exception ex) when ((uint)ex.HResult == 0x80070490 || (uint)ex.HResult == 0x8007274c || ex.Message.Contains("unreachable", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"GATT Plugin: Unreachable {deviceName}: {ex.Message}"); statusSensor.Value = "Device Unreachable"; batterySensor.Value = 0; connectionFailedDuringGatt = true; }
            catch (Exception ex) { Console.WriteLine($"GATT Plugin: Generic GATT Error {deviceName}: {ex.Message}"); statusSensor.Value = "GATT Error"; batterySensor.Value = 0; connectionFailedDuringGatt = true; } // Log only message for generic errors unless debugging
            finally {
                batteryService?.Dispose(); // Ensure service is disposed if using block fails
                if (connectionFailedDuringGatt && deviceToUse != null) {
                    Console.WriteLine($"GATT Plugin: Disposing BLE cache for {deviceName} due to GATT failure."); deviceToUse.Dispose();
                    lock (_sensorLock) { if (_deviceSensors.TryGetValue(deviceId, out var s)) _deviceSensors[deviceId] = (s.Name, s.Status, s.BatteryLevel, null); }
                    // Ensure status reflects the error
                    if (!statusSensor.Value.Contains("Error") && !statusSensor.Value.Contains("Unreachable") && !statusSensor.Value.Contains("Denied")) statusSensor.Value = "Disconnected";
                } else if (connectionFailedDuringGatt) { // Device was already null or disposed but connection failed
                     statusSensor.Value = statusSensor.Value.Contains("Error") || statusSensor.Value.Contains("Unreachable") ? statusSensor.Value : "Disconnected"; batterySensor.Value = 0;
                }
            }
        }

        // Resets sensors and disposes associated BluetoothLEDevice objects.
        // Assumes lock is held externally or called safely.
        private void ResetAllSensorsInternal() {
            // Console.WriteLine("Bluetooth GATT Plugin: Internal Reset: Disposing existing BLE devices..."); // Less verbose
            var devicesToDispose = _deviceSensors.Values.Select(v => v.Device).Where(d => d != null).ToList();
            _deviceSensors.Clear(); int disposeCount = 0;
            foreach(var device in devicesToDispose) { try { device?.Dispose(); disposeCount++; } catch (Exception ex) { Console.WriteLine($"GATT Plugin: Error disposing device during Reset: {ex.Message}"); } }
            // Console.WriteLine($"Bluetooth GATT Plugin: Internal Reset: Cleared sensors and disposed {disposeCount} BLE devices."); // Less verbose
        }

        // --- Standard Dispose Pattern ---
        public override void Close() { Console.WriteLine("Bluetooth GATT Plugin: Close called"); DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult(); }
        private async Task DisposeAsync() {
            // Console.WriteLine("Bluetooth GATT Plugin: Starting async dispose"); // Less verbose
            if (_cts != null) { if (!_cts.IsCancellationRequested) { _cts.Cancel(); } _cts.Dispose(); _cts = null; }
            lock(_sensorLock) { ResetAllSensorsInternal(); } // Call internal version under lock
            // Console.WriteLine("Bluetooth GATT Plugin: Async dispose finished."); // Less verbose
            await Task.Yield();
        }
        public void Dispose() { Console.WriteLine("Bluetooth GATT Plugin: Dispose called"); DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult(); GC.SuppressFinalize(this); }
        ~BluetoothBatteryPlugin() { Dispose(); } // Finalizer as safety net
        public override void Update() => throw new NotImplementedException("Use UpdateAsync for Bluetooth GATT Plugin.");

    } // End Class
} // End Namespace