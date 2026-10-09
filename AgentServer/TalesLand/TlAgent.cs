using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TalesLand
{
	/// <summary>
	/// TalesLand agent session. Speaks the opcode map in TlOpcodes.Generated.cs.
	/// Layouts copied from the traced emulator handlers, not from the 2026 server.
	/// </summary>
	public static class TlAgentHost
	{
		private const int Seed = 0x5eed1234;
		private const int FarmMapType = 1;
		private const int FarmMaxPlayers = 20;
		private const int ParkKind = 47;
		private const int ParkMaxPlayers = 30;
		private const int ParkMap = 33;
		private const int ParkRoomId = 1;
		private const int RelayPort = 9503;
		private const int DefaultRaceMap = 1;
		private const int GameKindFarm = 53;
		private const int MakeRoomFailAlready = 2;
		private const int MakeRoomFailOther = 7;
		private const int EnterRoomFailed = 63;
		private const int EnterRoomMissing = 1;
		private const int EnterRoomFull = 4;
		private const int EnterRoomPassword = 7;
		private const int LicenseStartFailed = 84;
		private const int HellEntryLicense = 1;
		private const int StarterLeaseDays = 30;

		private static readonly object SendLock = new object();
		private static readonly List<Live> Lives = new List<Live>();
		private static readonly Dictionary<int, string> Online = new Dictionary<int, string>();
		private static readonly HashSet<int> InRoom = new HashSet<int>();
		private static readonly HashSet<int> InPark = new HashSet<int>();
		private static readonly object RoomsLock = new object();
		private static readonly Dictionary<int, GameRoom> Rooms = new Dictionary<int, GameRoom>();
		private static TlDb Database;

		private sealed class Live
		{
			public NetworkStream Stream;
			public TlLink Link;
			public int UserNum;
			public string Name;
			public Session Session;
		}

		private sealed class Seat
		{
			public int UserNum;
			public int Index;
			public string Name;
			public byte[] Card;
		}

		private sealed class GameRoom
		{
			public int Id;
			public int Kind;
			public int Max;
			public string Title;
			public string Password;
			public bool Team;
			public bool Tread;
			public bool Item;
			public int Map;
			public int Master;
			public bool Loading;
			public bool ResultSent;
			public bool ReturnArmed;
			public Timer GoalWindow;
			public readonly HashSet<int> Loaded = new HashSet<int>();
			public readonly List<GoalMark> Goals = new List<GoalMark>();
			public readonly List<Seat> Seats = new List<Seat>();
		}

		private sealed class GoalMark
		{
			public int UserNum;
			public int Index;
			public uint Milliseconds;
		}

		private sealed class RoomNote
		{
			public NetworkStream Stream;
			public TlLink Link;
			public byte[] Payload;
		}

		public sealed class OnlineUser
		{
			public int UserNum { get; set; }
			public string Name { get; set; }
		}

		private static readonly int[] EmptyU32Lists = new int[]
		{
			(int)TlAgentPacket.CHAOS_CHANNEL_GET_ALL_USER_POINT_INFO_REQ, (int)TlAgentPacket.CHAOS_CHANNEL_GET_ALL_USER_POINT_INFO_OK,
			(int)TlAgentPacket.GET_QUEST_STATE_EX_REQ, (int)TlAgentPacket.GET_QUEST_STATE_EX_OK,
			(int)TlAgentPacket.GET_NEW_QUEST_STATE_REQ, (int)TlAgentPacket.GET_NEW_QUEST_STATE_OK,
			(int)TlAgentPacket.CHALLENGE_SUBJECT_INFO_REQ, (int)TlAgentPacket.CHALLENGE_SUBJECT_INFO_ACK,
			(int)TlAgentPacket.MY_FARM_COLLECTION_LIST_REQ, (int)TlAgentPacket.MY_FARM_COLLECTION_LIST_OK,
			(int)TlAgentPacket.UNK_427, (int)TlAgentPacket.UNK_428,
			(int)TlAgentPacket.UNK_429, (int)TlAgentPacket.UNK_430,
			(int)TlAgentPacket.EMBLEM_USER_INFO_REQ, (int)TlAgentPacket.EMBLEM_USER_INFO_RESULT_OK,
			(int)TlAgentPacket.EMBLEM_MISSION_USER_INFO_REQ, (int)TlAgentPacket.EMBLEM_MISSION_USER_INFO_RESULT_OK
		};

		private static readonly int[] ResultThenEmpty = new int[]
		{
			(int)TlAgentPacket.GET_ITEMMSG_REQ, (int)TlAgentPacket.GET_ITEMMSG_ACK,
			(int)TlAgentPacket.EXTRA_ABILITIES_REQ, (int)TlAgentPacket.EXTRA_ABILITIES_FAILED,
			(int)TlAgentPacket.QUEST_GET_ALL_STATE_REQ, (int)TlAgentPacket.QUEST_GET_ALL_STATE_ACK
		};

		public static void Start(string host, int port, string connectionString)
		{
			TlDb db = new TlDb(connectionString);
			Database = db;
			IPAddress ip = IPAddress.Parse(host);
			AdvertisedHost = host;
			TlCommunityHost.Start();
			TlListen.Serve("TalesLand-Agent", ip, port, delegate(NetworkStream stream, string remote)
			{
				Run(stream, remote, db);
			});
		}

		private static string AdvertisedHost = "127.0.0.1";

		private sealed class Session
		{
			public int? AccountId;
			public int? CharacterSetNum;
			public int? PlayedModel;
			public bool SeedConsumed;
			public ulong ClockAnchor;
			public DateTime ClockAtUtc;
			public int? GameIndex;
			public int RoomId;
			public int MiniMap;
			public readonly Dictionary<int, int> MiniScores = new Dictionary<int, int>();
			public int CapsuleItem;
			public int CapsuleMachine;
			public int LicenseType;
			public int LicenseMission;
			public int TimeAttackMap;
			public byte[] PendingSuit;
			public byte[] UdpBlock;
		}

		private static void Run(NetworkStream stream, string remote, TlDb db)
		{
			TlLink link = new TlLink();
			Session session = new Session();
			Live live = new Live { Stream = stream, Link = link, Session = session };
			lock (SendLock)
			{
				Lives.Add(live);
			}
			byte[] buf = new byte[8192];
			TlLog.Info("connected");
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
				List<byte[]> payloads = link.Receive(chunk, out fatal);
				if (fatal != null)
				{
					TlLog.Warn("agent framing " + fatal);
					return;
				}
				foreach (byte[] payload in payloads)
				{
					int op;
					int size;
					if (!TlOpcode.TryRead(payload, 0, out op, out size))
					{
						continue;
					}
					TlReader reader = new TlReader(payload, size);
					Handle(stream, link, db, session, remote, op, reader);
				}
			}
			}
			finally
			{
				int gone = 0;
				lock (SendLock)
				{
					Lives.Remove(live);
					if (live.UserNum > 0)
					{
						bool replaced = false;
						foreach (Live other in Lives)
						{
							if (other.UserNum == live.UserNum)
							{
								replaced = true;
								break;
							}
						}
						if (!replaced)
						{
							Online.Remove(live.UserNum);
							InRoom.Remove(live.UserNum);
							InPark.Remove(live.UserNum);
							gone = live.UserNum;
						}
					}
				}
				if (gone > 0)
				{
					SendRoomNotes(TakeSeat(gone));
				}
			}
		}

		public static string OnlineSummary()
		{
			lock (SendLock)
			{
				int users = Online.Count;
				string names = "";
				if (users > 0)
				{
					names = " " + string.Join(", ", new List<string>(Online.Values).ToArray());
				}
				return "ShowInfo - user(" + users + "), room(" + InRoom.Count + "), parkroom(" + InPark.Count + ")" + names;
			}
		}

		public static void ReloadServerSettings()
		{
			if (Database == null)
			{
				throw new InvalidOperationException("Agent is not started");
			}
			Database.ReloadSettings();
			byte[] payload = ServerSettings(Database);
			Live[] copy;
			lock (SendLock)
			{
				copy = Lives.ToArray();
			}
			foreach (Live live in copy)
			{
				try
				{
					Send(live.Stream, live.Link, payload);
				}
				catch (Exception ex)
				{
					TlLog.Warn("settings push failed: " + ex.Message);
				}
			}
			TlLog.Info("reloaded tblServerSettingInfo " + Database.ClientSettings.Count);
		}

		public static int ReloadMaps()
		{
			if (Database == null)
			{
				throw new InvalidOperationException("Agent is not started");
			}
			int maps = Database.CountMaps();
			TlLog.Info("reloaded tblmapinfo " + maps);
			return maps;
		}

		public static List<OnlineUser> OnlineUsers()
		{
			List<OnlineUser> list = new List<OnlineUser>();
			lock (SendLock)
			{
				foreach (Live live in Lives)
				{
					if (live.UserNum <= 0)
					{
						continue;
					}
					list.Add(new OnlineUser
					{
						UserNum = live.UserNum,
						Name = string.IsNullOrEmpty(live.Name) ? ("user " + live.UserNum) : live.Name
					});
				}
			}
			return list;
		}

		public static bool DisconnectUser(int userNum)
		{
			List<NetworkStream> streams = new List<NetworkStream>();
			lock (SendLock)
			{
				for (int i = Lives.Count - 1; i >= 0; i--)
				{
					if (Lives[i].UserNum != userNum)
					{
						continue;
					}
					streams.Add(Lives[i].Stream);
					Lives.RemoveAt(i);
				}
				if (streams.Count > 0)
				{
					Online.Remove(userNum);
					InRoom.Remove(userNum);
					InPark.Remove(userNum);
				}
			}
			foreach (NetworkStream stream in streams)
			{
				try
				{
					stream.Close();
				}
				catch (Exception)
				{
				}
			}
			return streams.Count > 0;
		}

		private static void MarkOnline(Session session, NetworkStream stream, int userNum, string name)
		{
			if (userNum <= 0)
			{
				return;
			}
			session.AccountId = userNum;
			string shown = string.IsNullOrEmpty(name) ? ("user " + userNum) : name;
			lock (SendLock)
			{
				Online[userNum] = shown;
				foreach (Live live in Lives)
				{
					if (live.Stream == stream)
					{
						live.UserNum = userNum;
						live.Name = shown;
						break;
					}
				}
			}
		}

		private static void SendRoomNotes(List<RoomNote> notes)
		{
			if (notes == null)
			{
				return;
			}
			foreach (RoomNote note in notes)
			{
				try
				{
					Send(note.Stream, note.Link, note.Payload);
				}
				catch (Exception ex)
				{
					TlLog.Warn("room notify failed: " + ex.Message);
				}
			}
		}

		private static List<RoomNote> NotesFor(List<int> users, byte[] payload)
		{
			List<RoomNote> notes = new List<RoomNote>();
			if (payload == null || users == null || users.Count == 0)
			{
				return notes;
			}
			lock (SendLock)
			{
				foreach (Live live in Lives)
				{
					if (live.Stream == null || live.Link == null || !users.Contains(live.UserNum))
					{
						continue;
					}
					notes.Add(new RoomNote { Stream = live.Stream, Link = live.Link, Payload = payload });
				}
			}
			return notes;
		}

		private static void ClearSeatSession(int userNum)
		{
			lock (SendLock)
			{
				foreach (Live live in Lives)
				{
					if (live.UserNum == userNum && live.Session != null)
					{
						live.Session.GameIndex = null;
						live.Session.RoomId = 0;
					}
				}
			}
		}

		private static bool Seated(int userNum)
		{
			foreach (GameRoom room in Rooms.Values)
			{
				foreach (Seat seat in room.Seats)
				{
					if (seat.UserNum == userNum)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool HasIndex(GameRoom room, int index)
		{
			foreach (Seat seat in room.Seats)
			{
				if (seat.Index == index)
				{
					return true;
				}
			}
			return false;
		}

		private static bool RoomMatches(GameRoom room, int battle, int tread, int item)
		{
			if (battle == 0 && !room.Team)
			{
				return false;
			}
			if (battle == 1 && room.Team)
			{
				return false;
			}
			if (tread == 0 && !room.Tread)
			{
				return false;
			}
			if (tread == 1 && room.Tread)
			{
				return false;
			}
			if (item == 0 && !room.Item)
			{
				return false;
			}
			if (item == 1 && room.Item)
			{
				return false;
			}
			return true;
		}

		private static byte[] EnterRoomFail(int reason, bool extra)
		{
			TlWriter w = new TlWriter((int)TlAgentPacket.ENTER_ROOM_ACK).U32(EnterRoomFailed).U8(reason);
			if (extra)
			{
				w.U8(0);
			}
			return w.Payload();
		}

		private static List<RoomNote> TakeSeat(int userNum)
		{
			int index = -1;
			int master = -1;
			int roomId = 0;
			List<int> others = new List<int>();
			lock (RoomsLock)
			{
				GameRoom found = null;
				Seat seat = null;
				foreach (GameRoom room in Rooms.Values)
				{
					foreach (Seat one in room.Seats)
					{
						if (one.UserNum == userNum)
						{
							found = room;
							seat = one;
							break;
						}
					}
					if (found != null)
					{
						break;
					}
				}
				if (found == null)
				{
					return new List<RoomNote>();
				}
				index = seat.Index;
				found.Seats.Remove(seat);
				foreach (Seat one in found.Seats)
				{
					others.Add(one.UserNum);
				}
				if (found.Seats.Count == 0)
				{
					Rooms.Remove(found.Id);
				}
				else
				{
					roomId = found.Id;
				}
				if (roomId != 0 && index == found.Master)
				{
					int next = int.MaxValue;
					foreach (Seat one in found.Seats)
					{
						if (one.Index < next)
						{
							next = one.Index;
						}
					}
					found.Master = next;
					master = next;
				}
			}
			ClearSeatSession(userNum);
			List<RoomNote> notes = NotesFor(others, new TlWriter((int)TlAgentPacket.WAIT_PLAYER_OUT).U8(index).Payload());
			if (master >= 0)
			{
				notes.AddRange(NotesFor(others, new TlWriter((int)TlAgentPacket.SET_MASTER).U8(master).Payload()));
				TlLog.Info("room master moved to " + master);
			}
			if (roomId != 0)
			{
				notes.AddRange(FinishLoadNotes(roomId));
				notes.AddRange(FinishRaceNotes(roomId));
			}
			return notes;
		}

		private static List<RoomNote> FinishLoadNotes(int roomId)
		{
			List<int> users = new List<int>();
			lock (RoomsLock)
			{
				GameRoom room;
				if (!Rooms.TryGetValue(roomId, out room) || !room.Loading || room.Seats.Count == 0)
				{
					return new List<RoomNote>();
				}
				foreach (Seat seat in room.Seats)
				{
					if (!room.Loaded.Contains(seat.UserNum))
					{
						return new List<RoomNote>();
					}
					users.Add(seat.UserNum);
				}
				room.Loading = false;
				room.Loaded.Clear();
			}
			// CGameClock is cleared to 0 by the loading-end packet and then counts frames at 60Hz.
			// A wall-clock tick leaves the start gate waiting for that many frames.
			const uint startFrame = 90;
			List<RoomNote> notes = NotesFor(users, new TlWriter((int)TlAgentPacket.ALL_USER_END_LOADING).Payload());
			notes.AddRange(NotesFor(users, new TlWriter((int)TlAgentPacket.START_GAME_RES).U32(startFrame).U32(0).Payload()));
			TlLog.Info("room load done start frame " + startFrame);
			return notes;
		}

		private static List<RoomNote> FinishRaceNotes(int roomId)
		{
			List<int> users = new List<int>();
			List<GoalMark> goals = new List<GoalMark>();
			List<Seat> seats = new List<Seat>();
			lock (RoomsLock)
			{
				GameRoom room;
				if (!Rooms.TryGetValue(roomId, out room) || room.ResultSent || room.Seats.Count == 0 || room.Goals.Count == 0)
				{
					return new List<RoomNote>();
				}
				room.ResultSent = true;
				goals.AddRange(room.Goals);
				seats.AddRange(room.Seats);
				foreach (Seat seat in room.Seats)
				{
					users.Add(seat.UserNum);
				}
			}
			byte[] result = GameResult(goals, seats);
			TlLog.Info("room result players " + seats.Count);
			return NotesFor(users, result);
		}

		private static void NoteGoalWindow(int roomId)
		{
			bool all = false;
			bool first = false;
			uint anchor = 0;
			lock (RoomsLock)
			{
				GameRoom room;
				if (!Rooms.TryGetValue(roomId, out room) || room.ResultSent)
				{
					return;
				}
				int done = room.Goals.Count;
				all = room.Seats.Count > 0 && done >= room.Seats.Count;
				first = done == 1 && room.GoalWindow == null;
				if (first && room.Goals.Count > 0)
				{
					anchor = room.Goals[0].Milliseconds;
				}
			}
			if (all)
			{
				PublishResult(roomId);
				return;
			}
			if (!first)
			{
				return;
			}
			List<int> users = RoomUsers(roomId);
			if (users.Count > 0)
			{
				// The countdown UI starts only from this packet. The u32 is the finisher's race
				// clock in milliseconds, so the digits open on 10 and tick down with the race.
				byte[] henry = new TlWriter((int)TlAgentPacket.START_TIME_OUT_COUNT).U32(anchor).U8(10).Payload();
				SendRoomNotes(NotesFor(users, henry));
				TlLog.Info("room henry " + anchor + " sec 10");
			}
			// Henry's count shows 10, then keeps moving for a short settle, so the window is 12 seconds.
			Timer window = new Timer(delegate(object state) { PublishResult(roomId); }, null, 12000, Timeout.Infinite);
			lock (RoomsLock)
			{
				GameRoom room;
				if (!Rooms.TryGetValue(roomId, out room) || room.ResultSent)
				{
					window.Dispose();
					return;
				}
				if (room.GoalWindow != null)
				{
					room.GoalWindow.Dispose();
				}
				room.GoalWindow = window;
			}
			TlLog.Info("room goal window 12s");
		}

		private static void PublishResult(int roomId)
		{
			List<RoomNote> notes = FinishRaceNotes(roomId);
			if (notes.Count == 0)
			{
				return;
			}
			SendRoomNotes(notes);
			bool arm = false;
			lock (RoomsLock)
			{
				GameRoom room;
				if (Rooms.TryGetValue(roomId, out room) && !room.ReturnArmed)
				{
					room.ReturnArmed = true;
					arm = true;
					if (room.GoalWindow != null)
					{
						room.GoalWindow.Dispose();
						room.GoalWindow = null;
					}
				}
			}
			if (arm)
			{
				new Timer(delegate(object state) { ReturnToRoom(roomId); }, null, 8000, Timeout.Infinite);
			}
		}

		private static void ReturnToRoom(int roomId)
		{
			List<int> users = RoomUsers(roomId);
			if (users.Count == 0)
			{
				return;
			}
			// 694 asks to leave. The frame only leaves once the result board has finished and this
			// empty notify has armed the room-result flag. Without it the board stays up.
			SendRoomNotes(NotesFor(users, new TlWriter((int)TlAgentPacket.EMBLEM_ROOM_RESULT_FOR_MISSION_CHECK_NOTIFY).Payload()));
			SendRoomNotes(NotesFor(users, new TlWriter((int)TlAgentPacket.MOVE_TO_GAME_ROOM).Payload()));
			TlLog.Info("room back " + roomId);
		}

		private static byte[] GameResult(List<GoalMark> goals, List<Seat> seats)
		{
			goals.Sort(delegate(GoalMark a, GoalMark b) { return a.Milliseconds.CompareTo(b.Milliseconds); });
			List<Seat> order = new List<Seat>();
			foreach (GoalMark goal in goals)
			{
				foreach (Seat seat in seats)
				{
					if (seat.UserNum == goal.UserNum)
					{
						order.Add(seat);
						break;
					}
				}
			}
			foreach (Seat seat in seats)
			{
				bool listed = false;
				foreach (Seat have in order)
				{
					if (have.UserNum == seat.UserNum)
					{
						listed = true;
						break;
					}
				}
				if (!listed)
				{
					order.Add(seat);
				}
			}
			TlWriter w = new TlWriter((int)TlAgentPacket.UNK_683);
			w.Bool(false).Bool(false).U8(order.Count);
			int place = 1;
			foreach (Seat seat in order)
			{
				uint ms = 3600000;
				foreach (GoalMark goal in goals)
				{
					if (goal.UserNum == seat.UserNum)
					{
						ms = goal.Milliseconds;
						break;
					}
				}
				// The client stores this first u32 as the result time and sorts the board by it.
				// The next u16 is the EXP the result row shows and adds. The u16 after the time is the TR.
				bool finished = ms != 3600000;
				int exp = RaceExp(place, finished);
				int tr = RaceTr(place, finished);
				w.U32(ms).U8(place).U8(seat.Index);
				w.U16(exp);
				w.U32(ms).U16(tr).U32(0).U32(0).U32(0);
				if (Database != null)
				{
					Database.AddPlayReward(seat.UserNum, exp, tr);
				}
				TlLog.Info("room reward user " + seat.UserNum + " place " + place + " exp " + exp + " tr " + tr);
				w.U8(0).U8(0).U16(0).U16(0).U32(0);
				// One entry in each list so the result screen has a row object to read.
				w.U32(1).U32(0).U32(1).U32(0).Zeros(28).U32(0);
				place++;
			}
			w.U32(0);
			return w.Payload();
		}

		private static int RaceExp(int place, bool finished)
		{
			if (!finished)
			{
				return 15;
			}
			if (place <= 1)
			{
				return 100;
			}
			if (place == 2)
			{
				return 70;
			}
			if (place == 3)
			{
				return 50;
			}
			return 30;
		}

		private static int RaceTr(int place, bool finished)
		{
			if (!finished)
			{
				return 10;
			}
			if (place <= 1)
			{
				return 60;
			}
			if (place == 2)
			{
				return 40;
			}
			if (place == 3)
			{
				return 30;
			}
			return 20;
		}

		private static List<RoomNote> OpenRoom(int userNum, GameRoom room, byte[] card, string name)
		{
			List<KeyValuePair<int, int>> kicks = new List<KeyValuePair<int, int>>();
			lock (RoomsLock)
			{
				List<GameRoom> existing = new List<GameRoom>(Rooms.Values);
				foreach (GameRoom one in existing)
				{
					for (int i = one.Seats.Count - 1; i >= 0; i--)
					{
						if (one.Seats[i].UserNum == userNum)
						{
							one.Seats.RemoveAt(i);
						}
					}
					if (one.Seats.Count == 0)
					{
						Rooms.Remove(one.Id);
					}
				}
				GameRoom old;
				if (Rooms.TryGetValue(room.Id, out old))
				{
					foreach (Seat seat in old.Seats)
					{
						kicks.Add(new KeyValuePair<int, int>(seat.UserNum, seat.Index));
					}
					Rooms.Remove(room.Id);
				}
				room.Seats.Clear();
				room.Seats.Add(new Seat { UserNum = userNum, Index = 0, Name = name, Card = card });
				Rooms[room.Id] = room;
			}
			List<RoomNote> notes = new List<RoomNote>();
			foreach (KeyValuePair<int, int> kick in kicks)
			{
				ClearSeatSession(kick.Key);
				notes.AddRange(NotesFor(new List<int> { kick.Key }, new TlWriter((int)TlAgentPacket.WAIT_PLAYER_OUT).U8(kick.Value).Payload()));
			}
			return notes;
		}

		private sealed class JoinResult
		{
			public byte[] Fail;
			public readonly List<byte[]> Self = new List<byte[]>();
			public List<RoomNote> Others = new List<RoomNote>();
		}

		private static JoinResult JoinRoom(Session session, TlCharacter me, int roomId, string password)
		{
			JoinResult result = new JoinResult();
			int userNum = session.AccountId ?? 0;
			string name = me != null ? me.Nickname : "";
			List<int> others = new List<int>();
			List<byte[]> present = new List<byte[]>();
			GameRoom room = null;
			int index = 0;
			int master = 0;
			byte[] card = null;
			lock (RoomsLock)
			{
				if (Seated(userNum))
				{
					result.Fail = EnterRoomFail(MakeRoomFailAlready, false);
					return result;
				}
				GameRoom found;
				if (!Rooms.TryGetValue(roomId, out found) || found.Seats.Count == 0)
				{
					result.Fail = EnterRoomFail(EnterRoomMissing, false);
					return result;
				}
				if (found.Seats.Count >= found.Max)
				{
					result.Fail = EnterRoomFail(EnterRoomFull, true);
					return result;
				}
				if (!string.IsNullOrEmpty(found.Password) && found.Password != (password ?? ""))
				{
					result.Fail = EnterRoomFail(EnterRoomPassword, true);
					return result;
				}
				while (HasIndex(found, index))
				{
					index++;
				}
				foreach (Seat seat in found.Seats)
				{
					others.Add(seat.UserNum);
					present.Add(seat.Card);
				}
				card = NewRoomUser(userNum, name, me, index, session.UdpBlock);
				found.Seats.Add(new Seat { UserNum = userNum, Index = index, Name = name, Card = card });
				room = found;
				master = found.Master;
			}
			session.GameIndex = index;
			session.RoomId = room.Id;
			result.Self.Add(new TlWriter((int)TlAgentPacket.ROOM_USER_LIST_CLEAR).Payload());
			result.Self.Add(new TlWriter((int)TlAgentPacket.ROOM_USER_LIST).U32(0).Payload());
			result.Self.Add(EnterMadeRoom(room.Kind, room.Max, room.Id, room.Map, room.Title, team: room.Team, tread: room.Tread, item: room.Item, myIndex: index));
			result.Self.AddRange(present);
			result.Self.Add(card);
			result.Self.Add(new TlWriter((int)TlAgentPacket.SET_MASTER).U8(master).Payload());
			result.Others = NotesFor(others, card);
			return result;
		}

		private static JoinResult EnterShared(Session session, TlCharacter me, int roomId, int kind, int max, int map, string title)
		{
			JoinResult result = new JoinResult();
			int userNum = session.AccountId ?? 0;
			string name = me != null ? me.Nickname : "";
			List<int> others = new List<int>();
			List<byte[]> present = new List<byte[]>();
			int index = 0;
			int master = 0;
			byte[] card = null;
			lock (RoomsLock)
			{
				if (Seated(userNum))
				{
					result.Fail = EnterRoomFail(MakeRoomFailAlready, false);
					return result;
				}
				GameRoom found;
				if (!Rooms.TryGetValue(roomId, out found) || found.Seats.Count == 0)
				{
					found = new GameRoom
					{
						Id = roomId,
						Kind = kind,
						Max = max,
						Title = title,
						Password = "",
						Map = map,
						Master = 0
					};
					Rooms[roomId] = found;
				}
				if (found.Seats.Count >= found.Max)
				{
					result.Fail = EnterRoomFail(EnterRoomFull, true);
					return result;
				}
				while (HasIndex(found, index))
				{
					index++;
				}
				foreach (Seat seat in found.Seats)
				{
					others.Add(seat.UserNum);
					present.Add(seat.Card);
				}
				card = NewRoomUser(userNum, name, me, index, session.UdpBlock);
				found.Seats.Add(new Seat { UserNum = userNum, Index = index, Name = name, Card = card });
				if (found.Seats.Count == 1)
				{
					found.Master = index;
				}
				master = found.Master;
			}
			session.GameIndex = index;
			session.RoomId = roomId;
			result.Self.Add(new TlWriter((int)TlAgentPacket.ROOM_USER_LIST_CLEAR).Payload());
			result.Self.Add(new TlWriter((int)TlAgentPacket.ROOM_USER_LIST).U32(0).Payload());
			if (kind == GameKindFarm)
			{
				result.Self.Add(EnterFarmRoom(roomId, title, me, index));
			}
			else
			{
				result.Self.Add(EnterMadeRoom(kind, max, roomId, map, title, false, false, false, index));
			}
			result.Self.AddRange(present);
			result.Self.Add(card);
			result.Self.Add(new TlWriter((int)TlAgentPacket.SET_MASTER).U8(master).Payload());
			result.Others = NotesFor(others, card);
			return result;
		}

		private static List<int> RoomUsers(int roomId)
		{
			List<int> users = new List<int>();
			if (roomId <= 0)
			{
				return users;
			}
			lock (RoomsLock)
			{
				GameRoom room;
				if (!Rooms.TryGetValue(roomId, out room))
				{
					return users;
				}
				foreach (Seat seat in room.Seats)
				{
					users.Add(seat.UserNum);
				}
			}
			return users;
		}

		private static byte[] RoomBrowser(int kind, int battle, int tread, int item, int page, int perPage)
		{
			if (perPage <= 0 || perPage > 30)
			{
				perPage = 9;
			}
			if (page < 0)
			{
				page = 0;
			}
			List<GameRoom> matched = new List<GameRoom>();
			lock (RoomsLock)
			{
				foreach (GameRoom room in Rooms.Values)
				{
					if (room.Kind == kind && room.Seats.Count > 0 && RoomMatches(room, battle, tread, item))
					{
						matched.Add(room);
					}
				}
			}
			matched.Sort(delegate(GameRoom a, GameRoom b) { return a.Id.CompareTo(b.Id); });
			int start = page * perPage;
			int shown = start < matched.Count ? Math.Min(perPage, matched.Count - start) : 0;
			TlWriter w = new TlWriter((int)TlAgentPacket.UNK_116).U8(kind).U32(0).U32(0).U8(shown);
			for (int i = 0; i < shown; i++)
			{
				GameRoom room = matched[start + i];
				string owner = room.Seats[0].Name ?? "";
				w.U32((uint)room.Id).String(room.Title ?? "").Bool(string.IsNullOrEmpty(room.Password)).String(owner);
				w.U8(room.Seats.Count).U8(room.Max).Bool(room.Team).Bool(room.Tread).Bool(room.Item).U16(room.Map).U32(0);
				w.Bool(false).Bool(false).Bool(false).Bool(false).U8(0).U8(0).U8(0).U32(0);
			}
			TlLog.Info("room list kind " + kind + " battle " + battle + " tread " + tread + " item " + item + " page " + page + " -> " + shown + "/" + matched.Count);
			return w.Payload();
		}

		private static void MarkRoom(Session session, bool park)
		{
			if (session.AccountId == null)
			{
				return;
			}
			int userNum = session.AccountId.Value;
			lock (SendLock)
			{
				if (park)
				{
					InPark.Add(userNum);
					InRoom.Remove(userNum);
				}
				else
				{
					InRoom.Add(userNum);
					InPark.Remove(userNum);
				}
			}
		}

		private static void ClearRoom(Session session)
		{
			if (session.AccountId == null)
			{
				return;
			}
			lock (SendLock)
			{
				InRoom.Remove(session.AccountId.Value);
				InPark.Remove(session.AccountId.Value);
			}
		}

		public static void BroadcastNotice(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			byte[] payload = new TlWriter((int)TlAgentPacket.NOTICE_MSG).String(text).Payload();
			Live[] copy;
			lock (SendLock)
			{
				copy = Lives.ToArray();
			}
			int sent = 0;
			foreach (Live live in copy)
			{
				try
				{
					Send(live.Stream, live.Link, payload);
					sent++;
				}
				catch (Exception ex)
				{
					TlLog.Warn("notice failed: " + ex.Message);
				}
			}
			TlLog.Info("notice to " + sent + " client(s): " + text);
		}

		private static void Send(NetworkStream stream, TlLink link, byte[] payload)
		{
			lock (SendLock)
			{
				byte[] frame = link.Wrap(payload);
				stream.Write(frame, 0, frame.Length);
			}
		}

		private static void Handle(NetworkStream stream, TlLink link, TlDb db, Session session, string remote, int op, TlReader reader)
		{
			if (op == (int)TlAgentPacket.KEEPALIVE || op == (int)TlAgentPacket.SEED_OFF_REQ)
			{
				if (op == (int)TlAgentPacket.SEED_OFF_REQ)
				{
					session.SeedConsumed = true;
				}
				return;
			}
			if (op == (int)TlAgentPacket.CLIENT_HASH_CHECK_REQ)
			{
				int constA = reader.U8();
				int b0 = reader.U8();
				int constB = b0 == 0xFF ? reader.U16() : b0;
				string version = reader.String();
				TlLog.Info("hash " + version + " constants " + constA + "/" + constB);
				Send(stream, link, new TlWriter((int)TlAgentPacket.CLIENT_HASH_CHECK_ACK).U8(1).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.LOGIN_REQ)
			{
				// snda copies the Password and DynamicPassword boxes into strings 2 and 3.
				string a = reader.String();
				string password = reader.String();
				string dynamicPassword = reader.String();
				bool rejected;
				int accountId = db.Login(a, password, out rejected);
				if (rejected)
				{
					TlLog.Info("login " + a + " rejected");
					Send(stream, link, new TlWriter((int)TlAgentPacket.LOGIN_ACK).U32(1).U32(0).Payload());
					return;
				}
				string shown = a;
				List<TlCharacter> mine = db.ListCharacters(accountId);
				if (mine.Count > 0 && !string.IsNullOrEmpty(mine[0].Nickname))
				{
					shown = mine[0].Nickname;
				}
				MarkOnline(session, stream, accountId, shown);
				TlLog.Info("login " + a + " auth=" + accountId + " dynamic=" + dynamicPassword.Length);
				TlWriter ack = new TlWriter((int)TlAgentPacket.LOGIN_ACK);
				ack.U32(0).U32(2).String("AuthNum").String(accountId.ToString()).String("UserID").String(a);
				Send(stream, link, ack.Payload());
				SendServerTime(stream, link, session, db, true);
				link.EnableObfuscation(Seed);
				session.SeedConsumed = true;
				Send(stream, link, ServerSettings(db));
				Send(stream, link, new TlWriter((int)TlAgentPacket.NOTIFY_MY_UDP_ACK).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.CHARACTER_LIST_REQ)
			{
				ulong auth = reader.U64();
				string user = reader.String();
				List<TlCharacter> chars = db.ListCharacters((int)auth);
				TlLog.Info("character list auth=" + auth + " user=" + user + " count=" + chars.Count);
				TlWriter w = new TlWriter((int)TlAgentPacket.CHARACTER_LIST_ACK).U16(0).U32(chars.Count);
				foreach (TlCharacter ch in chars)
				{
					w.U32(ch.CreatorSetNum).String(ch.Nickname).Bytes(Appearance(ch));
				}
				Send(stream, link, w.Payload());
				return;
			}
			if (op == (int)TlAgentPacket.CREATE_CHARACTER_REQ)
			{
				ulong auth = reader.U64();
				reader.String();
				string nickname = reader.String();
				int setNum = (int)reader.U32();
				TlLog.Info("create " + nickname + " set=" + setNum);
				if (!db.KnowsSet(setNum))
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.CREATE_NEW_CHARACTER_FAILED).U16(4).Payload());
					return;
				}
				int modelId;
				List<TlItem> grant = db.ResolveGrant(setNum, out modelId);
				if (grant == null)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.CREATE_NEW_CHARACTER_FAILED).U16(5).Payload());
					return;
				}
				TlCharacter created = db.CreateCharacter((int)auth, nickname, modelId, setNum, grant);
				if (created == null)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.CREATE_NEW_CHARACTER_FAILED).U16(1).Payload());
					return;
				}
				session.CharacterSetNum = setNum;
				Send(stream, link, new TlWriter((int)TlAgentPacket.CREATE_CHARACTER_OK).Bytes(Appearance(created)).U32(setNum).String(nickname).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.ENTER_WORLD_REQ)
			{
				reader.U8();
				int b0 = reader.U8();
				if (b0 == 0xFF)
				{
					reader.U16();
				}
				reader.String();
				string user = reader.String();
				ulong auth = reader.U64();
				int setNum = (int)reader.U32();
				session.AccountId = (int)auth;
				session.CharacterSetNum = setNum;
				TlCharacter me = Current(db, session);
				string name = me != null && !string.IsNullOrEmpty(me.Nickname) ? me.Nickname : user;
				MarkOnline(session, stream, (int)auth, name);
				TlLog.Info("enter world user=" + name + " set=" + setNum);
				SendServerTime(stream, link, session, db, false);
				int farmId = session.AccountId ?? 1;
				Send(stream, link, FarmInfo(farmId, name));
				Send(stream, link, ActiveFuncItems(db, session));
				return;
			}
			if (op == (int)TlAgentPacket.GET_NICKNAME_REQ)
			{
				TlCharacter me = Current(db, session);
				if (me != null)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.ENTER_NEW_NICKNAME_ACK).U32(0).String(me.Nickname).Payload());
				}
				return;
			}
			if (op == (int)TlAgentPacket.GET_EXP_REQ)
			{
				TlCharacter me = Current(db, session);
				Send(stream, link, new TlWriter((int)TlAgentPacket.GET_EXP_ACK).U32(0).U32(me != null ? me.Experience : 0).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.CLOCK_POLL)
			{
				Send(stream, link, new TlWriter((int)TlAgentPacket.UNK_44).U64((ulong)DateTime.UtcNow.ToFileTimeUtc()).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.GET_AVATAR_REQ)
			{
				// The select screen closes the client unless this model matches the portrait they clicked.
				TlCharacter me = Current(db, session, false);
				byte[] look = me != null ? Appearance(me) : new byte[34];
				Send(stream, link, new TlWriter((int)TlAgentPacket.GET_AVATAR_ACK).U32(0).Bytes(look).Payload());
				TlLog.Info("avatar model " + (me != null ? me.ModelId : 0));
				return;
			}
			if (op == (int)TlAgentPacket.GET_ITEM_LIST_REQ)
			{
				int avatar = reader.Remaining >= 2 ? reader.U16() : 0;
				int category = reader.Remaining >= 4 ? reader.I32() : -1;
				TlCharacter me = Current(db, session);
				List<TlItem> items = new List<TlItem>();
				if (me != null)
				{
					foreach (TlItem it in me.Items)
					{
						bool inBucket = it.FdCharacter == avatar || it.FdCharacter == 0;
						bool categoryOk = category < 0 || category == 8888 || it.FdPosition == category;
						if (inBucket && categoryOk)
						{
							items.Add(it);
						}
					}
				}
				Send(stream, link, ItemList(session, avatar, items));
				return;
			}
			if (op == (int)TlAgentPacket.GET_USER_PROFILE_REQ)
			{
				// Case 34 is complete at six bytes when every presence flag is 0.
				// A master=1 nickname probe left RemainSize=19 on a 49-byte frame.
				TlWriter w = new TlWriter((int)TlAgentPacket.GET_USER_PROFILE_OK);
				w.U8(0).U8(0).U8(0).U8(0).U8(0).U8(0);
				Send(stream, link, w.Payload());
				return;
			}
			if (op == (int)TlAgentPacket.UNK_115)
			{
				int kind = reader.Remaining > 0 ? reader.U8() : 0;
				int battle = reader.Remaining > 0 ? reader.U8() : 2;
				int tread = reader.Remaining > 0 ? reader.U8() : 2;
				int item = reader.Remaining > 0 ? reader.U8() : 2;
				int grade = reader.Remaining > 0 ? reader.U8() : 255;
				int page = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				int perPage = reader.Remaining > 0 ? reader.U8() : 9;
				Send(stream, link, RoomBrowser(kind, battle, tread, item, page, perPage));
				TlLog.Info("room list grade " + grade);
				return;
			}
			if (op == (int)TlAgentPacket.UNK_119)
			{
				Send(stream, link, EnterRoomFail(MakeRoomFailAlready, false));
				return;
			}
			if (op == (int)TlAgentPacket.JOIN_ROOM_REQ)
			{
				int roomId = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				string password = "";
				if (reader.Remaining >= 4)
				{
					try
					{
						password = reader.String();
					}
					catch (EndOfStreamException)
					{
						password = "";
					}
				}
				if (session.AccountId == null || session.AccountId.Value <= 0)
				{
					Send(stream, link, EnterRoomFail(EnterRoomMissing, false));
					TlLog.Info("join room " + roomId + " -> no account");
					return;
				}
				JoinResult joined = JoinRoom(session, Current(db, session), roomId, password);
				if (joined.Fail != null)
				{
					Send(stream, link, joined.Fail);
					TlLog.Info("join room " + roomId + " -> refused");
					return;
				}
				MarkRoom(session, false);
				foreach (byte[] packet in joined.Self)
				{
					Send(stream, link, packet);
				}
				SendRoomNotes(joined.Others);
				TlLog.Info("join room " + roomId + " index " + session.GameIndex + " users " + joined.Self.Count);
				return;
			}
			if (op == (int)TlAgentPacket.MYROOM_ENVELOPE_REQ)
			{
				HandleMyRoom(stream, link, db, session, remote, reader);
				return;
			}
			if (op == (int)TlAgentPacket.CHAOS_CHANNEL_GET_WAR_INFO_REQ)
			{
				Send(stream, link, new TlWriter((int)TlAgentPacket.CHAOS_CHANNEL_GET_WAR_INFO_OK).U32(0).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.UNK_199)
			{
				Send(stream, link, new TlWriter((int)TlAgentPacket.UNK_200).U16(0).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.GET_NEW_QUEST_ACCUMULABLE_POINT_REQ)
			{
				Send(stream, link, new TlWriter((int)TlAgentPacket.GET_NEW_QUEST_ACCUMULABLE_POINT_OK).U32(0).Payload());
				return;
			}
			for (int i = 0; i < EmptyU32Lists.Length; i += 2)
			{
				if (op == EmptyU32Lists[i])
				{
					Send(stream, link, new TlWriter(EmptyU32Lists[i + 1]).U32(0).Payload());
					return;
				}
			}
			if (op == (int)TlAgentPacket.GET_ACTIVE_FUNC_ITEM_REQ)
			{
				Send(stream, link, ActiveFuncItems(db, session));
				return;
			}
			for (int i = 0; i < ResultThenEmpty.Length; i += 2)
			{
				if (op == ResultThenEmpty[i])
				{
					Send(stream, link, new TlWriter(ResultThenEmpty[i + 1]).U32(0).U32(0).Payload());
					return;
				}
			}
			if (op == (int)TlAgentPacket.FARM_ENVELOPE_REQ)
			{
				HandleFarm(stream, link, db, session, remote, reader);
				return;
			}
			if (op == (int)TlAgentPacket.COMMUNITY_ENVELOPE)
			{
				HandleCommunity(stream, link, db, session, remote, reader);
				return;
			}
			if (op == (int)TlAgentPacket.SEND_SYSTEM_INFO)
			{
				List<string> fields = new List<string>();
				while (fields.Count < 9 && reader.Remaining >= 4)
				{
					fields.Add(reader.String());
				}
				TlLog.Info("system info " + string.Join(" | ", fields.ToArray()));
				return;
			}
			if (op == (int)TlAgentPacket.COURT_RESULT_REQ)
			{
				Send(stream, link, new TlWriter((int)TlAgentPacket.COURT_RESULT).Bool(false).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.QUIZ_MODE_GET_CATEGORY_REQ)
			{
				Send(stream, link, new TlWriter((int)TlAgentPacket.QUIZ_MODE_GET_CATEGORY).U8(1).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.GET_MY_TICKET_INFO_REQ)
			{
				Send(stream, link, new TlWriter((int)TlAgentPacket.GET_MY_TICKET_INFO_ACK).U32(0).U32(0).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.IS_EXIST_CONFIRMATION_PASSWORD_REQ)
			{
				Send(stream, link, new TlWriter((int)TlAgentPacket.IS_EXIST_CONFIRMATION_PASSWORD_OK).Bool(false).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.FAMILY_GET_FAMILY_INFO_REQ)
			{
				string nick = reader.Remaining >= 4 ? reader.String() : "";
				TlLog.Info("family info " + nick + " -> none");
				Send(stream, link, new TlWriter((int)TlAgentPacket.FAMILY_GET_FAMILY_INFO_FAILED).U16(0).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.SHOP_REQUEST_CHECK_CASH_REQ)
			{
				int cash = session.AccountId != null ? db.GetCash(session.AccountId.Value) : 0;
				Send(stream, link, new TlWriter((int)TlAgentPacket.SHOP_REQUEST_CHECK_CASH_ACK).U32(0).U32(cash).U32(0).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.START_NEW_QUEST_REQ)
			{
				int quest = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				TlLog.Info("start quest " + quest);
				Send(stream, link, new TlWriter((int)TlAgentPacket.START_NEW_QUEST_OK).U32(quest).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.INDIVIDUAL_RECORD_GET_REQ)
			{
				int recordType = reader.Remaining > 0 ? reader.U8() : -1;
				if (recordType == 1)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.INDIVIDUAL_RECORD_GET_ACK).U8(recordType).U32(0).Payload());
				}
				else
				{
					TlLog.Info("individual records type " + recordType + " has no traced reply");
				}
				return;
			}
			if (op == (int)TlAgentPacket.CHALLENGE_UPDATE_GROUP_REQ || op == (int)TlAgentPacket.CHALLENGE_UPDATE_MISSION_REQ)
			{
				int count = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				TlLog.Info(Name(op) + " updates " + count);
				return;
			}
			if (op == (int)TlAgentPacket.ROOM_ENVELOPE)
			{
				HandleRoom(stream, link, session, remote, reader);
				return;
			}
			if (op == (int)TlAgentPacket.UNK_291)
			{
				TlCharacter payer = Current(db, session);
				int money = payer != null ? payer.Money : 0;
				Send(stream, link, new TlWriter((int)TlAgentPacket.UNK_291).U32(money).Payload());
				TlLog.Info("money notify " + money);
				return;
			}
			if (op == (int)TlAgentPacket.UNK_74)
			{
				int flag = reader.Remaining > 0 ? reader.U8() : 0;
				TlLog.Info("character flag " + flag);
				return;
			}
			if (op == (int)TlAgentPacket.UNK_39 || op == (int)TlAgentPacket.ITEM_CACHE_INVALIDATE)
			{
				TlLog.Info(Name(op) + " notify bytes=" + reader.Remaining);
				return;
			}
			if (op == (int)TlAgentPacket.CHANNEL_LIST_REQ)
			{
				int gameKind = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				TlWriter w = new TlWriter((int)TlAgentPacket.CHANNEL_LIST_ACK).U32(gameKind).U32(3);
				for (int n = 1; n <= 3; n++)
				{
					w.U32(0).U32(0).U32(n).U32(0).U32(0).U32(0);
				}
				Send(stream, link, w.Payload());
				TlLog.Info("channel list kind " + gameKind + " -> 1,2,3");
				return;
			}
			if (op == (int)TlAgentPacket.UNK_376)
			{
				int quest = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				TlLog.Info("quest condition " + quest);
				Send(stream, link, new TlWriter((int)TlAgentPacket.UNK_377).U32(quest).Payload());
				return;
			}
			if (op == 278)
			{
				// count, then one 24-byte record per item. Dword 0 is the item the client looks up.
				int count = reader.Remaining >= 4 ? (int)reader.U32() : -1;
				if (count < 1 || count > 8 || reader.Remaining < count * 24)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.SHOP_BUY_PRODUCTS_TOGETHER_ACK).U32(1).Payload());
					TlLog.Info("shop buy rejected count=" + count + " bytes=" + reader.Remaining);
					return;
				}
				byte[][] rows = new byte[count][];
				int userNum = session.AccountId ?? 0;
				int money = 0;
				int cash = 0;
				bool paid = true;
				for (int i = 0; i < count; i++)
				{
					rows[i] = reader.Bytes(24);
					int ticket = BitConverter.ToInt32(rows[i], 0);
					int granted;
					int days;
					if (userNum <= 0 || ticket <= 0 || !db.TryBuy(userNum, ticket, out money, out cash, out granted, out days))
					{
						paid = false;
						TlLog.Info("shop buy item " + ticket + " refused");
						break;
					}
					byte[] id = BitConverter.GetBytes(granted);
					Buffer.BlockCopy(id, 0, rows[i], 0, 4);
					TlCatalogueItem wear = db.FindItem(granted);
					TlLog.Info("shop buy item " + ticket + " -> " + granted + " days=" + days + " tr=" + money + " cash=" + cash);
					if (days > 0)
					{
						Send(stream, link, ItemRecord(261, wear, granted, ItemDeadline(session, days), false));
					}
				}
				if (!paid)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.SHOP_BUY_PRODUCTS_TOGETHER_ACK).U32(1).Payload());
					return;
				}
				TlWriter w = new TlWriter((int)TlAgentPacket.SHOP_BUY_PRODUCTS_TOGETHER_ACK);
				w.U32(0).U32(count);
				for (int i = 0; i < count; i++)
				{
					w.Bytes(rows[i]);
				}
				w.U32(0).U8(0).U32(0);
				Send(stream, link, w.Payload());
				Send(stream, link, new TlWriter((int)TlAgentPacket.UNK_291).U32(money).Payload());
				Send(stream, link, new TlWriter((int)TlAgentPacket.SHOP_REQUEST_CHECK_CASH_ACK).U32(0).U32(cash).U32(0).Payload());
				return;
			}
			if (op == (int)TlAgentPacket.SET_ON_OFF_ITEM_REQ)
			{
				int item = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				bool on = reader.Remaining > 0 && reader.U8() != 0;
				if (session.AccountId != null && item > 0)
				{
					db.SetItemWorn(session.AccountId.Value, item, on);
					Send(stream, link, ActiveFuncItems(db, session));
					SendUserItemAttrs(stream, link, db, session.AccountId.Value);
				}
				Send(stream, link, new TlWriter((int)TlAgentPacket.UNK_41).U32(0).U32(item).U32(1).U32(0).Bool(on).Payload());
				TlLog.Info("wear item " + item + " on=" + (on ? 1 : 0));
				return;
			}
			if (op == 187)
			{
				int map = reader.Remaining >= 2 ? reader.U16() : 0;
				int price;
				int life;
				if (!db.TryMiniGameOffer(map, out price, out life))
				{
					TlLog.Info("minigame map " + map + " missing in tblminigameinfo");
					return;
				}
				// a6 is the TR price, a8 is the life the hit counter starts from. a3 stays 0 so the check uses TR.
				Send(stream, link, new TlWriter((int)TlAgentPacket.MINIGAME_GET_MAP_INFO_ACK)
					.U32(0).U16(map).Bool(false).U16(0).U16(0)
					.U32(price).U32(0).U32(life).U32(0).U32(0).U32(0).U32(0).Payload());
				TlLog.Info("minigame map " + map + " price " + price + " life " + life);
				return;
			}
			if (op == 195)
			{
				int id = reader.Remaining >= 2 ? reader.U16() : 0;
				Send(stream, link, new TlWriter((int)TlAgentPacket.UNK_196).U32(0).U32(0).Payload());
				TlLog.Info("minigame info " + id);
				return;
			}
			if (op == 189)
			{
				int id = reader.Remaining >= 2 ? reader.U16() : 0;
				bool flag = reader.Remaining > 0 && reader.U8() != 0;
				int price;
				int life;
				if (!db.TryMiniGameOffer(id, out price, out life))
				{
					Send(stream, link, new TlWriter(190).U32(1).Payload());
					TlLog.Info("minigame start " + id + " missing in tblminigameinfo");
					return;
				}
				int money;
				if (session.AccountId == null || !db.TrySpendTr(session.AccountId.Value, price, out money))
				{
					Send(stream, link, new TlWriter(190).U32(1).Payload());
					TlLog.Info("minigame start " + id + " refused, need " + price + " TR");
					return;
				}
				// Result 0 is the start path: u16, u32, u32, u16. State 7 is already set when the client sends this.
				Send(stream, link, new TlWriter(190).U32(0).U16(id).U32(0).U32(0).U16(0).Payload());
				Send(stream, link, new TlWriter((int)TlAgentPacket.UNK_291).U32(money).Payload());
				session.MiniMap = id;
				TlLog.Info("minigame start " + id + " flag=" + (flag ? 1 : 0) + " price=" + price + " life=" + life + " tr=" + money);
				return;
			}
			if (op == 191)
			{
				TlLog.Info("minigame life tick");
				return;
			}
			if (op == 192)
			{
				int tier = reader.Remaining > 0 ? reader.U8() : 0;
				TlLog.Info("minigame score tier " + tier);
				return;
			}
			if (op == 193)
			{
				// Game over: elapsed ms, then the score at the life object. Result 0 plus three u32s opens the score screen.
				int elapsed = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				int score = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				int map = session.MiniMap;
				int best;
				session.MiniScores.TryGetValue(map, out best);
				if (score > best)
				{
					session.MiniScores[map] = score;
					best = score;
				}
				Send(stream, link, new TlWriter((int)TlAgentPacket.MINI_GAME_OVER_FAILED).U32(0).U32(best).U32(0).U32(score).Payload());
				TlLog.Info("minigame over map " + map + " score " + score + " best " + best + " ms " + elapsed);
				return;
			}
			if (op == (int)TlAgentPacket.MINIGAME_GET_RANK_REQ)
			{
				int board = reader.Remaining > 0 ? reader.U8() : 0;
				int id = reader.Remaining >= 2 ? reader.U16() : 0;
				TlCharacter me = Current(db, session);
				string name = me != null ? me.Nickname : "";
				int best;
				session.MiniScores.TryGetValue(id, out best);
				TlWriter rank = new TlWriter((int)TlAgentPacket.MINIGAME_GET_RANK_ACK).U32(0).U16(id).U32(0);
				if (name.Length > 0)
				{
					rank.U32(1).String(name).U32(best);
				}
				else
				{
					rank.U32(0);
				}
				rank.U32(0).U32(0);
				Send(stream, link, rank.Payload());
				TlLog.Info("minigame rank " + id + " board " + board + " score " + best);
				return;
			}
			if (op == 260)
			{
				int item = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				TlCatalogueItem desc = db.FindItem(item);
				int shown = desc != null && desc.GrantItemNum > 0 ? desc.GrantItemNum : item;
				TlCatalogueItem wear = db.FindItem(shown) ?? desc;
				int days = desc != null && desc.LeaseDays > 0 ? desc.LeaseDays : StarterLeaseDays;
				Send(stream, link, ItemRecord(261, wear, shown, ItemDeadline(session, days), false));
				TlLog.Info("item record " + item + " -> " + shown);
				return;
			}
			if (op == 465)
			{
				int machine = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				session.CapsuleMachine = machine;
				Send(stream, link, CapsuleStock(db, machine));
				TlLog.Info("capsule machine " + machine + " prizes=" + db.CapsulePrizes(machine).Count);
				return;
			}
			if (op == (int)TlAgentPacket.UNK_292)
			{
				int kind = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				string machineText = reader.Remaining >= 4 ? reader.String() : "";
				string slotText = reader.Remaining >= 4 ? reader.String() : "";
				if (reader.Remaining >= 4)
				{
					reader.String();
				}
				if (kind == 3 && reader.Remaining >= 4)
				{
					reader.String();
				}
				if (kind == 3)
				{
					int requested;
					int.TryParse(slotText, out requested);
					int userNum = session.AccountId ?? 0;
					TlMixResult mix = db.MixAlchemist(userNum, requested);
					if (!mix.Ok)
					{
						Send(stream, link, new TlWriter(314).U32(79).U8(mix.Reason).Payload());
						TlLog.Info("alchemist mix " + requested + " fail " + mix.Reason);
						return;
					}
					TlWriter mixed = new TlWriter(314).U32(0).U32(mix.ItemNum).U32(mix.Attrs.Count);
					foreach (KeyValuePair<int, int> row in mix.Attrs)
					{
						mixed.U16(row.Key).U32(row.Value);
					}
					Send(stream, link, mixed.Payload());
					TlLog.Info("alchemist mix " + requested + " -> " + mix.ItemNum + " attrs " + mix.Attrs.Count);
					return;
				}
				int machine;
				int.TryParse(machineText, out machine);
				List<TlCapsulePrize> prizes = db.CapsulePrizes(machine);
				int slot;
				int.TryParse(slotText, out slot);
				TlCapsulePrize prize = null;
				foreach (TlCapsulePrize row in prizes)
				{
					if (row.Index == slot)
					{
						prize = row;
						break;
					}
				}
				if (prize == null && prizes.Count > 0)
				{
					prize = prizes[0];
				}
				int listed = prize != null ? prize.ItemNum : 0;
				TlCatalogueItem ticket = db.FindItem(listed);
				int days = ticket != null && ticket.LeaseDays > 0 ? ticket.LeaseDays : StarterLeaseDays;
				int stored = listed;
				if (session.AccountId != null && listed > 0)
				{
					stored = db.Grant(session.AccountId.Value, listed);
				}
				TlCatalogueItem wear = db.FindItem(stored);
				session.CapsuleItem = listed;
				session.CapsuleMachine = machine;
				if (listed > 0)
				{
					db.TakeCapsule(machine, listed);
				}
				TlCharacter wearer = Current(db, session);
				int closet = wear != null && wear.FdCharacter != 0 ? wear.FdCharacter : (wearer != null ? wearer.ModelId : 0);
				// 468's second dword is the item. 471 closes the machine, so it has to wait until the pop animation finishes.
				Send(stream, link, new TlWriter(468).U32(0).U32(listed).Payload());
				if (machine > 0)
				{
					Send(stream, link, CapsuleStock(db, machine));
				}
				if (stored > 0)
				{
					Send(stream, link, ItemRecord(261, wear, stored, ItemDeadline(session, days), false, closet));
				}
				TlLog.Info("capsule pull machine " + machineText + " item " + listed + " stored " + stored);
				return;
			}
			if (op == 470)
			{
				string who = reader.Remaining >= 4 ? reader.String() : "";
				string memo = reader.Remaining >= 4 ? reader.String() : "";
				int item = session.CapsuleItem;
				if (item > 0)
				{
					Send(stream, link, new TlWriter(471).Bool(false).U32(item).Payload());
					if (session.CapsuleMachine > 0)
					{
						Send(stream, link, CapsuleStock(db, session.CapsuleMachine));
					}
					TlCatalogueItem wear = db.FindItem(item);
					TlCharacter wearer = Current(db, session);
					int closet = wear != null && wear.FdCharacter != 0 ? wear.FdCharacter : (wearer != null ? wearer.ModelId : 0);
					int days = wear != null && wear.LeaseDays > 0 ? wear.LeaseDays : StarterLeaseDays;
					Send(stream, link, ItemRecord(261, wear, item, ItemDeadline(session, days), false, closet));
				}
				TlLog.Info("capsule receive " + who + " item " + item + " memo " + memo.Length);
				return;
			}
			if (op == (int)TlAgentPacket.GET_QUEST_INFO_EX_REQ)
			{
				Send(stream, link, new TlWriter((int)TlAgentPacket.GET_QUEST_INFO_EX_OK).U32(0).Payload());
				TlLog.Info("quest info -> none");
				return;
			}
			if (op == (int)TlAgentPacket.ANNIVERSARY_OBJECT_VALUE_REQ)
			{
				int objectNum = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				Send(stream, link, new TlWriter((int)TlAgentPacket.ANNIVERSARY_OBJECT_VALUE_OK).U32(0).U32(0).Payload());
				TlLog.Info("anniversary object " + objectNum + " -> none");
				return;
			}
			if (op == (int)TlAgentPacket.MAKE_ROOM_REQ)
			{
				int gameKind;
				int maxUsers;
				string title;
				string password;
				bool team;
				bool tread;
				bool item;
				uint marker;
				uint special;
				try
				{
					gameKind = reader.U8();
					maxUsers = reader.U8();
					title = reader.String();
					password = reader.String();
					team = reader.U8() != 0;
					tread = reader.U8() != 0;
					item = reader.U8() != 0;
					marker = reader.U32();
					special = reader.U32();
				}
				catch (EndOfStreamException)
				{
					TlLog.Info("make room truncated");
					Send(stream, link, new TlWriter((int)TlAgentPacket.MAKE_ROOM_FAIL).U32(MakeRoomFailOther).Payload());
					return;
				}
				TlLog.Info("make room kind " + gameKind + " max " + maxUsers + " title " + title.Length + " password " + password.Length + " team " + (team ? 1 : 0) + " tread " + (tread ? 1 : 0) + " item " + (item ? 1 : 0) + " marker " + marker.ToString("X8") + " special " + special);
				if (reader.Remaining != 0 || session.AccountId == null || session.AccountId.Value <= 0 || maxUsers == 0 || marker != 0xFFFFFFFFu || special != 0 || gameKind < 0 || gameKind >= GameKindFarm)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.MAKE_ROOM_FAIL).U32(MakeRoomFailOther).Payload());
					TlLog.Info("make room rejected");
					return;
				}
				if (session.GameIndex != null)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.MAKE_ROOM_FAIL).U32(MakeRoomFailAlready).Payload());
					TlLog.Info("make room rejected already in room");
					return;
				}
				int roomId = session.AccountId.Value;
				TlCharacter me = Current(db, session);
				string name = me != null ? me.Nickname : "";
				byte[] card = NewRoomUser(roomId, name, me, 0, session.UdpBlock);
				GameRoom created = new GameRoom
				{
					Id = roomId,
					Kind = gameKind,
					Max = maxUsers,
					Title = title,
					Password = password,
					Team = team,
					Tread = tread,
					Item = item,
					Map = DefaultRaceMap
				};
				List<RoomNote> replaced = OpenRoom(roomId, created, card, name);
				session.GameIndex = 0;
				session.RoomId = roomId;
				MarkRoom(session, false);
				Send(stream, link, new TlWriter((int)TlAgentPacket.ROOM_USER_LIST_CLEAR).Payload());
				Send(stream, link, new TlWriter((int)TlAgentPacket.ROOM_USER_LIST).U32(0).Payload());
				Send(stream, link, EnterMadeRoom(gameKind, maxUsers, roomId, DefaultRaceMap, title, team, tread, item, 0));
				Send(stream, link, card);
				// Without this the client leaves the master flag clear, so a created room looks like a join.
				Send(stream, link, new TlWriter((int)TlAgentPacket.SET_MASTER).U8(0).Payload());
				SendRoomNotes(replaced);
				TlLog.Info("room " + roomId + " created kind " + gameKind + " map " + DefaultRaceMap);
				return;
			}
			if (op == (int)TlAgentPacket.ENTER_CHANNEL_REQ)
			{
				// u32, u32, u32 gameKind, u8. Park is kind 47 and loads map 33.
				uint field0 = reader.Remaining >= 4 ? reader.U32() : 0;
				uint field1 = reader.Remaining >= 4 ? reader.U32() : 0;
				int kind = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				int extra = reader.Remaining > 0 ? reader.U8() : 0;
				TlCharacter me = Current(db, session);
				int userNum = session.AccountId ?? 0;
				if (userNum > 0)
				{
					SendRoomNotes(TakeSeat(userNum));
				}
				int maxUsers = kind == ParkKind ? ParkMaxPlayers : FarmMaxPlayers;
				int map = kind == ParkKind ? ParkMap : 0;
				int sharedId = 900000 + kind;
				string title = kind == ParkKind ? "Park" : "Channel";
				JoinResult joined = EnterShared(session, me, sharedId, kind, maxUsers, map, title);
				if (joined.Fail != null)
				{
					Send(stream, link, joined.Fail);
					TlLog.Info("enter channel kind " + kind + " rejected");
					return;
				}
				MarkRoom(session, kind == ParkKind);
				foreach (byte[] pkt in joined.Self)
				{
					Send(stream, link, pkt);
				}
				SendRoomNotes(joined.Others);
				TlLog.Info("enter channel kind " + kind + " index " + (session.GameIndex ?? 0) + " map " + map + " fields " + field0 + "," + field1 + "," + extra);
				return;
			}
			if (op == (int)TlAgentPacket.GET_USER_ITEM_ATTR_REQ)
			{
				Send(stream, link, ActiveFuncItems(db, session));
				SendUserItemAttrs(stream, link, db, session.AccountId ?? 0);
				return;
			}
			if (op == (int)TlAgentPacket.LEAVE_ROOM_REQ)
			{
				int flag = reader.Remaining > 0 ? reader.U8() : -1;
				int index = session.GameIndex ?? 0;
				int userNum = session.AccountId ?? 0;
				session.GameIndex = null;
				session.RoomId = 0;
				ClearRoom(session);
				Send(stream, link, new TlWriter((int)TlAgentPacket.WAIT_PLAYER_OUT).U8(index).Payload());
				if (userNum > 0)
				{
					SendRoomNotes(TakeSeat(userNum));
				}
				TlLog.Info("leave room flag " + flag + " index " + index);
				return;
			}
			if (op == 157)
			{
				int territory = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				// Case 158: u32 count, then a flag. Count 0 and flag 0 skips the event rows.
				Send(stream, link, new TlWriter((int)TlAgentPacket.CHAOS_CHANNEL_GET_EVENT_INFO_OK).U32(0).Bool(false).Payload());
				TlLog.Info("chaos event info territory " + territory + " -> none");
				return;
			}
			if (op == (int)TlAgentPacket.COMPLETE_NEW_QUEST_REQ)
			{
				int quest = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				Send(stream, link, new TlWriter((int)TlAgentPacket.COMPLETE_NEW_QUEST_OK).U32(quest).Payload());
				TlLog.Info("complete quest " + quest);
				return;
			}
			if (op == 85)
			{
				int code = reader.Remaining > 0 ? reader.U8() : -1;
				TlLog.Info("exit question " + code);
				return;
			}
			if (op == 282)
			{
				int page = reader.Remaining >= 2 ? reader.U16() : 1;
				int pageSize = reader.Remaining >= 2 ? reader.U16() : 5;
				List<TlGift> gifts = db.Gifts(session.AccountId ?? 0, page, pageSize);
				TlWriter box = new TlWriter(283).U32(0).U16(page).U16(pageSize).U16(gifts.Count);
				foreach (TlGift gift in gifts)
				{
					box.U32(gift.Id).String(gift.Sender).U32(db.GiftShownItem(gift.ItemNum)).U64(GiftDate(gift.SentAt)).String(gift.Memo);
				}
				Send(stream, link, box.Payload());
				TlLog.Info("gift box page " + page + " size " + pageSize + " -> " + gifts.Count);
				return;
			}
			if (op == (int)TlAgentPacket.SHOP_ACCEPT_GIFT_REQ)
			{
				int giftId = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				TlGift gift = db.FindGift(session.AccountId ?? 0, giftId);
				if (gift == null)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.SHOP_ACCEPT_GIFT_ACK).U32(1).Payload());
					TlLog.Info("gift accept " + giftId + " -> missing");
					return;
				}
				int stored = db.Grant(session.AccountId.Value, gift.ItemNum);
				db.DeleteGift(session.AccountId.Value, gift.Id);
				Send(stream, link, new TlWriter((int)TlAgentPacket.SHOP_ACCEPT_GIFT_ACK).U32(0).U32((uint)stored).Payload());
				TlCatalogueItem wear = db.FindItem(stored);
				TlCharacter wearer = Current(db, session);
				int closet = wear != null && wear.FdCharacter != 0 ? wear.FdCharacter : (wearer != null ? wearer.ModelId : 0);
				int days = wear != null && wear.LeaseDays > 0 ? wear.LeaseDays : StarterLeaseDays;
				Send(stream, link, ItemRecord(261, wear, stored, ItemDeadline(session, days), false, closet));
				TlLog.Info("gift accept " + gift.Id + " item " + gift.ItemNum + " -> " + stored);
				return;
			}
			if (op == (int)TlAgentPacket.UNK_323)
			{
				int kind = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				Send(stream, link, new TlWriter((int)TlAgentPacket.DIVINATION_CHECK_FREE_FAILED).U32(0).U32(kind).Bool(true).Payload());
				TlLog.Info("fortune check kind " + kind + " -> free");
				return;
			}
			if (op == 325)
			{
				int kind = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				int req = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				if (reader.Remaining >= 4)
				{
					reader.String();
				}
				int buffKind = kind >= 1 && kind <= 3 ? kind : 1;
				int item = FortuneItem(buffKind);
				uint limit = 15u * 60u * 1000u;
				ulong got = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				TlWriter result = new TlWriter((int)TlAgentPacket.DIVINATION_RESULT_FAILED);
				result.U32(0).U32((uint)buffKind).U32(1);
				result.U32((uint)item).U32(limit).U64(got);
				result.U32(2);
				result.U16(61).U32(FloatBits(buffKind));
				result.U16(78).U32(FloatBits(buffKind));
				Send(stream, link, result.Payload());
				TlLog.Info("fortune result kind " + kind + " req " + req + " item " + item);
				return;
			}
			if (op == (int)TlAgentPacket.LICENSE_GET_LICENSE_MISSION_INFO_REQ)
			{
				if (reader.Remaining != 4)
				{
					TlLog.Info("license mission info truncated");
					return;
				}
				int type = (int)reader.U32();
				if (!db.HasLicense(type))
				{
					TlLog.Info("license mission info unknown type " + type);
					return;
				}
				int marked = SendLicenseClears(stream, link, db, session.AccountId ?? 0, type);
				TlLog.Info("license mission info type " + type + " -> " + marked);
				return;
			}
			if (op == (int)TlAgentPacket.LICENSE_START_GAME_REQ)
			{
				if (reader.Remaining != 8)
				{
					TlLog.Info("license start truncated");
					return;
				}
				int type = (int)reader.U32();
				int mission = (int)reader.U32();
				int result = db.HasLicenseMission(type, mission) ? 0 : LicenseStartFailed;
				if (result == 0)
				{
					session.LicenseType = type;
					session.LicenseMission = mission;
				}
				Send(stream, link, new TlWriter((int)TlAgentPacket.LICENSE_START_GAME_ACK).U32(result).Payload());
				TlLog.Info("license start type " + type + " mission " + mission + " -> " + result);
				return;
			}
			if (op == (int)TlAgentPacket.LICENSE_GOAL_IN_NORMAL_END_REQ)
			{
				if (reader.Remaining < 4)
				{
					TlLog.Info("license goal-in truncated");
					return;
				}
				int count = (int)reader.U32();
				if (count < 0 || reader.Remaining != count * 8)
				{
					TlLog.Info("license goal-in malformed");
					return;
				}
				int point = 0;
				for (int i = 0; i < count; i++)
				{
					int key = (int)reader.U32();
					int value = (int)reader.U32();
					if (value > point)
					{
						point = value;
					}
					TlLog.Info("license goal-in " + key + "=" + value);
				}
				int lic = session.LicenseType;
				int mission = session.LicenseMission;
				if (lic == 0 || session.AccountId == null || !db.HasLicenseMission(lic, mission))
				{
					TlLog.Info("license goal-in with no started mission");
					return;
				}
				db.SaveLicenseClear(session.AccountId.Value, lic, mission, point);
				// Result 0, then six scalars. The fourth must be 1 or 2 or the client
				// does not OR the license bit. Reward class 0 adds no money or exp.
				TlWriter done = new TlWriter((int)TlAgentPacket.LICENSE_GOAL_IN_NORMAL_END_ACK);
				done.U32(0).U32((uint)lic).U32((uint)mission).U32((uint)point).U32(1).U32(0).U32(0).U32(0);
				Send(stream, link, done.Payload());
				SendLicenseClears(stream, link, db, session.AccountId.Value, lic);
				TlLog.Info("license clear type " + lic + " mission " + mission + " point " + point);
				return;
			}
			if (op == (int)TlAgentPacket.TIMEATTACK_GET_MAP_INFO_REQ)
			{
				int map = reader.Remaining >= 2 ? reader.U16() : 0;
				List<KeyValuePair<string, int>> ranks = db.TimeAttackRanks(map);
				TlWriter board = new TlWriter((int)TlAgentPacket.TIMEATTACK_GET_MAP_INFO_ACK).U32(0).U16(0).U16(map).U8(ranks.Count);
				foreach (KeyValuePair<string, int> row in ranks)
				{
					board.String(row.Key).U32(row.Value);
				}
				Send(stream, link, board.Payload());
				TlLog.Info("time attack map " + map + " ranks " + ranks.Count);
				return;
			}
			if (op == (int)TlAgentPacket.TIMEATTACK_START_GAME_REQ)
			{
				int map = reader.Remaining >= 2 ? reader.U16() : 0;
				session.TimeAttackMap = map;
				Send(stream, link, new TlWriter((int)TlAgentPacket.TIMEATTACK_START_GAME_ACK).U32(0).U16(map).Payload());
				TlLog.Info("time attack start " + map);
				return;
			}
			if (op == (int)TlAgentPacket.UNK_399)
			{
				if (reader.Remaining != 4)
				{
					TlLog.Info("time attack finish truncated");
					return;
				}
				int lap = (int)reader.U32();
				int map = session.TimeAttackMap;
				TlCharacter me = Current(db, session);
				string name = me != null ? me.Nickname : "";
				if (session.AccountId != null && map > 0)
				{
					db.SaveTimeAttack(session.AccountId.Value, map, lap, name);
				}
				Send(stream, link, new TlWriter(400).Payload());
				TlLog.Info("time attack finish map " + map + " lap " + lap);
				return;
			}
			if (op == (int)TlAgentPacket.GET_USER_INFO_REQ)
			{
				string asked = "";
				if (reader.Remaining >= 4)
				{
					try
					{
						asked = reader.String();
					}
					catch (EndOfStreamException)
					{
						asked = "";
					}
				}
				Send(stream, link, UserInfo(db, asked));
				TlLog.Info("user info " + asked);
				return;
			}
			if (op == (int)TlAgentPacket.NOTIFY_MY_UDP_REQ)
			{
				if (reader.Remaining < 48)
				{
					TlLog.Info("udp report short bytes=" + reader.Remaining);
					return;
				}
				session.UdpBlock = reader.Bytes(48);
				TlLog.Info("udp report " + (session.AccountId ?? 0));
				return;
			}
			TlLog.Info("unhandled opcode " + op + " (" + Name(op) + ") bytes=" + reader.Remaining + " hex=" + Hex(reader));
		}

		private static void HandleFarm(NetworkStream stream, TlLink link, TlDb db, Session session, string remote, TlReader reader)
		{
			int b0 = reader.U8();
			int farmOp = b0 == 0xFF ? reader.U16() : b0;
			TlCharacter me = Current(db, session);
			string name = me != null ? me.Nickname : "";
			int farmId = session.AccountId ?? 0;
			if (farmOp == (int)TlFarmPacket.FARM_JOIN_ROOM_REQ)
			{
				uint unique = reader.U32();
				if (farmId <= 0 || unique != (uint)farmId)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.ENTER_ROOM_ACK).U32(63).U8(1).Payload());
					return;
				}
				if (farmId > 0)
				{
					SendRoomNotes(TakeSeat(farmId));
				}
				JoinResult joined = EnterShared(session, me, 900000 + GameKindFarm, GameKindFarm, FarmMaxPlayers, 0, name);
				if (joined.Fail != null)
				{
					Send(stream, link, joined.Fail);
					TlLog.Info("farm join rejected");
					return;
				}
				foreach (byte[] pkt in joined.Self)
				{
					Send(stream, link, pkt);
				}
				SendRoomNotes(joined.Others);
				TlLog.Info("farm join index " + (session.GameIndex ?? 0));
				return;
			}
			if (farmOp == (int)TlFarmPacket.FARM_ROOM_LIST_REQ || farmOp == (int)TlFarmPacket.FARM_ROOM_LIST_POLL)
			{
				Send(stream, link, FarmWrap((int)TlFarmPacket.UNK_16, TlWriter.Bare(24).U32(0).U32(0).U32(0).U32(0).U32(0).Payload()));
				return;
			}
			if (farmOp == (int)TlFarmPacket.FARM_GET_FARM_MAP_INFO_REQ)
			{
				Send(stream, link, FarmWrap((int)TlFarmPacket.UNK_38, TlWriter.Bare(8).U32(0).Payload()));
				Send(stream, link, FarmWrap((int)TlFarmPacket.FARM_GET_FARM_MAP_INFO_FAILED, TlWriter.Bare(16).U32(0).U32(farmId).U32(FarmMapType).Payload()));
				return;
			}
			if (farmOp == (int)TlFarmPacket.FARM_GET_MY_FARM_POINT_REQ)
			{
				Send(stream, link, FarmWrap((int)TlFarmPacket.FARM_GET_MY_FARM_POINT_FAILED, TlWriter.Bare(12).U32(0).U32(0).Payload()));
				return;
			}
			if (farmOp == (int)TlFarmPacket.GET_FARM_ITEM_MSG_REQ)
			{
				Send(stream, link, FarmWrap((int)TlFarmPacket.GET_FARM_ITEM_MSG_OK, TlWriter.Bare(8).U32(0).Payload()));
				return;
			}
			if (farmOp == (int)TlFarmPacket.FARM_GET_FARM_ITEM_LIST_REQ)
			{
				Send(stream, link, FarmWrap((int)TlFarmPacket.FARM_GET_FARM_ITEM_LIST_BEGIN, new byte[0]));
				Send(stream, link, FarmWrap((int)TlFarmPacket.FARM_GET_FARM_ITEM_LIST_ALL, TlWriter.Bare(12).U32(0).U32(0).Payload()));
				return;
			}
			if (farmOp == (int)TlFarmPacket.FARM_GET_FARM_ITEM_ATTR_LIST_REQ)
			{
				uint echo = reader.Remaining >= 4 ? reader.U32() : (uint)farmId;
				TlWriter body = TlWriter.Bare(16).U32(0).U32((int)echo).Bool(false).Bool(false).U32(0);
				Send(stream, link, FarmWrap((int)TlFarmPacket.FARM_GET_FARM_ITEM_ATTR_LIST_FAILED, body.Payload()));
				return;
			}
			if (farmOp == 34)
			{
				int id = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				// Case 0x23 reads nothing and clears the map-object buffer.
				Send(stream, link, FarmWrap((int)TlFarmPacket.UNK_35, new byte[0]));
				TlLog.Info("farm clear map " + id);
				return;
			}
			if (farmOp == 45)
			{
				int id = reader.Remaining >= 4 ? (int)reader.U32() : 0;
				// Case 0x2E is two u32s. Result 0 calls the basic-set success handler.
				Send(stream, link, FarmWrap((int)TlFarmPacket.UNK_46, TlWriter.Bare(12).U32(0).U32(0).Payload()));
				TlLog.Info("farm basic set " + id + " -> ok");
				return;
			}
			TlLog.Info("unhandled farm opcode " + farmOp + " bytes=" + reader.Remaining + " hex=" + Hex(reader));
		}

		private static void HandleMyRoom(NetworkStream stream, TlLink link, TlDb db, Session session, string remote, TlReader reader)
		{
			int kind = reader.Remaining > 0 ? reader.U8() : -1;
			TlCharacter me = Current(db, session);
			if (kind == (int)TlMyRoomPacket.GET_CHARACTER_LIST_REQ)
			{
				List<int> nums = new List<int>();
				if (me != null && me.BaseModelId > 0)
				{
					nums.Add(me.BaseModelId);
				}
				if (me != null)
				{
					List<int> period = new List<int>();
					foreach (TlItem it in me.Items)
					{
						if (it.FdCharacter >= 201 && it.FdCharacter <= 205 && !period.Contains(it.FdCharacter))
						{
							period.Add(it.FdCharacter);
						}
					}
					period.Sort();
					foreach (int id in period)
					{
						if (!nums.Contains(id))
						{
							nums.Add(id);
						}
					}
				}
				TlWriter body = TlWriter.Bare(16).U8(nums.Count);
				foreach (int n in nums)
				{
					body.U16(n);
				}
				Send(stream, link, MyRoomAck((int)TlMyRoomPacket.GET_CHARACTER_LIST_ACK, body.Payload()));
				TlLog.Info("myroom kind 0 -> character list " + string.Join(",", nums.ConvertAll(n => n.ToString()).ToArray()));
				return;
			}
			if (kind == 5)
			{
				int user = session.AccountId ?? 0;
				List<KeyValuePair<int, int>> cards = db.CardCounts(user);
				TlWriter cardsBody = TlWriter.Bare(8 + cards.Count * 8).U32((uint)cards.Count);
				foreach (KeyValuePair<int, int> card in cards)
				{
					cardsBody.U32((uint)card.Key).U32((uint)card.Value);
				}
				Send(stream, link, MyRoomAck(6, cardsBody.Payload()));
				TlLog.Info("myroom kind 5 -> cards " + cards.Count);
				return;
			}
			if (kind == (int)TlMyRoomPacket.TROPHY_LIST_REQ)
			{
				Send(stream, link, MyRoomAck((int)TlMyRoomPacket.TROPHY_LIST_ACK, TlWriter.Bare(8).U32(0).Payload()));
				return;
			}
			if (kind == (int)TlMyRoomPacket.GET_CHARACTER_INFO_REQ)
			{
				session.PendingSuit = null;
				int requested = reader.Remaining >= 2 ? reader.U16() : 0;
				int show = requested > 0 ? requested : (me != null ? me.ModelId : 0);
				byte[] look = me != null ? Appearance(me, show) : new byte[34];
				Send(stream, link, MyRoomAck((int)TlMyRoomPacket.GET_CHARACTER_INFO_ACK, look));
				TlLog.Info("myroom kind 8 character " + show);
				return;
			}
			if (kind == (int)TlMyRoomPacket.SAVE_CHARACTER_INFO_REQ)
			{
				session.PendingSuit = reader.Remaining >= 34 ? reader.Bytes(34) : null;
				Send(stream, link, MyRoomAck((int)TlMyRoomPacket.SAVE_CHARACTER_INFO_ACK, null));
				TlLog.Info("suit held bytes=" + (session.PendingSuit != null ? session.PendingSuit.Length : 0));
				return;
			}
			if (kind == (int)TlMyRoomPacket.SAVE_BASIC_CHARACTER_REQ)
			{
				int requested = reader.Remaining >= 2 ? reader.U16() : 0;
				if (requested > 0)
				{
					RememberModel(db, session, me, requested);
				}
				if (session.PendingSuit != null)
				{
					ApplySuit(db, session, me, session.PendingSuit);
					session.PendingSuit = null;
				}
				Send(stream, link, MyRoomAck((int)TlMyRoomPacket.SAVE_BASIC_CHARACTER_ACK, null));
				TlLog.Info("myroom kind 14 character " + requested);
				return;
			}
			TlLog.Info("myroom kind " + kind + " bytes=" + reader.Remaining);
		}

		private static byte[] MyRoomAck(int kind, byte[] data)
		{
			TlWriter w = new TlWriter((int)TlAgentPacket.MYROOM_ENVELOPE_ACK).U8(kind);
			if (data != null && data.Length > 0)
			{
				w.Bytes(data);
			}
			return w.Payload();
		}

		private static void HandleCommunity(NetworkStream stream, TlLink link, TlDb db, Session session, string remote, TlReader reader)
		{
			int b0 = reader.U8();
			int sub = b0 == 0xFF ? reader.U16() : b0;
			if (sub == (int)TlAgentCommunitySubPacket.COMMUNITY_SERVER_LIST_REQ)
			{
				string host = string.IsNullOrEmpty(AdvertisedHost) ? "127.0.0.1" : AdvertisedHost;
				TlWriter list = TlWriter.Bare(32).U8(1).String(host).U32(TlCommunityHost.Port);
				Send(stream, link, Community((int)TlAgentCommunitySubPacket.COMMUNITY_SERVER_LIST_ACK, list.Payload()));
				TlLog.Info("community server " + host + ":" + TlCommunityHost.Port);
				return;
			}
			if (sub == (int)TlAgentCommunitySubPacket.UNK_31)
			{
				TlLog.Info("community pending friend requests -> none");
				return;
			}
			if (sub == (int)TlAgentCommunitySubPacket.MY_INFO_REQ)
			{
				TlCharacter me = Current(db, session);
				string name = me != null ? me.Nickname : "";
				int farmId = session.AccountId ?? 0;
				TlWriter body = TlWriter.Bare(64);
				body.String(name).Bool(true).Bool(false).U8(0).U16(0).String("");
				WriteFarm(body, farmId, name);
				body.Zeros(8).U8(0);
				Send(stream, link, Community((int)TlAgentCommunitySubPacket.MY_INFO_ACK, body.Payload()));
				return;
			}
			if (sub == (int)TlAgentCommunitySubPacket.GROUP_OPERATE_REQ)
			{
				int gop = reader.Remaining > 0 ? reader.U8() : 0;
				if (gop != 0)
				{
					TlLog.Info("community group op " + gop);
					return;
				}
				Send(stream, link, Community((int)TlAgentCommunitySubPacket.GROUP_OPERATE_ACK, TlWriter.Bare(8).U8(0).U32(0).Payload()));
				return;
			}
			if (sub == (int)TlAgentCommunitySubPacket.UNK_45)
			{
				Send(stream, link, Community((int)TlAgentCommunitySubPacket.UNK_46, TlWriter.Bare(8).String(" ").Payload()));
				return;
			}
			if (sub == (int)TlAgentCommunitySubPacket.UNK_47)
			{
				Send(stream, link, Community((int)TlAgentCommunitySubPacket.UNK_48, TlWriter.Bare(8).U32(0).Payload()));
				return;
			}
			TlLog.Info("community sub " + sub + " bytes=" + reader.Remaining + " hex=" + Hex(reader));
		}

		private static byte[] Community(int sub, byte[] fields)
		{
			TlWriter w = new TlWriter((int)TlAgentPacket.COMMUNITY_ENVELOPE);
			TlWriter op = TlWriter.Bare(4);
			TlOpcode.Write(op, sub);
			w.Bytes(op.Payload());
			if (fields != null && fields.Length > 0)
			{
				w.Bytes(fields);
			}
			return w.Payload();
		}

		private static void HandleRoom(NetworkStream stream, TlLink link, Session session, string remote, TlReader reader)
		{
			if (reader.Remaining < 1)
			{
				return;
			}
			int b0 = reader.U8();
			int sub = b0 == 0xFF ? reader.U16() : b0;
			if (sub == (int)TlAgentPacket.FARM_OBJECT_LOCK_INFO_REQ)
			{
				Send(stream, link, new TlWriter((int)TlAgentPacket.FARM_OBJECT_LOCK_INFO_ACK).U32(0).Payload());
				return;
			}
			if (sub == (int)TlAgentPacket.CHAT)
			{
				if (reader.Remaining < 2)
				{
					return;
				}
				int len = reader.U16();
				if (len <= 0 || reader.Remaining < len)
				{
					return;
				}
				byte[] buf = reader.Bytes(len);
				int index = session.GameIndex ?? 0;
				byte[] chat = new TlWriter((int)TlAgentPacket.CHAT).U8(index).U16(buf.Length).Bytes(buf).Payload();
				List<int> users = RoomUsers(session.RoomId);
				if (users.Count == 0)
				{
					Send(stream, link, chat);
				}
				else
				{
					SendRoomNotes(NotesFor(users, chat));
				}
				TlLog.Info("room chat index " + index + " bytes " + buf.Length + " -> " + (users.Count == 0 ? 1 : users.Count));
				return;
			}
			if (sub == 543)
			{
				if (reader.Remaining < 1)
				{
					return;
				}
				bool ready = reader.U8() != 0;
				int index = session.GameIndex ?? 0;
				byte[] status = new TlWriter((int)TlAgentPacket.READY_STATUS).U8(index).Bool(ready).Payload();
				List<int> users = RoomUsers(session.RoomId);
				if (users.Count == 0)
				{
					Send(stream, link, status);
				}
				else
				{
					SendRoomNotes(NotesFor(users, status));
				}
				TlLog.Info("room ready index " + index + " " + (ready ? 1 : 0));
				return;
			}
			if (sub == (int)TlAgentPacket.UNK_518 || sub == (int)TlAgentPacket.COUNTER_REPORT)
			{
				TlLog.Info("room sub " + sub + " report");
				return;
			}
			if (sub == (int)TlAgentPacket.CHANGE_MAP_REQ)
			{
				if (reader.Remaining != 2)
				{
					TlLog.Info("room map rejected bytes=" + reader.Remaining);
					return;
				}
				int map = reader.U16();
				byte[] changed = new TlWriter((int)TlAgentPacket.MAP_CHANGED).U16(map).Payload();
				Send(stream, link, changed);
				int userNum = session.AccountId ?? 0;
				List<int> others = new List<int>();
				lock (RoomsLock)
				{
					GameRoom room;
					if (session.RoomId > 0 && Rooms.TryGetValue(session.RoomId, out room))
					{
						room.Map = map;
						foreach (Seat seat in room.Seats)
						{
							if (seat.UserNum != userNum)
							{
								others.Add(seat.UserNum);
							}
						}
					}
				}
				SendRoomNotes(NotesFor(others, changed));
				TlLog.Info("room map " + map);
				return;
			}
			if (sub == 575)
			{
				if (reader.Remaining < 3)
				{
					TlLog.Info("room start truncated bytes=" + reader.Remaining);
					return;
				}
				int map = reader.U16();
				int mode = reader.U8();
				int index = session.GameIndex ?? 0;
				int master = -1;
				lock (RoomsLock)
				{
					GameRoom room;
					if (session.RoomId > 0 && Rooms.TryGetValue(session.RoomId, out room))
					{
						master = room.Master;
					}
				}
				if (master < 0 || index != master)
				{
					Send(stream, link, new TlWriter((int)TlAgentPacket.REUQEST_CANT_START_COUNTING_ACK).Payload());
					TlLog.Info("room start denied index " + index + " master " + master);
					return;
				}
				byte[] ack = new TlWriter((int)TlAgentPacket.REQUEST_START_COUNTING_ACK).Payload();
				List<int> users = RoomUsers(session.RoomId);
				if (users.Count == 0)
				{
					Send(stream, link, ack);
				}
				else
				{
					SendRoomNotes(NotesFor(users, ack));
				}
				TlLog.Info("room start counting map " + map + " mode " + mode + " -> " + (users.Count == 0 ? 1 : users.Count));
				return;
			}
			if (sub == 548)
			{
				return;
			}
			if (sub == 581)
			{
				if (reader.Remaining < 7)
				{
					TlLog.Info("room go truncated bytes=" + reader.Remaining);
					return;
				}
				int map = reader.U16();
				int mode = reader.U8();
				uint seed = reader.U32();
				int index = session.GameIndex ?? 0;
				int master = -1;
				lock (RoomsLock)
				{
					GameRoom room;
					if (session.RoomId > 0 && Rooms.TryGetValue(session.RoomId, out room))
					{
						master = room.Master;
						if (index == room.Master)
						{
							room.Loading = true;
							room.Loaded.Clear();
							room.Goals.Clear();
							room.ResultSent = false;
							if (map != 0)
							{
								room.Map = map;
							}
						}
					}
				}
				if (master < 0 || index != master)
				{
					TlLog.Info("room go denied index " + index + " master " + master);
					return;
				}
				byte[] loading = new TlWriter((int)TlAgentPacket.START_LOADING_ACK).U16(map).U32(seed).Payload();
				List<int> users = RoomUsers(session.RoomId);
				if (users.Count == 0)
				{
					Send(stream, link, loading);
				}
				else
				{
					SendRoomNotes(NotesFor(users, loading));
				}
				TlLog.Info("room loading map " + map + " mode " + mode + " seed " + seed + " -> " + (users.Count == 0 ? 1 : users.Count));
				return;
			}
			if (sub == 587)
			{
				int userNum = session.AccountId ?? 0;
				int index = session.GameIndex ?? 0;
				int roomId = session.RoomId;
				bool waiting = false;
				lock (RoomsLock)
				{
					GameRoom room;
					if (roomId > 0 && Rooms.TryGetValue(roomId, out room) && room.Loading)
					{
						room.Loaded.Add(userNum);
						waiting = true;
					}
				}
				if (!waiting)
				{
					return;
				}
				byte[] ended = new TlWriter((int)TlAgentPacket.END_LOADING).U8(index).Payload();
				List<int> users = RoomUsers(roomId);
				if (users.Count > 0)
				{
					SendRoomNotes(NotesFor(users, ended));
				}
				SendRoomNotes(FinishLoadNotes(roomId));
				TlLog.Info("room load end index " + index);
				return;
			}
			if (sub == 628)
			{
				if (reader.Remaining < 16)
				{
					TlLog.Info("room goal truncated bytes=" + reader.Remaining);
					return;
				}
				uint ms = reader.U32();
				reader.U32();
				reader.U32();
				uint goal = reader.U32();
				int index = session.GameIndex ?? 0;
				int userNum = session.AccountId ?? 0;
				int roomId = session.RoomId;
				byte[] note = new TlWriter(629).U8(index).U32(ms).U32(goal).Payload();
				List<int> users = RoomUsers(roomId);
				if (users.Count == 0)
				{
					Send(stream, link, note);
				}
				else
				{
					SendRoomNotes(NotesFor(users, note));
				}
				lock (RoomsLock)
				{
					GameRoom room;
					if (roomId > 0 && Rooms.TryGetValue(roomId, out room))
					{
						bool have = false;
						foreach (GoalMark one in room.Goals)
						{
							if (one.UserNum == userNum)
							{
								have = true;
								break;
							}
						}
						if (!have)
						{
							if (ms == 0)
							{
								ms = 3600000;
							}
							room.Goals.Add(new GoalMark { UserNum = userNum, Index = index, Milliseconds = ms });
						}
					}
				}
				TlLog.Info("room goal index " + index + " ms " + ms + " type " + goal);
				NoteGoalWindow(roomId);
				return;
			}
			TlLog.Info("room sub " + sub + " bytes=" + reader.Remaining);
		}

		private static byte[] FarmInfo(int unique, string name)
		{
			TlWriter body = TlWriter.Bare(64);
			body.U32(0);
			WriteFarm(body, unique, name);
			return FarmWrap((int)TlFarmPacket.FARM_GET_MY_FARM_INFO_OK, body.Payload());
		}

		private static void WriteFarm(TlWriter w, int unique, string name)
		{
			w.U32(unique).U32(0).U32(0).U32(FarmMapType).String(name).String("").U64(0).U64(0).Bool(false).Bool(false).U32(0).U32(0);
		}

		private static byte[] FarmWrap(int farmOp, byte[] fields)
		{
			TlWriter w = new TlWriter((int)TlAgentPacket.FARM_ENVELOPE_ACK);
			TlWriter op = TlWriter.Bare(4);
			TlOpcode.Write(op, farmOp);
			w.Bytes(op.Payload());
			if (fields != null && fields.Length > 0)
			{
				w.Bytes(fields);
			}
			return w.Payload();
		}

		private static int SendLicenseClears(NetworkStream stream, TlLink link, TlDb db, int userNum, int type)
		{
			List<KeyValuePair<int, int>> cleared = db.LicenseClears(userNum, type);
			TlWriter info = new TlWriter((int)TlAgentPacket.LICENSE_GET_LICENSE_MISSION_INFO_ACK).U32(0).U32((uint)type).U32(cleared.Count);
			foreach (KeyValuePair<int, int> row in cleared)
			{
				info.U32(row.Key).U32(row.Value);
			}
			Send(stream, link, info.Payload());
			return cleared.Count;
		}

		private static void WriteRoomGear(TlWriter w, TlCharacter me)
		{
			List<KeyValuePair<int, List<KeyValuePair<int, int>>>> attrs = me != null && Database != null
				? Database.UserItemAttrs(me.Id)
				: new List<KeyValuePair<int, List<KeyValuePair<int, int>>>>();
			HashSet<int> withAttr = new HashSet<int>();
			foreach (KeyValuePair<int, List<KeyValuePair<int, int>>> item in attrs)
			{
				withAttr.Add(item.Key);
			}
			List<TlItem> gear = new List<TlItem>();
			if (me != null && me.Items != null)
			{
				foreach (TlItem it in me.Items)
				{
					bool functional = it.FdPosition > 100 && it.FdPosition != 106;
					if (functional && (it.Equipped || withAttr.Contains(it.ItemNum)))
					{
						gear.Add(it);
					}
				}
			}
			w.U32(gear.Count);
			foreach (TlItem it in gear)
			{
				w.U16(0).U16(it.FdPosition).U16(it.FdItemKind).U32(it.ItemNum);
				w.U64(0).U32(2).U8(0).U8(0).U8(0).U8(0).U8(0).U8(1);
			}
			w.U32(attrs.Count);
			int attrCount = 0;
			foreach (KeyValuePair<int, List<KeyValuePair<int, int>>> item in attrs)
			{
				w.U32(item.Key).U32(item.Value.Count);
				foreach (KeyValuePair<int, int> row in item.Value)
				{
					w.U16(row.Key).U32(row.Value);
					attrCount++;
				}
			}
			TlLog.Info("room gear items " + gear.Count + " attrs " + attrCount);
		}

		private static byte[] EnterMadeRoom(int gameKind, int maxUsers, int roomId, int map, string title, bool team, bool tread, bool item, int myIndex)
		{
			TlWriter w = new TlWriter((int)TlAgentPacket.ENTER_ROOM_ACK);
			w.U32(0).U8(gameKind).U32(0).String(title).U8(maxUsers).String("").U32(0).U32(roomId).U8(myIndex).U16(map).U32(0);
			w.Bool(team).Bool(tread).Bool(item).U32(0).U32(0);
			return w.Payload();
		}

		private static byte[] EnterChannelRoom(int gameKind, int maxUsers, int roomId, int map, string name)
		{
			TlWriter w = new TlWriter((int)TlAgentPacket.ENTER_ROOM_ACK);
			w.U32(0).U8(gameKind).U32(0).String(name).U8(maxUsers).String("").U32(0).U32(roomId).U8(0).U16(map).U32(0);
			w.Bool(false).Bool(false).Bool(false).U32(0).U32(0);
			return w.Payload();
		}

		private static byte[] EnterFarmRoom(int farmId, string name, TlCharacter me, int myIndex)
		{
			TlWriter w = new TlWriter((int)TlAgentPacket.ENTER_ROOM_ACK);
			w.U32(0).U8(53).U32(0).String(name).U8(FarmMaxPlayers).String("").U32(0).U32(farmId).U8(myIndex).U16(0).U32(0);
			w.Bool(false).Bool(false).Bool(false).U32(0).U32(0);
			WriteFarm(w, farmId, name);
			w.U8(0).U8(0).U8(0).String("");
			return w.Payload();
		}

		private static byte[] NewRoomUser(int farmId, string name, TlCharacter me, int slot, byte[] udp)
		{
			byte[] look = me != null ? Appearance(me) : new byte[34];
			byte[] addr = new byte[48];
			if (udp != null && udp.Length == 48)
			{
				Buffer.BlockCopy(udp, 0, addr, 0, 48);
			}
			// Park and the minigame channels try a direct address for 60 seconds before relay.
			// Pointing every address at the relay makes that first send arrive immediately.
			byte[] relay = RelayAddress();
			Buffer.BlockCopy(relay, 0, addr, 0, 16);
			Buffer.BlockCopy(relay, 0, addr, 16, 16);
			Buffer.BlockCopy(relay, 0, addr, 32, 16);
			TlWriter w = new TlWriter((int)TlAgentPacket.NEW_ROOM_USER_ACK);
			w.U32(farmId).U8(slot).Bytes(addr).String(name).String("").U8(0).Bool(false).Bytes(look);
			// The dword after the appearance is the exp the room rank icon turns into a mark.
			int exp = me != null ? me.Experience : 0;
			w.U32(exp).U8(0).U8(0).U8(0).U32(0).Zeros(8).Zeros(88).U8(0);
			WriteRoomGear(w, me);
			WriteFarm(w, farmId, name);
			w.Bool(false);
			return w.Payload();
		}

		private static ulong ItemDeadline(Session session, int days)
		{
			ulong anchor = session.ClockAnchor != 0 ? session.ClockAnchor : (ulong)DateTime.UtcNow.ToFileTimeUtc();
			int span = days > 0 ? days : StarterLeaseDays;
			return anchor + (ulong)span * 86400000UL;
		}

		private static ulong ItemDeadlineFromExpire(Session session, long expireUnix)
		{
			ulong anchor = session.ClockAnchor != 0 ? session.ClockAnchor : (ulong)DateTime.UtcNow.ToFileTimeUtc();
			if (expireUnix <= 0)
			{
				return anchor + (ulong)StarterLeaseDays * 86400000UL;
			}
			DateTime clockAt = session.ClockAtUtc == default(DateTime) ? DateTime.UtcNow : session.ClockAtUtc;
			long elapsed = new DateTimeOffset(DateTime.SpecifyKind(clockAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
			long remain = expireUnix * 1000L - elapsed;
			if (remain < 1000L)
			{
				remain = 1000L;
			}
			return anchor + (ulong)remain;
		}

		private static ulong GiftDate(DateTime when)
		{
			if (when.Year < 1970)
			{
				return 0;
			}
			DateTime wall = DateTime.SpecifyKind(when, DateTimeKind.Utc);
			long unixMs = new DateTimeOffset(wall).ToUnixTimeMilliseconds();
			long shown = unixMs - 32400000L;
			return shown < 0 ? 0UL : (ulong)shown;
		}

		private static int FortuneItem(int kind)
		{
			if (kind == 2)
			{
				return 1868;
			}
			if (kind == 3)
			{
				return 1870;
			}
			return 1866;
		}

		private static uint FloatBits(int kind)
		{
			float value = kind;
			return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
		}

		private static void SendUserItemAttrs(NetworkStream stream, TlLink link, TlDb db, int userNum)
		{
			List<KeyValuePair<int, List<KeyValuePair<int, int>>>> attrs = db.UserItemAttrs(userNum);
			TlWriter attr = new TlWriter((int)TlAgentPacket.GET_USER_ITEM_ATTR_ACK).U32(0).U32(attrs.Count);
			int attrCount = 0;
			foreach (KeyValuePair<int, List<KeyValuePair<int, int>>> item in attrs)
			{
				attr.U32(item.Key).U32(item.Value.Count);
				foreach (KeyValuePair<int, int> row in item.Value)
				{
					attr.U16(row.Key).U32(row.Value);
					attrCount++;
				}
			}
			attr.U32(0);
			Send(stream, link, attr.Payload());
			TlLog.Info("item attr items " + attrs.Count + " attrs " + attrCount);
		}

		private static byte[] ItemRecord(int opcode, TlCatalogueItem desc, int itemNum, ulong deadline, bool worn, int character = -1)
		{
			int position = desc != null ? desc.FdPosition : 0;
			bool functional = position > 100 && position != 106;
			int shown = functional ? 0 : (character >= 0 ? character : (desc != null ? desc.FdCharacter : 0));
			TlWriter w = new TlWriter(opcode);
			w.U16(shown).U16(position).U16(0).U32(itemNum);
			w.U64(deadline).U32(2).U8(0).U8(0).U8(0).U8(0).U8(0).U8(functional || worn ? 1 : 0);
			return w.Payload();
		}

		private static byte[] CapsuleStock(TlDb db, int machine)
		{
			List<TlCapsulePrize> prizes = db.CapsulePrizes(machine);
			TlWriter list = new TlWriter(466).U32(machine).U32(prizes.Count);
			foreach (TlCapsulePrize prize in prizes)
			{
				int stock = prize.Count > 0 ? prize.Count : 0;
				int max = prize.Max > stock ? prize.Max : (stock > 0 ? stock : 1);
				list.U32(prize.Index).U32(prize.ItemNum).U32(stock).U32(max);
			}
			return list.Payload();
		}

		private static byte[] ActiveFuncItems(TlDb db, Session session)
		{
			TlCharacter me = Current(db, session);
			List<TlItem> items = me != null ? me.Items : new List<TlItem>();
			HashSet<int> withAttr = new HashSet<int>();
			if (me != null)
			{
				foreach (KeyValuePair<int, List<KeyValuePair<int, int>>> item in db.UserItemAttrs(me.Id))
				{
					withAttr.Add(item.Key);
				}
			}
			TlWriter w = new TlWriter((int)TlAgentPacket.GET_ACTIVE_FUNC_ITEM_ACK__77).U32(0).U32(items.Count);
			List<int> applied = new List<int>();
			foreach (TlItem it in items)
			{
				bool functional = it.FdPosition > 100 && it.FdPosition != 106;
				bool apply = functional && (it.Equipped || withAttr.Contains(it.ItemNum));
				int shown = functional ? 0 : it.FdCharacter;
				if (apply)
				{
					applied.Add(it.ItemNum);
				}
				w.U16(shown).U16(it.FdPosition).U16(it.FdItemKind).U32(it.ItemNum);
				w.U64(ItemDeadlineFromExpire(session, it.ExpireUnix)).U32(2).U8(0).U8(0).U8(0).U8(0).U8(0).U8(apply ? 1 : 0);
			}
			TlLog.Info("active func items " + items.Count + " applied " + string.Join(",", applied.ConvertAll(n => n.ToString()).ToArray()));
			return w.Payload();
		}

		private static byte[] ItemList(Session session, int avatarKind, List<TlItem> items)
		{
			TlWriter w = new TlWriter((int)TlAgentPacket.GET_ITEM_LIST_ACK).U16(avatarKind).U32(0).U16(items.Count);
			foreach (TlItem it in items)
			{
				bool functional = it.FdPosition > 100 && it.FdPosition != 106;
				int shown = functional ? 0 : (it.FdCharacter == 0 && avatarKind > 0 ? avatarKind : it.FdCharacter);
				w.U16(shown).U16(it.FdPosition).U16(0).U32(it.ItemNum);
				w.U64(ItemDeadlineFromExpire(session, it.ExpireUnix)).U32(2).U8(0).U8(0).U8(0).U8(0).U8(0).U8(functional || it.Equipped ? 1 : 0);
			}
			return w.Payload();
		}

		private static byte[] ServerSettings(TlDb db)
		{
			List<KeyValuePair<string, string>> rows = db.ClientSettings;
			TlWriter w = new TlWriter((int)TlAgentPacket.SERVER_SETTING_INFO).U32(rows.Count);
			foreach (KeyValuePair<string, string> row in rows)
			{
				w.String(row.Key).String(row.Value ?? "");
			}
			return w.Payload();
		}

		private static void SendServerTime(NetworkStream stream, TlLink link, Session session, TlDb db, bool withSeed)
		{
			TlCharacter me = Current(db, session);
			ulong clock = (ulong)DateTime.UtcNow.ToFileTimeUtc();
			if (session.ClockAnchor == 0)
			{
				session.ClockAnchor = clock;
				session.ClockAtUtc = DateTime.UtcNow;
			}
			TlWriter w = new TlWriter((int)TlAgentPacket.SERVER_TIME);
			w.U32((uint)(session.AccountId ?? 0));
			if (withSeed && !session.SeedConsumed)
			{
				w.I32(Seed);
			}
			int exp = me != null ? me.Experience : 0;
			w.U32(me != null ? me.Money : 0).U32(HellEntryLicense).U32(exp).U32(0);
			if (withSeed)
			{
				TlLog.Info("rank exp " + exp);
			}
			w.U32((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
			w.Zeros(88).U8(0).Zeros(8).U8(0).U8(0).U64(session.ClockAnchor);
			w.Bytes(RelayAddress());
			w.Bool(false).U32(0);
			Send(stream, link, w.Payload());
		}

		private static byte[] RelayAddress()
		{
			byte[] o = new byte[16];
			string host = string.IsNullOrEmpty(AdvertisedHost) ? "127.0.0.1" : AdvertisedHost;
			IPAddress ip;
			if (!IPAddress.TryParse(host, out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
			{
				ip = IPAddress.Parse("127.0.0.1");
			}
			byte[] addr = ip.GetAddressBytes();
			int port = RelayPort;
			o[0] = 2;
			o[2] = (byte)(port >> 8);
			o[3] = (byte)port;
			Buffer.BlockCopy(addr, 0, o, 4, 4);
			return o;
		}

		private static TlCharacter Current(TlDb db, Session session)
		{
			return Current(db, session, true);
		}

		private static TlCharacter Current(TlDb db, Session session, bool playedModel)
		{
			if (session.AccountId == null)
			{
				return null;
			}
			List<TlCharacter> list = db.ListCharacters(session.AccountId.Value);
			TlCharacter found = null;
			if (session.CharacterSetNum != null)
			{
				foreach (TlCharacter row in list)
				{
					if (row.CreatorSetNum == session.CharacterSetNum.Value)
					{
						found = row;
						break;
					}
				}
			}
			TlCharacter ch = found ?? (list.Count > 0 ? list[0] : null);
			if (ch == null || !playedModel)
			{
				return ch;
			}
			if (session.PlayedModel != null && session.PlayedModel.Value > 0)
			{
				ch.ModelId = session.PlayedModel.Value;
			}
			return ch;
		}

		private static void RememberModel(TlDb db, Session session, TlCharacter me, int model)
		{
			session.PlayedModel = model;
			if (me == null || session.AccountId == null)
			{
				return;
			}
			me.ModelId = model;
			foreach (TlItem it in me.Items)
			{
				if (it.FdPosition != 0)
				{
					continue;
				}
				bool on = it.FdCharacter == model;
				if (it.Equipped != on)
				{
					db.SetItemWorn(session.AccountId.Value, it.ItemNum, on);
					it.Equipped = on;
				}
			}
		}

		private static void ApplySuit(TlDb db, Session session, TlCharacter me, byte[] raw)
		{
			if (me == null || raw == null || raw.Length < 34 || session.AccountId == null)
			{
				return;
			}
			ushort[] words = new ushort[17];
			for (int i = 0; i < 17; i++)
			{
				words[i] = (ushort)(raw[i * 2] | (raw[i * 2 + 1] << 8));
			}
			int worn = 0;
			for (int pos = 1; pos <= 9; pos++)
			{
				int style = words[pos];
				if (style == 65535)
				{
					continue;
				}
				if (style == 0)
				{
					foreach (TlItem it in me.Items)
					{
						if (it.FdPosition != pos || !it.Equipped)
						{
							continue;
						}
						if (it.FdCharacter != 0 && it.FdCharacter != me.ModelId)
						{
							continue;
						}
						db.SetItemWorn(session.AccountId.Value, it.ItemNum, false);
						it.Equipped = false;
					}
					continue;
				}
				TlItem match = null;
				foreach (TlItem it in me.Items)
				{
					if (it.FdPosition != pos || it.FdItemKind != style)
					{
						continue;
					}
					if (it.FdCharacter != 0 && it.FdCharacter != me.ModelId)
					{
						continue;
					}
					match = it;
					if (it.Equipped)
					{
						break;
					}
				}
				if (match == null)
				{
					TlLog.Info("suit slot " + pos + " kind " + style + " not owned");
					continue;
				}
				foreach (TlItem other in me.Items)
				{
					if (other.ItemNum == match.ItemNum || other.FdPosition != pos || !other.Equipped)
					{
						continue;
					}
					if (other.FdCharacter != 0 && other.FdCharacter != me.ModelId)
					{
						continue;
					}
					db.SetItemWorn(session.AccountId.Value, other.ItemNum, false);
					other.Equipped = false;
				}
				if (!match.Equipped)
				{
					db.SetItemWorn(session.AccountId.Value, match.ItemNum, true);
					match.Equipped = true;
				}
				worn++;
			}
			TlLog.Info("suit model " + me.ModelId + " worn " + worn);
		}

		private static byte[] UserInfo(TlDb db, string asked)
		{
			TlCharacter ch = FindOnline(db, asked);
			string shown = ch != null && !string.IsNullOrEmpty(ch.Nickname) ? ch.Nickname : (asked ?? "");
			byte[] look = ch != null ? Appearance(ch) : new byte[34];
			TlWriter w = new TlWriter((int)TlAgentPacket.GET_USER_INFO_ACK);
			w.U32(0);
			w.String(shown);
			w.String("");
			w.U32(0).U32(0);
			w.Bytes(look);
			w.String("").String("");
			w.U32(0);
			w.Zeros(88);
			w.U32(0).U32(0).U32(0);
			w.Zeros(8);
			w.U32(0).U32(0);
			w.U32(0).U32(0).U32(0).U32(0);
			w.String("").String("");
			w.Zeros(8).Zeros(8);
			w.U8(0).U8(0);
			w.U32(0).U32(0);
			// Profile present, every optional section empty. A false flag logs "Profile Don't be Received".
			w.Bool(true);
			w.U8(0).U8(0).U8(0).U8(0).U8(0).U8(0);
			return w.Payload();
		}

		private static TlCharacter FindOnline(TlDb db, string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return null;
			}
			Session found = null;
			lock (SendLock)
			{
				foreach (Live live in Lives)
				{
					if (live.Session != null && string.Equals(live.Name, name, StringComparison.OrdinalIgnoreCase))
					{
						found = live.Session;
						break;
					}
				}
			}
			return found == null ? null : Current(db, found);
		}

		private static byte[] Appearance(TlCharacter ch)
		{
			return Appearance(ch, ch.ModelId);
		}

		private static byte[] Appearance(TlCharacter ch, int model)
		{
			ushort[] words = new ushort[17];
			words[0] = (ushort)model;
			foreach (TlItem it in ch.Items)
			{
				if (!it.Equipped || it.FdPosition < 1 || it.FdPosition > 9)
				{
					continue;
				}
				if (it.FdCharacter != 0 && it.FdCharacter != model)
				{
					continue;
				}
				words[it.FdPosition] = (ushort)it.FdItemKind;
			}
			words[12] = (ushort)ch.CreatorSetNum;
			byte[] raw = new byte[34];
			for (int i = 0; i < 17; i++)
			{
				raw[i * 2] = (byte)words[i];
				raw[i * 2 + 1] = (byte)(words[i] >> 8);
			}
			return raw;
		}

		private static string Hex(TlReader reader)
		{
			int n = reader.Remaining > 32 ? 32 : reader.Remaining;
			if (n <= 0)
			{
				return "";
			}
			byte[] b = reader.Bytes(n);
			return BitConverter.ToString(b);
		}

		private static string Name(int op)
		{
			if (Enum.IsDefined(typeof(TlAgentPacket), op))
			{
				return ((TlAgentPacket)op).ToString();
			}
			return "UNK";
		}
	}
}
