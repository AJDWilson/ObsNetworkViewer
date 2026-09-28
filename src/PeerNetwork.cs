using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ObsNetworkViewer
{
    sealed class PeerMessage
    {
        public string Type;
        public string Id;
        public string Name;
        public RecordState State;
        public IPAddress Address;
    }

    /// <summary>
    /// Exchanges recording status with other instances on the LAN via UDP broadcast.
    /// Message types: "query" (sent on startup, asks everyone to reply), "status", and "bye" (sent on exit).
    /// </summary>
    sealed class PeerNetwork : IDisposable
    {
        public const int Port = 50505;
        const string AppTag = "OBSNetworkViewer/1";

        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly string instanceId = Guid.NewGuid().ToString("N");
        readonly UdpClient udp;
        volatile bool disposed;

        /// <summary>Raised on a background thread for every message from another instance.</summary>
        public event Action<PeerMessage> MessageReceived;

        public PeerNetwork()
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            udp.EnableBroadcast = true;
        }

        public void Start()
        {
            new Thread(ReceiveLoop) { IsBackground = true, Name = "PeerNetwork receive" }.Start();
        }

        public void Send(string type, string name, RecordState state)
        {
            if (disposed)
                return;

            var payload = new Dictionary<string, object>
            {
                { "app", AppTag },
                { "type", type },
                { "id", instanceId },
                { "name", name },
                { "state", state.ToString() }
            };
            byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(payload));

            foreach (IPAddress address in GetBroadcastAddresses())
            {
                try { udp.Send(bytes, bytes.Length, new IPEndPoint(address, Port)); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        }

        public void Dispose()
        {
            disposed = true;
            udp.Close();
        }

        void ReceiveLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (!disposed)
            {
                byte[] data;
                try
                {
                    data = udp.Receive(ref remote);
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                PeerMessage message = Parse(data, remote.Address);
                if (message == null || message.Id == instanceId)
                    continue;

                Action<PeerMessage> handler = MessageReceived;
                if (handler != null)
                    handler(message);
            }
        }

        PeerMessage Parse(byte[] data, IPAddress from)
        {
            try
            {
                var obj = json.DeserializeObject(Encoding.UTF8.GetString(data)) as Dictionary<string, object>;
                if (obj == null || !AppTag.Equals(obj["app"]))
                    return null;

                RecordState state;
                if (!Enum.TryParse(obj["state"] as string, out state))
                    state = RecordState.Unknown;

                return new PeerMessage
                {
                    Type = obj["type"] as string,
                    Id = obj["id"] as string,
                    Name = obj["name"] as string,
                    State = state,
                    Address = from
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The limited broadcast address plus the directed broadcast address of every active IPv4 interface.</summary>
        static List<IPAddress> GetBroadcastAddresses()
        {
            var addresses = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork || info.IPv4Mask == null)
                            continue;

                        byte[] ip = info.Address.GetAddressBytes();
                        byte[] mask = info.IPv4Mask.GetAddressBytes();
                        var broadcast = new byte[4];
                        for (int i = 0; i < 4; i++)
                            broadcast[i] = (byte)(ip[i] | ~mask[i]);

                        var address = new IPAddress(broadcast);
                        if (!addresses.Contains(address))
                            addresses.Add(address);
                    }
                }
            }
            catch (NetworkInformationException) { }
            return addresses;
        }
    }
}
