using System;
using System.Net;
using System.Net.Sockets;

namespace TalesLand
{
	/// <summary>
	/// TalesLand load-balance. Opcode 1 is u32=1, u32=6, then the client
	/// version hash (1.267.0.70_rel_&lt;32 hex&gt;). Opcode 2 is string
	/// agentAddress, u32 agentPort, bool ok. ok false is the client's
	/// "hash failed" path.
	/// </summary>
	public static class TlLoadBalanceHost
	{
		public static void Start(string host, int port, int agentPort, bool hashCheck = false, string[] allowedHashes = null)
		{
			IPAddress ip = IPAddress.Parse(host);
			TlListen.Serve("TalesLand-LB", ip, port, delegate(NetworkStream stream, string remote)
			{
				TlLink link = new TlLink();
				byte[] buf = new byte[4096];
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
					foreach (byte[] payload in link.Receive(chunk, out fatal))
					{
						if (fatal != null)
						{
							TlLog.Warn("lb framing " + fatal);
							return;
						}
						int op;
						int size;
						if (!TlOpcode.TryRead(payload, 0, out op, out size))
						{
							continue;
						}
						if (op != (int)TlLoadBalancePacket.LB_LOGIN_REQ)
						{
							TlLog.Info("lb " + remote + " opcode " + op);
							continue;
						}
						TlReader r = new TlReader(payload, size);
						uint a = r.U32();
						uint b = r.U32();
						string credential = r.String();
						TlLog.Info("Client Hash: " + credential);
						bool listed = allowedHashes != null && Array.IndexOf(allowedHashes, credential) >= 0;
						bool ok = credential.Length > 0 && a == 1 && b == 6 && (!hashCheck || listed);
						if (hashCheck && !listed)
						{
							TlLog.Warn("InCorrect Hash: " + credential + ", IP:" + remote);
						}
						TlLog.Info("lb " + remote + " login ok=" + ok);
						byte[] ack = new TlWriter((int)TlLoadBalancePacket.LB_LOGIN_ACK).String(host).U32(agentPort).Bool(ok).Payload();
						byte[] frame = link.Wrap(ack);
						stream.Write(frame, 0, frame.Length);
					}
				}
			});
		}
	}
}
