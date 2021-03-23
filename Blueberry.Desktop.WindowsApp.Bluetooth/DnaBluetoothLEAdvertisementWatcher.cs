using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace Blueberry.Desktop.WindowsApp.Bluetooth
{
    /// <summary>
    /// Wraps and makes use of the <see cref="BluetoothLEAdvertisementWatcher"/>
    /// for easier consumption
    /// </summary>
    public class DnaBluetoothLEAdvertisementWatcher
    {
        #region Private Members

        /// <summary>
        /// The underlying bluetooth watcher class
        /// </summary>
        private readonly BluetoothLEAdvertisementWatcher mWatcher;

        /// <summary>
        /// A list of discovered devices
        /// </summary>
        private readonly Dictionary<string, DnaBluetoothLEDevice> mDiscoveredDevices = new Dictionary<string, DnaBluetoothLEDevice>();

        /// <summary>
        /// The details about GATT services
        /// </summary>
        private readonly GattServiceIds mGattServiceIds;

        /// <summary>
        /// A thread lock object for this class 
        /// </summary>
        private readonly object mThreadLock = new object();

        #endregion

        #region Public Properties

        /// <summary>
        /// Indicates if this watcher is listening for advertisements
        /// </summary>
        public bool Listening => mWatcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

        /// <summary>
        /// A list of discovered devices
        /// </summary>
        public IReadOnlyCollection<DnaBluetoothLEDevice> DiscoveredDevices
        {
            get
            {
                // Clean up any timeouts
                CleanupTimeouts();

                // Practice thread-safety kids!
                lock (mThreadLock)
                {
                    // Convert to read-only list
                    return mDiscoveredDevices.Values.ToList().AsReadOnly();
                }
            }
        }

        /// <summary>
        /// The timeout in seconds that a device is removed from the <see cref="DiscoveredDevices"/>
        /// list if it is not re-advertised within this time
        /// </summary>
        public int HeartbeatTimeout { get; set; } = 30;

        #endregion

        #region Public Events

        /// <summary>
        /// Fired when the bluetooth watcher stops listening
        /// </summary>
        public event Action StoppedListening = () => { };

        /// <summary>
        /// Fired when the bluetooth watcher starts listening
        /// </summary>
        public event Action StartedListening = () => { };

        /// <summary>
        /// Fired when a device is discovered
        /// </summary>
        public event Action<DnaBluetoothLEDevice> DeviceDiscovered = (device) => { };

        /// <summary>
        /// Fired when a new device is discovered
        /// </summary>
        public event Action<DnaBluetoothLEDevice> NewDeviceDiscovered = (device) => { };

        /// <summary>
        /// Fired when a device name changes
        /// </summary>
        public event Action<DnaBluetoothLEDevice> DeviceNameChanged = (device) => { };

        /// <summary>
        /// Fired when a device is removed for timing out
        /// </summary>
        public event Action<DnaBluetoothLEDevice> DeviceTimeout = (device) => { };

        #endregion

        #region Constructor

        /// <summary>
        /// The default constructor
        /// </summary>
        public DnaBluetoothLEAdvertisementWatcher(GattServiceIds gattIds)
        {
            // Null guard
            mGattServiceIds = gattIds ?? throw new ArgumentNullException(nameof(gattIds));

            // Create bluetooth listener
            mWatcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            // Listen out for new advertisements
            mWatcher.Received += WatcherAdvertisementReceivedAsync;

            // Listen out for when the watcher stops listening
            mWatcher.Stopped += (watcher, e) =>
            {
                // Inform listeners
                StoppedListening();
            };
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Listens out for watcher advertisements
        /// </summary>
        /// <param name="sender">The watcher</param>
        /// <param name="args">The arguments</param>
        private async void WatcherAdvertisementReceivedAsync(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            // Cleanup Timeouts
            CleanupTimeouts();

            // Get BLE device info
            var device = await GetBluetoothLEDeviceAsync(
                args.BluetoothAddress, 
                args.Timestamp, 
                args.RawSignalStrengthInDBm);

            // Null guard
            if (device == null)
                return;

            // Is new discovery?
            var newDiscovery = false;
            var existingName = default(string);
            var nameChanged = false;

            // Lock your doors
            lock (mThreadLock)
            {
                // Check if this is a new discovery
                newDiscovery = !mDiscoveredDevices.ContainsKey(device.DeviceId);

                // If this is not new...
                if (!newDiscovery)
                    // Store the old name
                    existingName = mDiscoveredDevices[device.DeviceId].Name;

                // Name changed?
                nameChanged =
                    // If it already exists
                    !newDiscovery &&
                    // And is not a blank  name
                    !string.IsNullOrEmpty(device.Name) &&
                    // And the name is different
                    existingName != device.Name;

                // If we are no longer listening...
                if (!Listening)
                    // Don't bother adding to the list and do nothing
                    return;

                // Add/update the device in the dictionary
                mDiscoveredDevices[device.DeviceId] = device;
            }

            // Inform listeners
            DeviceDiscovered(device);

            // If name changed...
            if (nameChanged)
                // Inform listeners
                DeviceNameChanged(device);

            // If new discovery...
            if (newDiscovery)
                // Inform listeners
                NewDeviceDiscovered(device);
        }

        /// <summary>
        /// Connects to the BLE device and extracts more information from the
        /// <see cref="https://docs.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice"/>
        /// </summary>
        /// <param name="address">The BT address of the device to connect to</param>
        /// <param name="broadcastTime">The time the broadcast message was received</param>
        /// <param name="rssi">The signal strength in dB</param>
        /// <returns></returns>
        private async Task<DnaBluetoothLEDevice> GetBluetoothLEDeviceAsync(ulong address, DateTimeOffset broadcastTime, short rssi)
        {
            // Get bluetooth device info
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask();

            // Null guard
            if (device == null)
                return null;

            // NOTE: This can throw a System.Exception for failures
            // Get GATT services that are available
            var gatt = await device.GetGattServicesAsync().AsTask();

            // If we have any services...
            if (gatt.Status == GattCommunicationStatus.Success)
            {
                // Loop each GATT service
                foreach (var service in gatt.Services)
                {
                    // This ID contains the GATT Profile Assigned number we want!
                    // TODO: Get more info and connect
                    var gattProfileId = service.Uuid;
                }
            }

            // Return the new device information
            return new DnaBluetoothLEDevice
            (
                // Device Id
                deviceId: device.DeviceId,
                // Bluetooth Address
                address: device.BluetoothAddress,
                // Device Name
                name: device.Name,
                // Broadcast Time
                broadcastTime: broadcastTime,
                // Signal Strength
                rssi: rssi,
                // Is Connected?
                connected: device.ConnectionStatus == BluetoothConnectionStatus.Connected,
                // Can Pair?
                canPair: device.DeviceInformation.Pairing.CanPair,
                // Is Paired?
                paired: device.DeviceInformation.Pairing.IsPaired
            );
        }

        /// <summary>
        /// Prune any timed out devices that we have not heard off
        /// </summary>
        private void CleanupTimeouts()
        {
            lock (mThreadLock)
            {
                // The date in time that if less than means a device has timed out
                var threshold = DateTime.UtcNow - TimeSpan.FromSeconds(HeartbeatTimeout);

                // Any devices that have not sent a new broadcast within the heartbeat time
                mDiscoveredDevices.Where(f => f.Value.BroadcastTime < threshold).ToList().ForEach(device =>
                {
                    // Remove device
                    mDiscoveredDevices.Remove(device.Key);

                    // Inform listeners
                    DeviceTimeout(device.Value);
                });
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts listening for advertisements
        /// </summary>
        public void StartListening()
        {
            lock (mThreadLock)
            {
                // If already listening...
                if (Listening)
                    // Do nothing more
                    return;

                // Start the underlying watcher
                mWatcher.Start();
            }
            
            // Inform listeners
            StartedListening();
        }

        /// <summary>
        /// Stops listening for advertisements
        /// </summary>
        public void StopListening()
        {
            lock (mThreadLock)
            {
                // If we are no currently listening...
                if (!Listening)
                    // Do nothing more
                    return;

                // Stop listening
                mWatcher.Stop();

                // Clear any devices
                mDiscoveredDevices.Clear();
            }
        }

        /// <summary>
        /// Attempts to pair to a BLE device, by ID
        /// </summary>
        /// <param name="deviceId">The BLE device ID</param>
        /// <see cref="https://docs.microsoft.com/it-it/windows/uwp/devices-sensors/gatt-client"/>
        /// <returns></returns>
        public async Task PairToDeviceAsync(string deviceId)
        {
            // Get bluetooth device info
            using var device = await BluetoothLEDevice.FromIdAsync(deviceId).AsTask();

            // Null guard
            if (device == null)
                // TODO: Localize
                throw new ArgumentNullException("Failed to get information about the Bluetooth device");

            // If is not already paired...
            if (!device.DeviceInformation.Pairing.IsPaired) { 

                // Listen out for pairing request
                device.DeviceInformation.Pairing.Custom.PairingRequested += (sender, args) =>
                {
                    // Log it
                    // TODO: Remove
                    Console.WriteLine("Accepting pairing request...");

                    // Accept all attempts
                    args.Accept(); // <-- Could enter a pin in here to accept
                };

                // Try and pair to the device
                var result = await device.DeviceInformation.Pairing.Custom.PairAsync(
                    // TODO: Try different types to see if any work
                    DevicePairingKinds.ConfirmOnly
                    ).AsTask();

                // Log the result
                if (result.Status != DevicePairingResultStatus.Paired)
                {
                    // TODO: Remove
                    Console.WriteLine($"Pairing failed: {result.Status}");
                    return;
                }
            }
            Console.WriteLine("Pairing successful");

            var risultatoServ = await device.GetGattServicesAsync();
            if (risultatoServ.Status == GattCommunicationStatus.Success)
            {
                var services = risultatoServ.Services;
                //foreach(var s in services)
                //{
                //    Console.WriteLine("Service: " + s.Uuid);
                //}


                await GetCharactersticsAsync(services);

                //legge/scrive characteristic 
                //var result = await Readwrite.ReadValueAsync();
                //if (result.Status == GattCommunicationStatus.Success)
                //{
                 //   var reader = DataReader.FromBuffer(result.Value);
                  //  var input = new byte[reader.UnconsumedBufferLength];
                   // reader.ReadBytes(input);
                    // Utilize the data as needed
                   // Console.WriteLine(input);
                //}
            }
        }
        private static void RecieveDataAsync(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            //var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
            var bufferLength = (uint)reader.UnconsumedBufferLength;
            var receivedString = "";
            receivedString += reader.ReadString(bufferLength) + "\n";
            Console.WriteLine("Recieved Message: " + receivedString);
        }

        private static async Task WriteTheValueFromCharacteristicAsync(GattCharacteristic c, string st)
        {
            var writer = new DataWriter();
            var bytes = System.Text.Encoding.ASCII.GetBytes(st);
            writer.WriteBytes(bytes);
            var result = await c.WriteValueAsync(writer.DetachBuffer());
            Console.WriteLine("Write on characteristic :" + bytes+" ");
            if (result == GattCommunicationStatus.Success)
            {
                // Successfully wrote to device
                Console.WriteLine("Success!");
            } else
            {
                Console.WriteLine("Failed!");
            }
        }
        private static async Task ReadTheValueFromCharacteristicAsync(GattCharacteristic c)
        {
            var resultt = await c.ReadValueAsync();
            if (resultt.Status == GattCommunicationStatus.Success)
            {
                var reader = DataReader.FromBuffer(resultt.Value);
                var input = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(input);
                // Utilize the data as needed
                var res = System.Text.Encoding.UTF8.GetString(input);
                Console.WriteLine("data rcv:" + res);
            }
        }
        private static List<GattCharacteristic> Characterstics = new List<GattCharacteristic> { };

        private static GattCharacteristic Readwrite;

        private static bool Subscribe = true;
        private static async Task GetCharactersticsAsync(IReadOnlyList<GattDeviceService> services)
        {
            foreach (var s in services)
            {
                var result = await s.GetCharacteristicsAsync();
                if (result.Status == GattCommunicationStatus.Success)
                {
                    var characteristicss = result.Characteristics;
                    var i = 0;
                    foreach (var c in characteristicss)
                    {
                        Characterstics.Add(c);
                        Console.WriteLine("Service UUID="+s.Uuid+" - characteristic: " + c.Uuid);
                        //Console.WriteLine("Service UUID="+s.Uuid+" - characteristic: " + c.Uuid);
                        //var cStr = c.CharacteristicProperties.ToString().ToLower();
                        //if (c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read) &&
                        //    c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write))
                        //{
                        if ((s.Uuid.CompareTo(Guid.Parse("00001800-0000-1000-8000-00805f9b34fb")) == 0) &&
                        (c.Uuid.CompareTo(Guid.Parse("00002a00-0000-1000-8000-00805f9b34fb")) == 0))
                        {
                            //Guid.Parse("00002a29-0000-1000-8000-00805f9b34fb" ritorna: "Alpwise"
                            //Guid.Parse("00002a00-0000-1000-8000-00805f9b34fb" ritorna: "ADI_BLE_HELLOWORLD"
                            c.ValueChanged += RecieveDataAsync;
                            Readwrite = c;

                            await ReadTheValueFromCharacteristicAsync(c);
                            //await WriteTheValueFromCharacteristicAsync(c);

                            //var cosaLeggi = await Readwrite.ReadValueAsync();
                            //Console.WriteLine("Charac read/write: " + cosaLeggi.ToString());
                            Console.WriteLine("Charac read/write: found");
                        }
                        //Console.WriteLine("Found read/write characteristic");
                        //}
                        i++;
                    }
                }
            }
            Subscribe = false;
            Console.WriteLine("Got characteristics");
        }
        #endregion
    }
}
