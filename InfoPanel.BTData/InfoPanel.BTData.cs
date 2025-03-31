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
using System.Reflection;

namespace InfoPanel.BTData
{
    public class BluetoothBatteryPlugin : BasePlugin
    {
        // Plugin data fields
        private readonly PluginText _deviceName = new("device_name", "Device Name", "-");
        private readonly PluginText _status = new("status", "Status", "Disconnected");
        private readonly PluginSensor _batteryLevel = new("battery_level", "Battery Level", 0, "%");

        // Configuration
        private string? _targetFriendlyName; // Friendly name of the target Bluetooth device
        private string? _configFilePath;
        private int _refreshIntervalMinutes = 5; // Default refresh interval (5 minutes)
        private DateTime _lastUpdateTime = DateTime.MinValue;

        // Constructor
        public BluetoothBatteryPlugin()
            : base(
                "bluetooth-battery-plugin",
                "Bluetooth Device Battery",
                "Displays connection status and battery level of a Bluetooth LE device selected by friendly name."
            )
        {
        }

        public override string? ConfigFilePath => _configFilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(_refreshIntervalMinutes);

        // Initialize: Set up config file and read settings
        public override void Initialize()
        {
            _configFilePath = $"{Assembly.GetExecutingAssembly().ManifestModule.FullyQualifiedName}.ini";
            var parser = new FileIniDataParser();
            IniData config;

            if (!File.Exists(_configFilePath))
            {
                // Create default config
                config = new IniData();
                config["Bluetooth Battery Plugin"]["FriendlyName"] = ""; // Empty by default, user must set
                config["Bluetooth Battery Plugin"]["RefreshIntervalMinutes"] = "5";
                parser.WriteFile(_configFilePath, config);
                _targetFriendlyName = "";
                _refreshIntervalMinutes = 5;
            }
            else
            {
                // Read existing config
                config = parser.ReadFile(_configFilePath);
                _targetFriendlyName = config["Bluetooth Battery Plugin"]["FriendlyName"] ?? "";
                if (!int.TryParse(config["Bluetooth Battery Plugin"]["RefreshIntervalMinutes"], out _refreshIntervalMinutes) || _refreshIntervalMinutes <= 0)
                    _refreshIntervalMinutes = 5; // Fallback to 5 minutes
            }

            Console.WriteLine($"Bluetooth Plugin: Target device friendly name: '{_targetFriendlyName}'");
            Console.WriteLine($"Bluetooth Plugin: Refresh interval: {_refreshIntervalMinutes} minutes");
        }

        // Load: Populate the plugin container
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Bluetooth Device");
            container.Entries.AddRange([_deviceName, _status, _batteryLevel]);
            containers.Add(container);
        }

        // UpdateAsync: Fetch Bluetooth device status and battery level
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("Bluetooth Plugin: UpdateAsync cancelled.");
                return;
            }

            var now = DateTime.UtcNow;
            if (_lastUpdateTime != DateTime.MinValue && (now - _lastUpdateTime) < TimeSpan.FromMinutes(_refreshIntervalMinutes))
                return; // Skip if not time to update yet

            Console.WriteLine($"Bluetooth Plugin: Updating at {now:yyyy-MM-dd HH:mm:ss}");

            if (string.IsNullOrEmpty(_targetFriendlyName))
            {
                _status.Value = "No device specified";
                _deviceName.Value = "-";
                _batteryLevel.Value = 0;
                Console.WriteLine("Bluetooth Plugin: No friendly name set in config.");
                return;
            }

            try
            {
                // Enumerate paired Bluetooth LE devices
                string[] requestedProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" };
                var devices = await DeviceInformation.FindAllAsync(
                    BluetoothLEDevice.GetDeviceSelector(),
                    requestedProperties
                );

                DeviceInformation? targetDevice = null;
                foreach (var device in devices)
                {
                    if (device.Name.Equals(_targetFriendlyName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetDevice = device;
                        break;
                    }
                }

                if (targetDevice == null)
                {
                    _status.Value = "Device not found";
                    _deviceName.Value = _targetFriendlyName;
                    _batteryLevel.Value = 0;
                    Console.WriteLine($"Bluetooth Plugin: Device '{_targetFriendlyName}' not found among paired devices.");
                    return;
                }

                _deviceName.Value = targetDevice.Name;
                _status.Value = targetDevice.Properties["System.Devices.Aep.IsConnected"] as bool? == true ? "Connected" : "Disconnected";

                if (_status.Value == "Disconnected")
                {
                    _batteryLevel.Value = 0;
                    Console.WriteLine($"Bluetooth Plugin: Device '{_targetFriendlyName}' is disconnected.");
                }
                else
                {
                    // Connect to the device and query battery level
                    var bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(targetDevice.Id);
                    if (bluetoothLeDevice == null)
                    {
                        _status.Value = "Failed to connect";
                        _batteryLevel.Value = 0;
                        Console.WriteLine($"Bluetooth Plugin: Failed to connect to '{_targetFriendlyName}'.");
                        return;
                    }

                    // Get GATT services and find Battery Service (UUID 0x180F)
                    var gattServices = await bluetoothLeDevice.GetGattServicesForUuidAsync(
                        new Guid("0000180F-0000-1000-8000-00805F9B34FB") // Battery Service UUID
                    );

                    if (gattServices.Status == GattCommunicationStatus.Success && gattServices.Services.Count > 0)
                    {
                        var batteryService = gattServices.Services[0];
                        var characteristics = await batteryService.GetCharacteristicsForUuidAsync(
                            new Guid("00002A19-0000-1000-8000-00805F9B34FB") // Battery Level UUID
                        );

                        if (characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0)
                        {
                            var characteristic = characteristics.Characteristics[0];
                            var result = await characteristic.ReadValueAsync();
                            if (result.Status == GattCommunicationStatus.Success)
                            {
                                var reader = Windows.Storage.Streams.DataReader.FromBuffer(result.Value);
                                byte batteryLevel = reader.ReadByte();
                                _batteryLevel.Value = batteryLevel;
                                Console.WriteLine($"Bluetooth Plugin: Battery level for '{_targetFriendlyName}': {batteryLevel}%");
                            }
                            else
                            {
                                _batteryLevel.Value = 0;
                                Console.WriteLine($"Bluetooth Plugin: Failed to read battery level: {result.Status}");
                            }
                        }
                        else
                        {
                            _batteryLevel.Value = 0;
                            Console.WriteLine("Bluetooth Plugin: Battery Level characteristic not found.");
                        }
                    }
                    else
                    {
                        _batteryLevel.Value = 0;
                        Console.WriteLine("Bluetooth Plugin: Battery Service not found or inaccessible.");
                    }

                    // Clean up
                    bluetoothLeDevice.Dispose();
                }
            }
            catch (Exception ex)
            {
                _status.Value = "Error";
                _batteryLevel.Value = 0;
                Console.WriteLine($"Bluetooth Plugin: Error updating - {ex.Message}");
            }

            _lastUpdateTime = now;
        }

        // Close: Cleanup (empty for now)
        public override void Close()
        {
        }

        // Optional: Synchronous update not implemented
        public override void Update()
        {
            throw new NotImplementedException();
        }
    }
}