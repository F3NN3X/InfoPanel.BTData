using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using InfoPanel.Plugins;
using System.IO;
using IniParser;
using IniParser.Model;
using System.Reflection;
using System.Text;

/*
 * Plugin: InfoPanel.BTData
 * Version: 1.4.0 (Debug Version)
 * Author: F3NN3X / Themely.dev
 * Description: Simple plugin to detect Bluetooth devices and their battery status
 */

namespace InfoPanel.BTData
{
    public class BluetoothBatteryPlugin : BasePlugin
    {
        private string? _configFilePath;
        private int _refreshIntervalMinutes = 5;
        private bool _debugMode = true; // Always debug for now
        private DateTime _lastUpdateTime = DateTime.MinValue;
        private string _logPath;
        private object _logLock = new object();
        
        private Dictionary<string, (string Name, BluetoothConnectionStatus Status, int BatteryLevel)> _btDevices = 
            new Dictionary<string, (string, BluetoothConnectionStatus, int)>();
        
        private List<IPluginContainer> _containers = new List<IPluginContainer>();
        private Dictionary<string, PluginContainer> _deviceContainers = new Dictionary<string, PluginContainer>();

        // Standard GATT UUIDs for battery service
        private static readonly Guid BatteryServiceUuid = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
        private static readonly Guid BatteryLevelCharacteristicUuid = new Guid("00002a19-0000-1000-8000-00805f9b34fb");

        public BluetoothBatteryPlugin() 
            : base("bluetooth-battery-plugin", "Bluetooth Devices", "Shows connected Bluetooth devices - v1.4.0")
        {
            // Create log path right away
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel", "BTData");
            if (!Directory.Exists(logDir))
            {
                try { Directory.CreateDirectory(logDir); } catch { /* Ignore if failed */ }
            }
            _logPath = Path.Combine(logDir, "bluetooth-plugin-debug.log");
            LogDebug("Plugin constructed");
        }

        public override string? ConfigFilePath => _configFilePath;
        
        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(_refreshIntervalMinutes);

        public override void Initialize()
        {
            try
            {
                LogDebug("Initializing plugin...");
                
                // Set config path
                var execDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
                _configFilePath = Path.Combine(execDir, $"{this.Id}.ini");
                
                LogDebug($"Config path: {_configFilePath}");
                
                // Create default config if needed
                if (!File.Exists(_configFilePath))
                {
                    try
                    {
                        var parser = new FileIniDataParser();
                        var config = new IniData();
                        config["Settings"]["RefreshIntervalMinutes"] = "5";
                        config["Settings"]["Debug"] = "true";
                        parser.WriteFile(_configFilePath, config);
                        LogDebug("Created default config");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Error creating config: {ex.Message}");
                    }
                }
                
                // Load config
                try
                {
                    var parser = new FileIniDataParser();
                    var config = parser.ReadFile(_configFilePath);
                    
                    string refreshValue = config["Settings"]["RefreshIntervalMinutes"];
                    if (int.TryParse(refreshValue, out int minutes) && minutes > 0)
                    {
                        _refreshIntervalMinutes = minutes;
                    }
                    
                    string debugValue = config["Settings"]["Debug"];
                    if (bool.TryParse(debugValue, out bool debug))
                    {
                        _debugMode = debug;
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"Error loading config: {ex.Message}");
                }
                
                LogDebug($"Initialized with refresh={_refreshIntervalMinutes}min, debug={_debugMode}");
            }
            catch (Exception ex)
            {
                LogDebug($"Error in Initialize: {ex}");
            }
        }
        
        public override void Load(List<IPluginContainer> containers)
        {
            try
            {
                LogDebug("Load called - setting up containers");
                _containers = containers;
                
                // Create main container
                var mainContainer = new PluginContainer("Bluetooth Devices");
                mainContainer.Entries.Add(new PluginText("bt_status", "Status", "Searching for devices..."));
                containers.Add(mainContainer);
                
                // Store it in our dictionary for easier lookup later
                _deviceContainers["main"] = mainContainer;
                
                LogDebug("Initial container added");
                
                // Perform a synchronous scan to find devices immediately (this is important for InfoPanel)
                LogDebug("Performing synchronous device scan at load time");
                var scanTask = ScanForDevices();
                scanTask.Wait(); // Wait for the scan to complete
                
                // Now create containers for each discovered device
                foreach (var device in _btDevices)
                {
                    try
                    {
                        string deviceId = device.Key;
                        string containerId = $"bt-device-{deviceId.GetHashCode()}";
                        string containerName = $"BT - {device.Value.Name}";
                        
                        // Create container for this device
                        var deviceContainer = new PluginContainer(containerName);
                        
                        // Add entries
                        string nameId = $"name_{deviceId.GetHashCode()}";
                        string statusId = $"status_{deviceId.GetHashCode()}";
                        string batteryId = $"battery_{deviceId.GetHashCode()}";
                        
                        // Status text
                        string statusText = device.Value.Status == BluetoothConnectionStatus.Connected ? "Connected" : "Disconnected";
                        if (device.Value.Status == BluetoothConnectionStatus.Connected && device.Value.BatteryLevel >= 0)
                        {
                            statusText = $"Connected ({device.Value.BatteryLevel}%)";
                        }
                        
                        // Create entries
                        var nameEntry = new PluginText(nameId, "Name", device.Value.Name);
                        var statusEntry = new PluginText(statusId, "Status", statusText);
                        var batteryEntry = new PluginSensor(batteryId, "Battery Level", 
                            device.Value.BatteryLevel >= 0 ? device.Value.BatteryLevel : 0, "%");
                        
                        // Add entries to container
                        deviceContainer.Entries.Add(nameEntry);
                        deviceContainer.Entries.Add(statusEntry);
                        deviceContainer.Entries.Add(batteryEntry);
                        
                        // Add container to the list
                        containers.Add(deviceContainer);
                        
                        // Add to our dictionary
                        _deviceContainers[containerId] = deviceContainer;
                        
                        LogDebug($"Added device container for {device.Value.Name} during load");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Error creating container for device {device.Value.Name}: {ex.Message}");
                    }
                }
                
                // Update the status in the main container
                if (_btDevices.Count > 0)
                {
                    int connected = _btDevices.Count(d => d.Value.Status == BluetoothConnectionStatus.Connected);
                    var statusEntry = mainContainer.Entries.FirstOrDefault(e => e.Id == "bt_status") as PluginText;
                    if (statusEntry != null)
                    {
                        statusEntry.Value = $"Found {_btDevices.Count} devices, {connected} connected";
                    }
                }
                
                LogDebug($"Load complete. Added {_btDevices.Count} device containers");
            }
            catch (Exception ex)
            {
                LogDebug($"Error in Load: {ex}");
            }
        }

        public override void Update()
        {
            // Not used - we use UpdateAsync instead
        }
        
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            try
            {
                LogDebug("UpdateAsync called");
                
                // Check if it's time to update
                if (DateTime.Now - _lastUpdateTime < UpdateInterval)
                {
                    LogDebug("Skipping update - not time yet");
                    return;
                }
                
                _lastUpdateTime = DateTime.Now;
                await ScanForDevices();
                
                // Update UI based on scan results
                UpdateUI();
                
                LogDebug("UpdateAsync completed");
            }
            catch (Exception ex) 
            {
                LogDebug($"Error in UpdateAsync: {ex}");
            }
        }
        
        private async Task ScanForDevices()
        {
            try
            {
                LogDebug("ScanForDevices started");
                
                // Clear current device list
                _btDevices.Clear();
                
                // Try different methods to find Bluetooth devices
                await ScanForBluetoothDevices();
                
                // Log summary
                LogDebug($"Scan complete. Found {_btDevices.Count} valid devices");
                foreach (var dev in _btDevices)
                {
                    LogDebug($"  {dev.Value.Name}: {dev.Value.Status}, Battery: {dev.Value.BatteryLevel}%");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error in ScanForDevices: {ex}");
            }
        }
        
        private async Task ScanForBluetoothDevices()
        {
            // Method 1: Try to find all Bluetooth devices (simplest selector)
            try
            {
                LogDebug("Method 1: Looking for all Bluetooth devices");
                string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
                
                LogDebug($"Method 1: Found {devices.Count} devices");
                
                foreach (var device in devices)
                {
                    try
                    {
                        // Skip devices with no name
                        if (string.IsNullOrEmpty(device.Name)) continue;
                        
                        LogDebug($"Method 1: Processing device: {device.Name}, ID: {device.Id}");
                        
                        // Add to device list 
                        string deviceId = device.Id;
                        string name = device.Name;
                        
                        // Try to connect to get battery info
                        int batteryLevel = await GetBatteryLevelFromDeviceId(deviceId, name);
                        
                        _btDevices[deviceId] = (
                            name,
                            BluetoothConnectionStatus.Connected,  // Assume connected since it was found
                            batteryLevel
                        );
                        
                        LogDebug($"Method 1: Added device {name} with battery {batteryLevel}%");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Method 1: Error processing device {device.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Method 1 failed: {ex.Message}");
            }
            
            // Method 2: Use BluetoothLEDevice selector
            if (_btDevices.Count == 0)
            {
                try
                {
                    LogDebug("Method 2: Looking for Bluetooth LE devices");
                    string leSelector = BluetoothLEDevice.GetDeviceSelector();
                    DeviceInformationCollection leDevices = await DeviceInformation.FindAllAsync(leSelector);
                    
                    LogDebug($"Method 2: Found {leDevices.Count} LE devices");
                    
                    foreach (var device in leDevices)
                    {
                        try
                        {
                            // Skip devices with no name
                            if (string.IsNullOrEmpty(device.Name)) continue;
                            
                            LogDebug($"Method 2: Processing device: {device.Name}, ID: {device.Id}");
                            
                            // Add to device list 
                            string deviceId = device.Id;
                            string name = device.Name;
                            
                            // Try to connect to get battery info
                            int batteryLevel = await GetBatteryLevelFromDeviceId(deviceId, name);
                            
                            _btDevices[deviceId] = (
                                name,
                                BluetoothConnectionStatus.Connected,  // Assume connected since it was found
                                batteryLevel
                            );
                            
                            LogDebug($"Method 2: Added device {name} with battery {batteryLevel}%");
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Method 2: Error processing device {device.Id}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"Method 2 failed: {ex.Message}");
                }
            }
            
            // Method 3: Use direct selector for all devices
            if (_btDevices.Count == 0)
            {
                try
                {
                    LogDebug("Method 3: Looking for any device with Bluetooth capabilities");
                    string selector = "System.Devices.DevObjectType:=5"; // Type 5 is Bluetooth devices
                    DeviceInformationCollection allDevices = await DeviceInformation.FindAllAsync(selector);
                    
                    LogDebug($"Method 3: Found {allDevices.Count} possible Bluetooth devices");
                    
                    foreach (var device in allDevices)
                    {
                        try
                        {
                            // Skip devices with no name
                            if (string.IsNullOrEmpty(device.Name)) continue;
                            
                            LogDebug($"Method 3: Processing device: {device.Name}, ID: {device.Id}");
                            
                            // Log all properties
                            if (device.Properties.Count > 0)
                            {
                                LogDebug($"Method 3: Device properties for {device.Name}:");
                                foreach (var prop in device.Properties)
                                {
                                    LogDebug($"  {prop.Key}: {prop.Value}");
                                }
                            }
                            
                            // Add to device list 
                            string deviceId = device.Id;
                            string name = device.Name;
                            
                            // Try to connect to get battery info
                            int batteryLevel = await GetBatteryLevelFromDeviceId(deviceId, name);
                            
                            _btDevices[deviceId] = (
                                name,
                                BluetoothConnectionStatus.Connected,  // Assume connected since it was found
                                batteryLevel
                            );
                            
                            LogDebug($"Method 3: Added device {name} with battery {batteryLevel}%");
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Method 3: Error processing device {device.Id}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"Method 3 failed: {ex.Message}");
                }
            }
        }
        
        private async Task<int> GetBatteryLevelFromAddress(string address)
        {
            try
            {
                LogDebug($"Getting battery level for device with address {address}");
                
                // Convert address string to ulong
                if (!ulong.TryParse(address, System.Globalization.NumberStyles.HexNumber, null, out ulong bluetoothAddress))
                {
                    LogDebug($"Invalid address format: {address}");
                    return -1;
                }
                
                LogDebug($"Connecting to device with address: {bluetoothAddress:X}");
                
                // Try to connect to the device
                using (var bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress))
                {
                    if (bleDevice == null)
                    {
                        LogDebug("FromBluetoothAddressAsync returned null");
                        return -1;
                    }
                    
                    LogDebug($"Connected to device: {bleDevice.Name}, Status: {bleDevice.ConnectionStatus}");
                    
                    // Check if device is connected
                    if (bleDevice.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    {
                        LogDebug("Device not connected");
                        return -1;
                    }
                    
                    // Get GATT services
                    var servicesResult = await bleDevice.GetGattServicesForUuidAsync(BatteryServiceUuid);
                    
                    if (servicesResult.Status != GattCommunicationStatus.Success)
                    {
                        LogDebug($"GetGattServicesForUuidAsync failed: {servicesResult.Status}");
                        return -1;
                    }
                    
                    if (servicesResult.Services.Count == 0)
                    {
                        LogDebug("No battery service found");
                        return -1;
                    }
                    
                    LogDebug($"Found {servicesResult.Services.Count} battery services");
                    
                    // Get battery level characteristic
                    using (var batteryService = servicesResult.Services[0])
                    {
                        var characteristicsResult = await batteryService.GetCharacteristicsForUuidAsync(BatteryLevelCharacteristicUuid);
                        
                        if (characteristicsResult.Status != GattCommunicationStatus.Success)
                        {
                            LogDebug($"GetCharacteristicsForUuidAsync failed: {characteristicsResult.Status}");
                            return -1;
                        }
                        
                        if (characteristicsResult.Characteristics.Count == 0)
                        {
                            LogDebug("No battery level characteristic found");
                            return -1;
                        }
                        
                        LogDebug("Found battery level characteristic");
                        
                        // Read battery level
                        var batteryChar = characteristicsResult.Characteristics[0];
                        var readResult = await batteryChar.ReadValueAsync();
                        
                        if (readResult.Status != GattCommunicationStatus.Success)
                        {
                            LogDebug($"ReadValueAsync failed: {readResult.Status}");
                            return -1;
                        }
                        
                        if (readResult.Value == null || readResult.Value.Length == 0)
                        {
                            LogDebug("Empty battery data");
                            return -1;
                        }
                        
                        // Extract battery level
                        using (var reader = DataReader.FromBuffer(readResult.Value))
                        {
                            byte value = reader.ReadByte();
                            LogDebug($"Read battery level: {value}%");
                            return value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error getting battery level: {ex}");
                return -1;
            }
        }
        
        private async Task<int> GetBatteryLevelFromDeviceId(string deviceId, string deviceName)
        {
            try
            {
                LogDebug($"Getting battery level for device {deviceName} with ID {deviceId}");
                
                // First try to connect as a Bluetooth LE device
                using (var bleDevice = await BluetoothLEDevice.FromIdAsync(deviceId))
                {
                    if (bleDevice == null)
                    {
                        LogDebug($"Failed to connect to {deviceName} as a Bluetooth LE device");
                        return -1;
                    }
                    
                    LogDebug($"Connected to {deviceName} as LE device with status: {bleDevice.ConnectionStatus}");
                    
                    // Try to get the battery service
                    var servicesResult = await bleDevice.GetGattServicesForUuidAsync(BatteryServiceUuid);
                    
                    if (servicesResult.Status != GattCommunicationStatus.Success)
                    {
                        LogDebug($"GetGattServicesForUuidAsync failed: {servicesResult.Status}");
                        return -1;
                    }
                    
                    if (servicesResult.Services.Count == 0)
                    {
                        LogDebug($"No battery service found for {deviceName}");
                        return -1;
                    }
                    
                    // Found battery service
                    LogDebug($"Found battery service for {deviceName}");
                    
                    // Try to get battery level characteristic
                    using (var batteryService = servicesResult.Services[0])
                    {
                        var characteristicsResult = await batteryService.GetCharacteristicsForUuidAsync(BatteryLevelCharacteristicUuid);
                        
                        if (characteristicsResult.Status != GattCommunicationStatus.Success)
                        {
                            LogDebug($"GetCharacteristicsForUuidAsync failed: {characteristicsResult.Status}");
                            return -1;
                        }
                        
                        if (characteristicsResult.Characteristics.Count == 0)
                        {
                            LogDebug("No battery level characteristic found");
                            return -1;
                        }
                        
                        // Found battery characteristic
                        LogDebug("Found battery level characteristic");
                        
                        // Try to read battery level
                        var batteryChar = characteristicsResult.Characteristics[0];
                        var readResult = await batteryChar.ReadValueAsync();
                        
                        if (readResult.Status != GattCommunicationStatus.Success)
                        {
                            LogDebug($"ReadValueAsync failed: {readResult.Status}");
                            return -1;
                        }
                        
                        // Got battery data
                        if (readResult.Value == null || readResult.Value.Length == 0)
                        {
                            LogDebug("Empty battery data");
                            return -1;
                        }
                        
                        // Extract battery level from first byte
                        using (var reader = DataReader.FromBuffer(readResult.Value))
                        {
                            byte value = reader.ReadByte();
                            LogDebug($"Successfully read battery level for {deviceName}: {value}%");
                            return value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error getting battery level for {deviceName}: {ex.Message}");
                
                // Try the regular Bluetooth device approach as fallback
                try
                {
                    using (var device = await BluetoothDevice.FromIdAsync(deviceId))
                    {
                        if (device == null)
                        {
                            LogDebug($"Failed to connect to {deviceName} as a regular Bluetooth device");
                            return -1;
                        }
                        
                        LogDebug($"Connected to {deviceName} as classic device with address: {device.BluetoothAddress:X}");
                        
                        // Try to create an LE device from the address
                        return await GetBatteryLevelFromAddress(device.BluetoothAddress.ToString("X12"));
                    }
                }
                catch (Exception fallbackEx)
                {
                    LogDebug($"Fallback also failed for {deviceName}: {fallbackEx.Message}");
                    return -1;
                }
            }
        }
        
        private void UpdateUI()
        {
            try
            {
                LogDebug("Updating UI");
                
                // Make sure containers are available
                if (_containers == null || _containers.Count == 0)
                {
                    LogDebug("No containers to update");
                    return;
                }
                
                // Check if we have devices
                if (_btDevices.Count == 0)
                {
                    LogDebug("No devices to show");
                    
                    // Update main container to show no devices
                    if (_deviceContainers.TryGetValue("main", out var mainContainer))
                    {
                        // Update status text
                        var statusEntry = mainContainer.Entries.FirstOrDefault(e => e is PluginText && e.Id == "bt_status") as PluginText;
                        if (statusEntry != null)
                        {
                            statusEntry.Value = "No devices found";
                        }
                    }
                    
                    return;
                }
                
                // Update existing device containers with new data
                foreach (var device in _btDevices)
                {
                    try
                    {
                        // Generate a unique ID for this device
                        string deviceId = device.Key;
                        string containerId = $"bt-device-{deviceId.GetHashCode()}";
                        
                        // We can only update existing containers since InfoPanel doesn't support adding containers after Load
                        if (_deviceContainers.TryGetValue(containerId, out var container))
                        {
                            // Prepare entry IDs
                            string nameId = $"name_{deviceId.GetHashCode()}";
                            string statusId = $"status_{deviceId.GetHashCode()}";
                            string batteryId = $"battery_{deviceId.GetHashCode()}";
                            
                            // Get status text
                            string statusText = device.Value.Status == BluetoothConnectionStatus.Connected ? "Connected" : "Disconnected";
                            if (device.Value.Status == BluetoothConnectionStatus.Connected && device.Value.BatteryLevel >= 0)
                            {
                                statusText = $"Connected ({device.Value.BatteryLevel}%)";
                            }
                            
                            // Update entries
                            var nameEntry = container.Entries.FirstOrDefault(e => e.Id == nameId) as PluginText;
                            var statusEntry = container.Entries.FirstOrDefault(e => e.Id == statusId) as PluginText;
                            var batteryEntry = container.Entries.FirstOrDefault(e => e.Id == batteryId) as PluginSensor;
                            
                            if (nameEntry != null) nameEntry.Value = device.Value.Name;
                            if (statusEntry != null) statusEntry.Value = statusText;
                            if (batteryEntry != null) batteryEntry.Value = device.Value.BatteryLevel >= 0 ? device.Value.BatteryLevel : 0;
                            

                            LogDebug($"Updated container for device {device.Value.Name}");
                        }
                        else
                        {
                            LogDebug($"Device {device.Value.Name} discovered but container doesn't exist - will be visible after restart");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Error updating UI for device {device.Value.Name}: {ex.Message}");
                    }
                }
                
                // Update main container with count
                if (_deviceContainers.TryGetValue("main", out var statusContainer))
                {
                    var statusEntry = statusContainer.Entries.FirstOrDefault(e => e is PluginText && e.Id == "bt_status") as PluginText;
                    if (statusEntry != null)
                    {
                        int connected = _btDevices.Count(d => d.Value.Status == BluetoothConnectionStatus.Connected);
                        statusEntry.Value = $"Found {_btDevices.Count} devices, {connected} connected";
                    }
                }
                
                LogDebug("UI update complete");
            }
            catch (Exception ex)
            {
                LogDebug($"Error updating UI: {ex}");
            }
        }

        public override void Close()
        {
            LogDebug("Close called");
        }
        
        // Helper method to log debug messages
        private void LogDebug(string message)
        {
            if (!_debugMode) return;
            
            try
            {
                // Write to log file
                lock (_logLock)
                {
                    File.AppendAllText(_logPath, $"Bluetooth Plugin: {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Silently fail if logging fails
            }
        }
    }
}