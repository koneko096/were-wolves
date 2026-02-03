using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Net;

namespace WerewolvesLib
{
    public class NetworkManager
    {
        private EventBasedNetListener _listener;
        private NetManager _netManager;
        public NetPacketProcessor PacketProcessor { get; private set; }

        public int MyNodeID { get; private set; }
        public string LocalName { get; set; } = "Unknown";

        public Func<string, bool> OnValidateConnection;
        public List<NetPeer> ConnectedPeers { get; private set; } = new List<NetPeer>();
        public Action<NetPeer> OnPeerConnected;
        public Action<int> OnPeerDisconnected;  // NEW: Event for disconnect with nodeID
        public Action<string> OnLog;

        public NetworkManager(int port = 9050)
        {
            MyNodeID = new Random().Next(1000, 9999);

            _listener = new EventBasedNetListener();
            _netManager = new NetManager(_listener);
            PacketProcessor = new NetPacketProcessor();

            _netManager.IPv6Enabled = false;
            _netManager.UnconnectedMessagesEnabled = true;
            _netManager.BroadcastReceiveEnabled = true;

            _listener.ConnectionRequestEvent += OnConnectionRequested;
            _listener.NetworkReceiveUnconnectedEvent += OnReceiveUnconnected;

            // Setup Connection Logic
            _listener.PeerConnectedEvent += peer =>
            {
                ConnectedPeers.Add(peer);
                OnLog?.Invoke($"Connected to {peer}");

                // Trigger the event for GameHandler
                OnPeerConnected?.Invoke(peer);
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                int nodeID = peer.Id; // Get the peer's unique ID
                ConnectedPeers.Remove(peer);
                OnPeerDisconnected?.Invoke(nodeID);
            };

            // Setup Packet Routing
            _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                // Pass the reader to the PacketProcessor to auto-parse the class
                PacketProcessor.ReadAllPackets(reader, peer);
            };

            // Start
            _netManager.Start(port);
        }

        private void OnReceiveUnconnected(IPEndPoint point, NetPacketReader reader, UnconnectedMessageType type)
        {
            if ((type == UnconnectedMessageType.Broadcast || type == UnconnectedMessageType.BasicMessage)
                && reader.GetString() == "WEREWOLF_DISCOVERY")
            {
                // 1. Read the explicit port (if you added that logic previously)
                int remotePort = reader.GetInt();

                // 2. SELF CHECK: Do not connect to myself
                if (remotePort == _netManager.LocalPort) return;

                // 3. DUPLICATE CHECK: Do not connect if already connected
                foreach (var peer in ConnectedPeers)
                {
                    // Check if we are already connected to this IP:Port
                    // Note: On Localhost, IPs are all 127.0.0.1, so Port is the key differentiator
                    if (peer.Port == remotePort) return;
                }

                OnLog?.Invoke($"[Discovery] Found new peer at {remotePort}. Connecting...");

                ConnectTo(IPAddress.Loopback.ToString(), remotePort);
            }
        }

        private void OnConnectionRequested(ConnectionRequest request)
        {
            try
            {
                NetDataReader reader = request.Data;
                if (reader.AvailableBytes > 0)
                {
                    string key = reader.GetString();
                    string incomingName = reader.GetString();

                    // A. Check Key
                    if (key != "WEREWOLF_KEY")
                    {
                        request.Reject();
                        return;
                    }

                    // B. Check Name Deduplication (Ask GameHandler)
                    // If OnValidateConnection is null, we default to true (allow)
                    bool isNameValid = OnValidateConnection?.Invoke(incomingName) ?? true;

                    if (isNameValid)
                    {
                        // OnLog?.Invoke($"[Network] Accepted connection from {incomingName}");
                        request.Accept();
                    }
                    else
                    {
                        OnLog?.Invoke($"[Network] Rejected duplicate/invalid name: {incomingName}");
                        request.Reject();
                    }
                }
                else
                {
                    request.Reject(); // No data sent
                }
            }
            catch
            {
                request.Reject(); // Malformed packet
            }
        }

        public void Update()
        {
            _netManager.PollEvents();
        }

        public void ConnectTo(string ip, int port)
        {
            // CREATE HANDSHAKE PACKET
            NetDataWriter writer = new NetDataWriter();
            writer.Put("WEREWOLF_KEY"); // 1. Key
            writer.Put(LocalName);      // 2. My Name

            // Connect sending the Writer (Payload) instead of just the Key string
            _netManager.Connect(ip, port, writer);
        }

        public void SendBroadcastDiscovery()
        {
            NetDataWriter writer = new NetDataWriter();
            writer.Put("WEREWOLF_DISCOVERY");
            writer.Put(_netManager.LocalPort);

            // --- 1. RELEASE BEHAVIOR (Standard LAN) ---
            // This runs in BOTH Debug and Release.
            // It broadcasts to the entire Wi-Fi/Ethernet on the default port (9050).
            // Use port 9050 as the "Meeting Point" for real games.
            _netManager.SendBroadcast(writer, 9050);

            // --- 2. DEBUG BEHAVIOR (Localhost Shotgun) ---
            // This code is STRIPPED OUT when you switch to 'Release' mode.
#if DEBUG
            OnLog?.Invoke("[Debug] Performing Localhost Shotgun Discovery (Ports 9050-9055)...");

            for (int p = 9050; p <= 9055; p++)
            {
                // Skip default broadcast port (already covered) and my own port
                if (p == 9050 || p == _netManager.LocalPort) continue;

                // Shoot direct packet to Localhost
                _netManager.SendUnconnectedMessage(writer, new IPEndPoint(IPAddress.Loopback, p));
            }
#endif
        }

        public void SendToAll(NetDataWriter writer)
        {
            foreach (var peer in ConnectedPeers)
            {
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            }
        }

        public void Stop()
        {
            _netManager.Stop();
        }
    }
}
