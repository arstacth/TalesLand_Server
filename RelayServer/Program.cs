using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RelayServer
{
	/// <summary>
	/// Thai client UDP relay. The 2026 relay spoke opcodes 21-39; this client
	/// sends 744/748/746 and reads 745/749/747 (EV-NET-001..003).
	/// </summary>
	public static class Program
	{
		public const int Port = 9503;

		private const int Regist = 744;
		private const int RegistAck = 745;
		private const int Forward = 746;
		private const int ForwardIn = 747;
		private const int Heartbeat = 748;
		private const int HeartbeatAck = 749;

		private static readonly object Gate = new object();
		private static readonly Dictionary<int, IPEndPoint> Sessions = new Dictionary<int, IPEndPoint>();

		public static void Main()
		{
			Console.Title = "TalesLand Relay";
			UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
			udp.Client.ReceiveBufferSize = 1 << 20;
			udp.Client.SendBufferSize = 1 << 20;
			Info("relay listening on 0.0.0.0:" + Port);
			IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
			while (true)
			{
				byte[] msg;
				try
				{
					msg = udp.Receive(ref remote);
				}
				catch (Exception ex)
				{
					Info("relay recv " + ex.Message);
					Thread.Sleep(200);
					continue;
				}
				Reply reply = Handle(msg, remote);
				try
				{
					if (reply.ToSender != null)
					{
						udp.Send(reply.ToSender, reply.ToSender.Length, remote);
					}
					if (reply.Forwards != null)
					{
						foreach (KeyValuePair<IPEndPoint, byte[]> one in reply.Forwards)
						{
							udp.Send(one.Value, one.Value.Length, one.Key);
						}
					}
				}
				catch (Exception ex)
				{
					Info("relay send " + ex.Message);
				}
			}
		}

		private static void Info(string message)
		{
			Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + " INF] " + message);
		}

		private struct Reply
		{
			public byte[] ToSender;
			public List<KeyValuePair<IPEndPoint, byte[]>> Forwards;
		}

		private static Reply Handle(byte[] msg, IPEndPoint remote)
		{
			Reply none = new Reply();
			if (msg == null || msg.Length < 4)
			{
				return none;
			}
			if (msg.Length < 8 || msg[0] != 4 || msg[1] != 0xFF)
			{
				none.Forwards = RawToOthers(msg, remote);
				return none;
			}
			int op = msg[2] | (msg[3] << 8);
			if (op == Regist || op == Heartbeat)
			{
				int token = msg[4] | (msg[5] << 8) | (msg[6] << 16) | (msg[7] << 24);
				lock (Gate)
				{
					Sessions[token] = remote;
				}
				if (op == Regist)
				{
					Info("relay regist token " + token + " from " + remote);
					none.ToSender = RegistReply(remote);
					return none;
				}
				none.ToSender = HeartbeatReply();
				return none;
			}
			if (op == Forward && msg.Length >= 10)
			{
				int dest = msg[4] | (msg[5] << 8) | (msg[6] << 16) | (msg[7] << 24);
				int len = msg[8] | (msg[9] << 8);
				if (len < 0 || 10 + len > msg.Length)
				{
					return none;
				}
				IPEndPoint target;
				lock (Gate)
				{
					if (!Sessions.TryGetValue(dest, out target))
					{
						target = null;
					}
				}
				if (target == null)
				{
					return none;
				}
				byte[] wrapped = new byte[5 + len];
				int forwardIn = ForwardIn;
				wrapped[0] = 0xFF;
				wrapped[1] = (byte)forwardIn;
				wrapped[2] = (byte)(forwardIn >> 8);
				wrapped[3] = (byte)len;
				wrapped[4] = (byte)(len >> 8);
				Buffer.BlockCopy(msg, 10, wrapped, 5, len);
				none.Forwards = new List<KeyValuePair<IPEndPoint, byte[]>>();
				none.Forwards.Add(new KeyValuePair<IPEndPoint, byte[]>(target, wrapped));
			}
			return none;
		}

		private static List<KeyValuePair<IPEndPoint, byte[]>> RawToOthers(byte[] msg, IPEndPoint remote)
		{
			List<KeyValuePair<IPEndPoint, byte[]>> list = new List<KeyValuePair<IPEndPoint, byte[]>>();
			lock (Gate)
			{
				foreach (KeyValuePair<int, IPEndPoint> one in Sessions)
				{
					if (!one.Value.Equals(remote))
					{
						list.Add(new KeyValuePair<IPEndPoint, byte[]>(one.Value, msg));
					}
				}
			}
			return list;
		}

		private static byte[] RegistReply(IPEndPoint remote)
		{
			byte[] o = new byte[19];
			int ack = RegistAck;
			o[0] = 0xFF;
			o[1] = (byte)ack;
			o[2] = (byte)(ack >> 8);
			WriteSockaddr(o, 3, remote);
			return o;
		}

		private static byte[] HeartbeatReply()
		{
			int ack = HeartbeatAck;
			return new byte[] { 0xFF, (byte)ack, (byte)(ack >> 8) };
		}

		private static void WriteSockaddr(byte[] o, int at, IPEndPoint remote)
		{
			IPAddress ip = remote.Address.AddressFamily == AddressFamily.InterNetwork
				? remote.Address
				: remote.Address.MapToIPv4();
			byte[] addr = ip.GetAddressBytes();
			o[at] = 2;
			o[at + 1] = 0;
			o[at + 2] = (byte)(remote.Port >> 8);
			o[at + 3] = (byte)remote.Port;
			if (addr.Length == 4)
			{
				Buffer.BlockCopy(addr, 0, o, at + 4, 4);
			}
		}
	}
}
