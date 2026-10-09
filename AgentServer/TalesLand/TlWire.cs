using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TalesLand
{
	/// <summary>
	/// TalesLand trgame wire. Framing, opcode escape, strings and the agent
	/// obfuscation are the forms traced in tools/TalesLand_Emulator (EV-WIRE-001..007).
	/// </summary>
	public static class TlLog
	{
		public static bool DebugMode;
		public static Action<string> SinkInfo = Console.WriteLine;
		public static Action<string> SinkWarn = Console.WriteLine;

		public static void Info(string message)
		{
			if (DebugMode && SinkInfo != null)
			{
				SinkInfo(message);
			}
		}

		public static void Warn(string message)
		{
			if (DebugMode && SinkWarn != null)
			{
				SinkWarn(message);
			}
		}
	}

	public static class TlOpcode
	{
		public const int Escape = 0xFF;
		public const int MaxShort = 0xFE;

		public static void Write(TlWriter w, int opcode)
		{
			if (opcode < 0 || opcode > 0xFFFF)
			{
				throw new ArgumentOutOfRangeException("opcode");
			}
			if (opcode <= MaxShort)
			{
				w.U8(opcode);
			}
			else
			{
				w.U8(Escape);
				w.U16(opcode);
			}
		}

		public static bool TryRead(byte[] buf, int offset, out int opcode, out int size)
		{
			opcode = 0;
			size = 0;
			if (offset >= buf.Length)
			{
				return false;
			}
			int b = buf[offset];
			if (b != Escape)
			{
				opcode = b;
				size = 1;
				return true;
			}
			if (offset + 3 > buf.Length)
			{
				return false;
			}
			opcode = buf[offset + 1] | (buf[offset + 2] << 8);
			size = 3;
			return true;
		}
	}

	public sealed class TlWriter
	{
		private byte[] _buf;
		private int _len;
		private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

		public TlWriter(int opcode)
			: this(256, true, opcode)
		{
		}

		private TlWriter(int capacity, bool withOpcode, int opcode)
		{
			_buf = new byte[Math.Max(capacity, 16)];
			if (withOpcode)
			{
				TlOpcode.Write(this, opcode);
			}
		}

		public static TlWriter Bare(int capacity)
		{
			return new TlWriter(capacity, false, 0);
		}

		public int Length
		{
			get { return _len; }
		}

		private int Need(int n)
		{
			if (_len + n > _buf.Length)
			{
				int cap = Math.Max(_buf.Length * 2, _len + n);
				byte[] next = new byte[cap];
				Buffer.BlockCopy(_buf, 0, next, 0, _len);
				_buf = next;
			}
			int at = _len;
			_len += n;
			return at;
		}

		public TlWriter U8(int v)
		{
			int at = Need(1);
			_buf[at] = (byte)v;
			return this;
		}

		public TlWriter U16(int v)
		{
			int at = Need(2);
			_buf[at] = (byte)v;
			_buf[at + 1] = (byte)(v >> 8);
			return this;
		}

		public TlWriter U32(uint v)
		{
			int at = Need(4);
			_buf[at] = (byte)v;
			_buf[at + 1] = (byte)(v >> 8);
			_buf[at + 2] = (byte)(v >> 16);
			_buf[at + 3] = (byte)(v >> 24);
			return this;
		}

		public TlWriter U32(int v)
		{
			return U32(unchecked((uint)v));
		}

		public TlWriter I32(int v)
		{
			return U32(unchecked((uint)v));
		}

		public TlWriter U64(ulong v)
		{
			int at = Need(8);
			for (int i = 0; i < 8; i++)
			{
				_buf[at + i] = (byte)(v >> (8 * i));
			}
			return this;
		}

		public TlWriter Bool(bool v)
		{
			return U8(v ? 1 : 0);
		}

		public TlWriter Bytes(byte[] b)
		{
			int at = Need(b.Length);
			Buffer.BlockCopy(b, 0, _buf, at, b.Length);
			return this;
		}

		public TlWriter Zeros(int n)
		{
			Need(n);
			return this;
		}

		public TlWriter String(string s)
		{
			if (s == null)
			{
				s = "";
			}
			byte[] raw = Latin1.GetBytes(s);
			if (raw.Length > 0x9FFA)
			{
				throw new InvalidOperationException("string exceeds client limit");
			}
			U32(raw.Length);
			return Bytes(raw);
		}

		public byte[] Payload()
		{
			byte[] o = new byte[_len];
			Buffer.BlockCopy(_buf, 0, o, 0, _len);
			return o;
		}
	}

	public sealed class TlReader
	{
		private readonly byte[] _buf;
		private int _off;
		private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

		public TlReader(byte[] buf, int offset)
		{
			_buf = buf;
			_off = offset;
		}

		public int Remaining
		{
			get { return _buf.Length - _off; }
		}

		private int Need(int n)
		{
			if (_off + n > _buf.Length)
			{
				throw new EndOfStreamException("packet truncated");
			}
			int at = _off;
			_off += n;
			return at;
		}

		public int U8()
		{
			return _buf[Need(1)];
		}

		public int U16()
		{
			int at = Need(2);
			return _buf[at] | (_buf[at + 1] << 8);
		}

		public uint U32()
		{
			int at = Need(4);
			return (uint)_buf[at] | ((uint)_buf[at + 1] << 8) | ((uint)_buf[at + 2] << 16) | ((uint)_buf[at + 3] << 24);
		}

		public int I32()
		{
			return unchecked((int)U32());
		}

		public ulong U64()
		{
			int at = Need(8);
			ulong v = 0;
			for (int i = 0; i < 8; i++)
			{
				v |= (ulong)_buf[at + i] << (8 * i);
			}
			return v;
		}

		public byte[] Bytes(int n)
		{
			int at = Need(n);
			byte[] o = new byte[n];
			Buffer.BlockCopy(_buf, at, o, 0, n);
			return o;
		}

		public string String()
		{
			uint len = U32();
			if (len > 0x9FFA)
			{
				return "";
			}
			return Latin1.GetString(Bytes((int)len));
		}
	}

	public static class TlFrame
	{
		public const int MaxPacket = 0xA000;

		public static byte[] Encode(byte[] payload)
		{
			int total = payload.Length + 2;
			if (total > MaxPacket)
			{
				throw new InvalidOperationException("frame exceeds client limit");
			}
			byte[] o = new byte[total];
			o[0] = (byte)total;
			o[1] = (byte)(total >> 8);
			Buffer.BlockCopy(payload, 0, o, 2, payload.Length);
			return o;
		}
	}

	public sealed class TlFrameDecoder
	{
		private byte[] _buf = new byte[0];

		public List<byte[]> Push(byte[] chunk, out string error)
		{
			error = null;
			byte[] merged = new byte[_buf.Length + chunk.Length];
			Buffer.BlockCopy(_buf, 0, merged, 0, _buf.Length);
			Buffer.BlockCopy(chunk, 0, merged, _buf.Length, chunk.Length);
			_buf = merged;
			List<byte[]> frames = new List<byte[]>();
			while (_buf.Length >= 2)
			{
				int total = _buf[0] | (_buf[1] << 8);
				if (total < 2)
				{
					error = "length-too-small";
					return frames;
				}
				if (total > TlFrame.MaxPacket)
				{
					error = "length-too-large";
					return frames;
				}
				if (_buf.Length < total)
				{
					break;
				}
				byte[] payload = new byte[total - 2];
				Buffer.BlockCopy(_buf, 2, payload, 0, payload.Length);
				frames.Add(payload);
				byte[] rest = new byte[_buf.Length - total];
				Buffer.BlockCopy(_buf, total, rest, 0, rest.Length);
				_buf = rest;
			}
			return frames;
		}
	}

	/// <summary>Agent obfuscation. LCG and nibble transform from trgame sub_9727A0/sub_9727B0.</summary>
	public static class TlObfuscation
	{
		public static int AdvanceKey(int key)
		{
			unchecked
			{
				return 8461 * (key + 9059);
			}
		}

		public static void Nibble(byte[] buf, int key)
		{
			int k = key & 0xFF;
			for (int i = 0; i < buf.Length; i++)
			{
				int b = buf[i];
				buf[i] = (byte)((((b ^ k) << 4) | ((k ^ (b >> 4)) & 0x0F)) & 0xFF);
			}
		}

		public static byte[] Obfuscate(byte[] payload, int key, Random rng)
		{
			int padCount = 2 + rng.Next(3);
			byte[] outb = new byte[payload.Length + padCount + 1 + 4];
			Buffer.BlockCopy(payload, 0, outb, 0, payload.Length);
			for (int i = 0; i < padCount; i++)
			{
				outb[payload.Length + i] = (byte)rng.Next(255);
			}
			outb[payload.Length + padCount] = (byte)padCount;
			int keyAt = payload.Length + padCount + 1;
			uint uk = unchecked((uint)key);
			outb[keyAt] = (byte)uk;
			outb[keyAt + 1] = (byte)(uk >> 8);
			outb[keyAt + 2] = (byte)(uk >> 16);
			outb[keyAt + 3] = (byte)(uk >> 24);
			Nibble(outb, key);
			return outb;
		}

		public static bool TryDeobfuscate(byte[] frame, int key, out byte[] payload)
		{
			payload = null;
			if (frame.Length < 1 + 2 + 1 + 4)
			{
				return false;
			}
			byte[] buf = (byte[])frame.Clone();
			Nibble(buf, key);
			int carried = buf[buf.Length - 4] | (buf[buf.Length - 3] << 8) | (buf[buf.Length - 2] << 16) | (buf[buf.Length - 1] << 24);
			if (carried != key)
			{
				return false;
			}
			int padCount = buf[buf.Length - 5];
			int trailer = padCount + 5;
			if (trailer > buf.Length)
			{
				return false;
			}
			payload = new byte[buf.Length - trailer];
			Buffer.BlockCopy(buf, 0, payload, 0, payload.Length);
			return true;
		}
	}

	public sealed class TlLink
	{
		private readonly TlFrameDecoder _decoder = new TlFrameDecoder();
		private readonly Random _rng = new Random();
		private int _inKey;
		private int _outKey;
		public bool Obfuscated;

		public void EnableObfuscation(int seed)
		{
			_inKey = seed;
			_outKey = seed;
			Obfuscated = true;
		}

		public List<byte[]> Receive(byte[] chunk, out string fatal)
		{
			List<byte[]> frames = _decoder.Push(chunk, out fatal);
			List<byte[]> payloads = new List<byte[]>();
			foreach (byte[] frame in frames)
			{
				if (Obfuscated)
				{
					_inKey = TlObfuscation.AdvanceKey(_inKey);
					byte[] plain;
					if (!TlObfuscation.TryDeobfuscate(frame, _inKey, out plain))
					{
						TlLog.Warn("dropped obfuscated packet (key mismatch)");
						continue;
					}
					payloads.Add(plain);
				}
				else
				{
					payloads.Add(frame);
				}
			}
			return payloads;
		}

		public byte[] Wrap(byte[] payload)
		{
			if (Obfuscated)
			{
				_outKey = TlObfuscation.AdvanceKey(_outKey);
				payload = TlObfuscation.Obfuscate(payload, _outKey, _rng);
			}
			return TlFrame.Encode(payload);
		}
	}

	public static class TlListen
	{
		public static void Serve(string name, IPAddress ip, int port, Action<NetworkStream, string> onClient)
		{
			TcpListener listener = new TcpListener(ip, port);
			listener.Start();
			TlLog.Info(name + " listening on " + ip + ":" + port);
			System.Threading.ThreadPool.QueueUserWorkItem(delegate
			{
				while (true)
				{
					TcpClient client;
					try
					{
						client = listener.AcceptTcpClient();
					}
					catch (Exception ex)
					{
						TlLog.Warn(name + " accept failed: " + ex.Message);
						return;
					}
					string remote = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "?";
					System.Threading.ThreadPool.QueueUserWorkItem(delegate
					{
						try
						{
							using (client)
							using (NetworkStream stream = client.GetStream())
							{
								onClient(stream, remote);
							}
						}
						catch (Exception ex)
						{
							string message = ex.Message ?? "";
							if (message.IndexOf("forcibly closed", StringComparison.OrdinalIgnoreCase) >= 0
								|| message.IndexOf("connection was closed", StringComparison.OrdinalIgnoreCase) >= 0)
							{
								TlLog.Info(name + " " + remote + " disconnected");
								return;
							}
							TlLog.Warn(name + " " + remote + " closed: " + ex.Message);
						}
					});
				}
			});
		}
	}
}
