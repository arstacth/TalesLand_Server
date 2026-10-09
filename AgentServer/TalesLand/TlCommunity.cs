using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace TalesLand
{
	/// <summary>
	/// Friend / whisper TCP service. The client connects here after the agent
	/// answers community sub 0 with this host and port (EV-COMM-003).
	/// Top level is login 2, disconnect 4, forward 5, keepalive 6.
	/// </summary>
	public static class TlCommunityHost
	{
		public const int Port = 9502;

		private static readonly object Gate = new object();
		private static readonly Dictionary<string, Client> ByName = new Dictionary<string, Client>(StringComparer.OrdinalIgnoreCase);

		private sealed class Client
		{
			public string Name;
			public NetworkStream Stream;
			public TlLink Link;
		}

		public static void Start()
		{
			TlListen.Serve("TalesLand-Community", IPAddress.Any, Port, Run);
		}

		private static void Run(NetworkStream stream, string remote)
		{
			TlLink link = new TlLink();
			Client mine = new Client { Stream = stream, Link = link };
			byte[] buf = new byte[8192];
			TlFrameDecoder decoder = new TlFrameDecoder();
			TlLog.Info("community connected " + remote);
			try
			{
				while (true)
				{
					int n = stream.Read(buf, 0, buf.Length);
					if (n <= 0)
					{
						return;
					}
					byte[] chunk = new byte[n];
					Buffer.BlockCopy(buf, 0, chunk, 0, n);
					string fatal;
					List<byte[]> frames = decoder.Push(chunk, out fatal);
					if (fatal != null)
					{
						TlLog.Warn("community framing " + fatal);
						return;
					}
					foreach (byte[] payload in frames)
					{
						int op;
						int size;
						if (!TlOpcode.TryRead(payload, 0, out op, out size))
						{
							continue;
						}
						Handle(mine, op, new TlReader(payload, size));
					}
				}
			}
			finally
			{
				lock (Gate)
				{
					Client current;
					if (mine.Name != null && ByName.TryGetValue(mine.Name, out current) && current == mine)
					{
						ByName.Remove(mine.Name);
					}
				}
				TlLog.Info("community closed " + (mine.Name ?? remote));
			}
		}

		private static void Handle(Client mine, int op, TlReader reader)
		{
			if (op == 2)
			{
				string name = reader.Remaining >= 4 ? reader.String() : "";
				if (name.Length == 0)
				{
					TlLog.Info("community login empty");
					return;
				}
				lock (Gate)
				{
					mine.Name = name;
					ByName[name] = mine;
				}
				TlLog.Info("community login " + name);
				return;
			}
			if (op == 4)
			{
				lock (Gate)
				{
					Client current;
					if (mine.Name != null && ByName.TryGetValue(mine.Name, out current) && current == mine)
					{
						ByName.Remove(mine.Name);
					}
				}
				return;
			}
			if (op == 6)
			{
				return;
			}
			if (op == 5)
			{
				Forward(mine, reader);
				return;
			}
			TlLog.Info("community opcode " + op + " bytes=" + reader.Remaining);
		}

		private static void Forward(Client mine, TlReader reader)
		{
			if (mine.Name == null || reader.Remaining < 4)
			{
				return;
			}
			string target = reader.String();
			if (reader.Remaining < 4)
			{
				TlLog.Info("community forward short to " + target);
				return;
			}
			int outer = reader.U16();
			int nested = reader.U16();
			if (outer != nested || outer < 3 || reader.Remaining < outer - 2)
			{
				TlLog.Info("community forward bad frame to " + target + " " + outer + "/" + nested);
				return;
			}
			byte[] inner = reader.Bytes(outer - 2);
			Client dest;
			lock (Gate)
			{
				if (!ByName.TryGetValue(target, out dest) || dest.Stream == null)
				{
					dest = null;
				}
			}
			if (dest == null)
			{
				TlLog.Info("community forward " + mine.Name + " -> " + target + " offline");
				return;
			}
			int framed = inner.Length + 2;
			TlWriter w = new TlWriter(5);
			w.String(mine.Name).U16(framed).U16(framed).Bytes(inner);
			lock (Gate)
			{
				byte[] frame = dest.Link.Wrap(w.Payload());
				dest.Stream.Write(frame, 0, frame.Length);
			}
			TlLog.Info("community forward " + mine.Name + " -> " + target + " bytes " + inner.Length);
		}
	}
}
