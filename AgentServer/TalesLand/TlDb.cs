using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using MySql.Data.MySqlClient;

namespace TalesLand
{
	public sealed class TlItem
	{
		public int ItemNum;
		public int FdCharacter;
		public int FdPosition;
		public int FdItemKind;
		public bool Equipped;
		public int AcquiredAt;
		public long ExpireUnix;
	}

	public sealed class TlCapsulePrize
	{
		public int Index;
		public int ItemNum;
		public int Count;
		public int Max;
	}

	public sealed class TlGift
	{
		public int Id;
		public string Sender;
		public int ItemNum;
		public DateTime SentAt;
		public string Memo;
	}

	public sealed class TlCharacter
	{
		public int Id;
		public int AccountId;
		public string Nickname;
		public int ModelId;
		public int BaseModelId;
		public int CreatorSetNum;
		public int Money;
		public int Experience;
		public List<TlItem> Items = new List<TlItem>();
	}

	public sealed class TlCreatable
	{
		public int SetNum;
		public int ModelId;
		public int[] Styles = new int[11];
	}

	public sealed class TlCatalogueItem
	{
		public int ItemNum;
		public int FdType;
		public int FdCharacter;
		public int FdPosition;
		public int FdItemKind;
		public int GameMoney;
		public int Cash;
		public int GrantItemNum;
		public int LeaseDays;
	}

	public sealed class TlMixResult
	{
		public bool Ok;
		public byte Reason;
		public int ItemNum;
		public List<KeyValuePair<int, int>> Attrs = new List<KeyValuePair<int, int>>();
	}

	public sealed class TlDb
	{
		private readonly string _cs;
		private readonly Dictionary<int, TlCreatable> _creatable = new Dictionary<int, TlCreatable>();
		private readonly List<TlCatalogueItem> _catalogue = new List<TlCatalogueItem>();
		private readonly Dictionary<int, List<TlCapsulePrize>> _capsule = new Dictionary<int, List<TlCapsulePrize>>();
		private readonly Dictionary<int, HashSet<int>> _licenseMissions = new Dictionary<int, HashSet<int>>();

		public List<KeyValuePair<string, string>> ClientSettings = new List<KeyValuePair<string, string>>();

		public TlDb(string connectionString)
		{
			_cs = connectionString;
			LoadStep("essencreatablechacter", LoadCreatable);
			LoadStep("tblavataritemdesc", LoadCatalogue);
			LoadStep("settingcapsulemachineitemlist", LoadCapsule);
			LoadStep("tblserversettinginfo", LoadSettings);
			LoadStep("tblgamelicenseinfo", LoadLicenses);
			TlLog.Info("talesland_db creatable=" + _creatable.Count + " avatar rows=" + _catalogue.Count
				+ " capsule machines=" + _capsule.Count + " settings=" + ClientSettings.Count
				+ " licenses=" + _licenseMissions.Count);
		}

		private static void LoadStep(string table, Action load)
		{
			try
			{
				load();
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException("talesland_db load failed on " + table + ": " + ex.GetType().Name + ": " + ex.Message, ex);
			}
		}

		public bool HasLicense(int type)
		{
			return _licenseMissions.ContainsKey(type);
		}

		public bool HasLicenseMission(int type, int mission)
		{
			HashSet<int> missions;
			return _licenseMissions.TryGetValue(type, out missions) && missions.Contains(mission);
		}

		private MySqlConnection Open()
		{
			MySqlConnection c = new MySqlConnection(_cs);
			try
			{
				c.Open();
			}
			catch (Exception ex)
			{
				c.Dispose();
				throw new InvalidOperationException("MySQL open failed (" + DbTarget(_cs) + "): " + ex.GetType().Name + ": " + ex.Message, ex);
			}
			return c;
		}

		private static string DbTarget(string connectionString)
		{
			try
			{
				MySqlConnectionStringBuilder b = new MySqlConnectionStringBuilder(connectionString);
				return b.Server + ":" + b.Port + "/" + b.Database + " user=" + b.UserID;
			}
			catch
			{
				return "unreadable connection string";
			}
		}

		private void LoadLicenses()
		{
			try
			{
				using (MySqlConnection c = Open())
				{
					using (MySqlCommand cmd = new MySqlCommand("SELECT fdNum FROM tblgamelicenseinfo", c))
					using (MySqlDataReader rd = cmd.ExecuteReader())
					{
						while (rd.Read())
						{
							int type = ToInt(rd, 0);
							if (!_licenseMissions.ContainsKey(type))
							{
								_licenseMissions[type] = new HashSet<int>();
							}
						}
					}
					using (MySqlCommand cmd = new MySqlCommand("SELECT fdLicenseNum, fdMissionNum FROM tblgamelicensemapinfo", c))
					using (MySqlDataReader rd = cmd.ExecuteReader())
					{
						while (rd.Read())
						{
							int type = ToInt(rd, 0);
							int mission = ToInt(rd, 1);
							HashSet<int> missions;
							if (!_licenseMissions.TryGetValue(type, out missions))
							{
								missions = new HashSet<int>();
								_licenseMissions[type] = missions;
							}
							missions.Add(mission);
						}
					}
				}
			}
			catch (Exception ex)
			{
				TlLog.Warn("license tables: " + ex.Message);
			}
		}

		private void LoadCreatable()
		{
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT `type`,`character`,`head`,`top`,`down`,`foot`,`achead`,`acfoot`,`achand`,`acback`,`acneck`,`pet`,`expnd` FROM essencreatablechacter ORDER BY `type`", c))
			using (MySqlDataReader rd = cmd.ExecuteReader())
			{
				while (rd.Read())
				{
					TlCreatable row = new TlCreatable();
					row.SetNum = ToInt(rd, 0);
					row.ModelId = ToInt(rd, 1);
					for (int i = 0; i < 11; i++)
					{
						row.Styles[i] = ToInt(rd, 2 + i);
					}
					_creatable[row.SetNum] = row;
				}
			}
		}

		private void LoadCatalogue()
		{
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdItemNum, fdType, fdCharacter, fdPosition, fdItemKind, fdGameMoney, fdCash, fdItemName FROM tblavataritemdesc ORDER BY fdItemNum", c))
			using (MySqlDataReader rd = cmd.ExecuteReader())
			{
				while (rd.Read())
				{
					TlCatalogueItem row = new TlCatalogueItem
					{
						ItemNum = ToInt(rd, 0),
						FdType = ToInt(rd, 1),
						FdCharacter = ToInt(rd, 2),
						FdPosition = ToInt(rd, 3),
						FdItemKind = ToInt(rd, 4),
						GameMoney = ToInt(rd, 5),
						Cash = ToInt(rd, 6),
						GrantItemNum = ToInt(rd, 0),
						LeaseDays = PeriodDays(rd.IsDBNull(7) ? "" : rd.GetString(7))
					};
					_catalogue.Add(row);
				}
			}
			BindPeriodTickets();
		}

		/// <summary>
		/// A type-3 shop row is a day ticket ("… 1日"). The wearable is the nearest preceding type-2 row.
		/// </summary>
		private void BindPeriodTickets()
		{
			TlCatalogueItem wear = null;
			foreach (TlCatalogueItem row in _catalogue)
			{
				if (row.FdType == 2)
				{
					wear = row;
					continue;
				}
				if (row.FdType == 3 && row.LeaseDays > 0 && wear != null)
				{
					row.GrantItemNum = wear.ItemNum;
				}
			}
		}

		private static int PeriodDays(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return 0;
			}
			StringBuilder plain = new StringBuilder(name.Length);
			foreach (char ch in name)
			{
				plain.Append(ch >= '０' && ch <= '９' ? (char)('0' + (ch - '０')) : ch);
			}
			string text = plain.ToString();
			int mark = text.LastIndexOf('日');
			if (mark <= 0)
			{
				return 0;
			}
			int start = mark;
			while (start > 0 && text[start - 1] >= '0' && text[start - 1] <= '9')
			{
				start--;
			}
			if (start == mark)
			{
				return 0;
			}
			int days;
			if (!int.TryParse(text.Substring(start, mark - start), out days) || days <= 0 || days > 3650)
			{
				return 0;
			}
			return days;
		}

		private void LoadCapsule()
		{
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT s.fdMachineNum, s.fdLevel, s.fdItemNum, s.fdItemMax, " +
				"CASE WHEN IFNULL(g.fdItemCount,0) > 0 THEN g.fdItemCount WHEN s.fdItemMax > 0 THEN s.fdItemMax ELSE 1 END " +
				"FROM settingcapsulemachineitemlist s " +
				"LEFT JOIN gamedatacapsulemachineitemcount g ON g.fdMachineNum=s.fdMachineNum AND g.fdItemNum=s.fdItemNum " +
				"ORDER BY s.fdMachineNum, s.fdLevel, s.fdItemNum", c))
			using (MySqlDataReader rd = cmd.ExecuteReader())
			{
				while (rd.Read())
				{
					int machine = ToInt(rd, 0);
					List<TlCapsulePrize> list;
					if (!_capsule.TryGetValue(machine, out list))
					{
						list = new List<TlCapsulePrize>();
						_capsule[machine] = list;
					}
					int max = ToInt(rd, 3);
					int count = ToInt(rd, 4);
					if (count < 1)
					{
						count = max > 0 ? max : 1;
					}
					if (max < count)
					{
						max = count;
					}
					list.Add(new TlCapsulePrize
					{
						Index = list.Count,
						ItemNum = ToInt(rd, 2),
						Count = count,
						Max = max
					});
				}
			}
		}

		public List<TlCapsulePrize> CapsulePrizes(int machine)
		{
			List<TlCapsulePrize> rows;
			if (machine > 0 && _capsule.TryGetValue(machine, out rows))
			{
				return rows;
			}
			return new List<TlCapsulePrize>();
		}

		public void ReloadSettings()
		{
			LoadSettings();
		}

		public int CountMaps()
		{
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand("SELECT COUNT(*) FROM tblmapinfo", c))
			{
				return Convert.ToInt32(cmd.ExecuteScalar());
			}
		}

		private void LoadSettings()
		{
			ClientSettings = new List<KeyValuePair<string, string>>();
			HashSet<string> seen = new HashSet<string>();
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdKey, fdValue, fdLiveValue FROM tblserversettinginfo ORDER BY fdNum", c))
			using (MySqlDataReader rd = cmd.ExecuteReader())
			{
				while (rd.Read())
				{
					if (rd.IsDBNull(0))
					{
						continue;
					}
					string key = rd.GetString(0);
					if (!seen.Add(key))
					{
						continue;
					}
					string live = rd.IsDBNull(2) ? "" : rd.GetString(2);
					string value = rd.IsDBNull(1) ? "" : rd.GetString(1);
					ClientSettings.Add(new KeyValuePair<string, string>(key, live.Length > 0 ? live : value));
				}
			}
		}

		private static int ToInt(MySqlDataReader rd, int i)
		{
			if (rd.IsDBNull(i))
			{
				return 0;
			}
			int n;
			int.TryParse(Convert.ToString(rd.GetValue(i)), out n);
			return n;
		}

		public int Login(string loginKey, string password, out bool rejected)
		{
			rejected = false;
			if (string.IsNullOrEmpty(loginKey) || password == null)
			{
				rejected = true;
				return 0;
			}
			using (MySqlConnection c = Open())
			{
				string stored = null;
				using (MySqlCommand read = new MySqlCommand(
					"SELECT fdPassword FROM userinfofrompublisher WHERE fdUserID=@k LIMIT 1", c))
				{
					read.Parameters.AddWithValue("@k", loginKey);
					object v = read.ExecuteScalar();
					if (v != null && v != DBNull.Value)
					{
						stored = Convert.ToString(v) ?? "";
					}
				}
				if (stored == null)
				{
					using (MySqlCommand pub = new MySqlCommand(
						"INSERT INTO userinfofrompublisher (fdUserID, fdPassword) VALUES (@k, @p)", c))
					{
						pub.Parameters.AddWithValue("@k", loginKey);
						pub.Parameters.AddWithValue("@p", PasswordStore(password));
						pub.ExecuteNonQuery();
					}
				}
				else if (stored.Length > 0 && !PasswordMatches(stored, password))
				{
					rejected = true;
					return 0;
				}
				else if (stored.Length == 0 && password.Length > 0)
				{
					using (MySqlCommand set = new MySqlCommand(
						"UPDATE userinfofrompublisher SET fdPassword=@p WHERE fdUserID=@k AND fdPassword=''", c))
					{
						set.Parameters.AddWithValue("@k", loginKey);
						set.Parameters.AddWithValue("@p", PasswordStore(password));
						set.ExecuteNonQuery();
					}
				}
				int userNum;
				using (MySqlCommand find = new MySqlCommand("SELECT fdUserNum FROM userinfo WHERE fdUID=@k LIMIT 1", c))
				{
					find.Parameters.AddWithValue("@k", loginKey);
					object id = find.ExecuteScalar();
					if (id != null && id != DBNull.Value)
					{
						userNum = Convert.ToInt32(id);
					}
					else
					{
						using (MySqlCommand ins = new MySqlCommand("INSERT INTO userinfo (fdUID, fdNickname) VALUES (@k, NULL)", c))
						{
							ins.Parameters.AddWithValue("@k", loginKey);
							ins.ExecuteNonQuery();
							userNum = (int)ins.LastInsertedId;
						}
					}
				}
				using (MySqlCommand game = new MySqlCommand(
					"INSERT INTO userinfogame (fdUserNum, fdExp, fdGameMoney, fdAvatarCharacterSettingNum, fdLikeable) VALUES (@id,0,0,0,0) " +
					"ON DUPLICATE KEY UPDATE fdUserNum=fdUserNum", c))
				{
					game.Parameters.AddWithValue("@id", userNum);
					game.ExecuteNonQuery();
				}
				return userNum;
			}
		}

		private static bool PasswordMatches(string stored, string typed)
		{
			if (stored == typed)
			{
				return true;
			}
			string ascii = Md5Hex(typed, Encoding.ASCII);
			if (string.Equals(stored, ascii, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			string utf = Md5Hex(typed, Encoding.UTF8);
			return utf != ascii && string.Equals(stored, utf, StringComparison.OrdinalIgnoreCase);
		}

		private static string PasswordStore(string typed)
		{
			bool ascii = true;
			for (int i = 0; i < typed.Length; i++)
			{
				if (typed[i] > 127)
				{
					ascii = false;
					break;
				}
			}
			return Md5Hex(typed, ascii ? Encoding.ASCII : Encoding.UTF8);
		}

		private static string Md5Hex(string text, Encoding encoding)
		{
			using (MD5 md5 = MD5.Create())
			{
				byte[] hash = md5.ComputeHash(encoding.GetBytes(text));
				StringBuilder sb = new StringBuilder(hash.Length * 2);
				for (int i = 0; i < hash.Length; i++)
				{
					sb.Append(hash[i].ToString("x2"));
				}
				return sb.ToString();
			}
		}

		public void AddPlayReward(int userNum, int exp, int tr)
		{
			if (userNum <= 0 || (exp == 0 && tr == 0))
			{
				return;
			}
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"INSERT INTO userinfogame (fdUserNum, fdExp, fdGameMoney, fdAvatarCharacterSettingNum, fdLikeable) " +
				"VALUES (@u, @e, @m, 0, 0) " +
				"ON DUPLICATE KEY UPDATE fdExp=fdExp+@e, fdGameMoney=fdGameMoney+@m", c))
			{
				cmd.Parameters.AddWithValue("@u", userNum);
				cmd.Parameters.AddWithValue("@e", exp);
				cmd.Parameters.AddWithValue("@m", tr);
				cmd.ExecuteNonQuery();
			}
		}

		public int GetCash(int userNum)
		{
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT IFNULL(p.fdCash,0) FROM userinfo u LEFT JOIN userinfofrompublisher p ON p.fdUserID=u.fdUID WHERE u.fdUserNum=@id", c))
			{
				cmd.Parameters.AddWithValue("@id", userNum);
				object v = cmd.ExecuteScalar();
				return v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
			}
		}

		public List<TlCharacter> ListCharacters(int accountId)
		{
			List<TlCharacter> list = new List<TlCharacter>();
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT u.fdUserNum, IFNULL(u.fdNickname,''), IFNULL(g.fdAvatarCharacterSettingNum,0), IFNULL(g.fdGameMoney,0), IFNULL(g.fdExp,0) " +
				"FROM userinfo u LEFT JOIN userinfogame g ON g.fdUserNum=u.fdUserNum " +
				"WHERE u.fdUserNum=@a AND u.fdNickname IS NOT NULL AND u.fdNickname<>''", c))
			{
				cmd.Parameters.AddWithValue("@a", accountId);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					while (rd.Read())
					{
						int setNum = rd.GetInt32(2);
						TlCreatable row;
						int model = _creatable.TryGetValue(setNum, out row) ? row.ModelId : 0;
						list.Add(new TlCharacter
						{
							Id = rd.GetInt32(0),
							AccountId = accountId,
							Nickname = rd.GetString(1),
							ModelId = model,
							BaseModelId = model,
							CreatorSetNum = setNum,
							Money = rd.GetInt32(3),
							Experience = Convert.ToInt32(Convert.ToInt64(rd.GetValue(4)))
						});
					}
				}
			}
			foreach (TlCharacter ch in list)
			{
				ch.Items = LoadItems(ch.Id);
				foreach (TlItem it in ch.Items)
				{
					if (it.Equipped && it.FdPosition == 0 && it.FdCharacter >= 201 && it.FdCharacter <= 205)
					{
						ch.ModelId = it.FdCharacter;
						break;
					}
				}
			}
			return list;
		}

		private List<TlItem> LoadItems(int userNum)
		{
			List<TlItem> items = new List<TlItem>();
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdItemDescNum, fdCharacter, fdUsing, UNIX_TIMESTAMP(fdGotDateTime), UNIX_TIMESTAMP(fdExpireDateTime) FROM tblavatarusercharacter WHERE fdUserNum=@id", c))
			{
				cmd.Parameters.AddWithValue("@id", userNum);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					while (rd.Read())
					{
						int itemNum = rd.GetInt32(0);
						int fdCharacter = rd.GetInt32(1);
						TlCatalogueItem desc = FindItem(itemNum);
						TlCatalogueItem shown = desc;
						if (desc != null && desc.GrantItemNum != desc.ItemNum)
						{
							shown = FindItem(desc.GrantItemNum) ?? desc;
							itemNum = shown.ItemNum;
						}
						items.Add(new TlItem
						{
							ItemNum = itemNum,
							FdCharacter = shown != null ? shown.FdCharacter : fdCharacter,
							FdPosition = shown != null ? shown.FdPosition : 0,
							FdItemKind = shown != null ? shown.FdItemKind : 0,
							Equipped = rd.GetInt32(2) != 0,
							AcquiredAt = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetInt64(3)),
							ExpireUnix = rd.IsDBNull(4) ? 0 : rd.GetInt64(4)
						});
					}
				}
			}
			List<TlItem> unique = new List<TlItem>();
			foreach (TlItem it in items)
			{
				int at = unique.FindIndex(x => x.ItemNum == it.ItemNum);
				if (at < 0)
				{
					unique.Add(it);
					continue;
				}
				if (it.ExpireUnix > unique[at].ExpireUnix)
				{
					unique[at] = it;
				}
			}
			return unique;
		}

		public bool KnowsSet(int setNum)
		{
			return _creatable.ContainsKey(setNum);
		}

		/// <summary>
		/// Grant the creatable grid's worn items. Null when a nonzero cell has no catalogue row.
		/// </summary>
		public List<TlItem> ResolveGrant(int setNum, out int modelId)
		{
			modelId = 0;
			TlCreatable row;
			if (!_creatable.TryGetValue(setNum, out row))
			{
				return null;
			}
			modelId = row.ModelId;
			int[] positions = new int[] { 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0 };
			List<TlItem> items = new List<TlItem>();
			int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			for (int i = 0; i < 11; i++)
			{
				int style = row.Styles[i];
				if (style == 0)
				{
					continue;
				}
				int position = positions[i];
				if (position == 0)
				{
					return null;
				}
				TlCatalogueItem hit = null;
				foreach (TlCatalogueItem d in _catalogue)
				{
					if (d.FdCharacter == row.ModelId && d.FdPosition == position && d.FdItemKind == style)
					{
						hit = d;
						break;
					}
				}
				if (hit == null)
				{
					return null;
				}
				items.Add(new TlItem
				{
					ItemNum = hit.ItemNum,
					FdCharacter = hit.FdCharacter,
					FdPosition = hit.FdPosition,
					FdItemKind = hit.FdItemKind,
					Equipped = true,
					AcquiredAt = now
				});
			}
			foreach (TlCatalogueItem d in _catalogue)
			{
				if (d.FdCharacter == row.ModelId && d.FdPosition == 0 && d.FdItemKind == 0 && (d.FdType == 1 || d.FdType == 2))
				{
					bool have = false;
					foreach (TlItem owned in items)
					{
						if (owned.ItemNum == d.ItemNum)
						{
							have = true;
							break;
						}
					}
					if (!have)
					{
						items.Add(new TlItem
						{
							ItemNum = d.ItemNum,
							FdCharacter = d.FdCharacter,
							FdPosition = d.FdPosition,
							FdItemKind = d.FdItemKind,
							Equipped = false,
							AcquiredAt = now
						});
					}
					break;
				}
			}
			return items;
		}

		public List<KeyValuePair<int, int>> CardCounts(int userNum)
		{
			List<KeyValuePair<int, int>> cards = new List<KeyValuePair<int, int>>();
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT u.fdItemDescNum, u.fdCount FROM tblavataruser u " +
				"JOIN tblavataritemdesc d ON d.fdItemNum=u.fdItemDescNum " +
				"WHERE u.fdUserNum=@u AND d.fdType=1 AND d.fdItemCategory<>1001 AND u.fdCount>0 " +
				"ORDER BY u.fdItemDescNum", c))
			{
				cmd.Parameters.AddWithValue("@u", userNum);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					while (rd.Read())
					{
						cards.Add(new KeyValuePair<int, int>(rd.GetInt32(0), rd.GetInt32(1)));
					}
				}
			}
			return cards;
		}

		/// <summary>
		/// Opcode 292 kind 3. The second string is the result item or the recipe card.
		/// Success is opcode 314 with the crafted item and its float attribute bits.
		/// </summary>
		public TlMixResult MixAlchemist(int userNum, int requestedItem)
		{
			TlMixResult fail = new TlMixResult();
			if (userNum <= 0 || requestedItem <= 0)
			{
				fail.Reason = 1;
				return fail;
			}
			int recipe = 0;
			int result = 0;
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdRecipeNum, fdResultItemDescNum FROM tblalchemist_recipe " +
				"WHERE fdResultItemDescNum=@n OR fdRecipeCardItemDescNum=@n LIMIT 1", c))
			{
				cmd.Parameters.AddWithValue("@n", requestedItem);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					if (!rd.Read())
					{
						fail.Reason = 1;
						return fail;
					}
					recipe = rd.GetInt32(0);
					result = rd.GetInt32(1);
				}
			}
			List<KeyValuePair<int, int>> sources = new List<KeyValuePair<int, int>>();
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdNeedItemDescNum, fdNeedCount FROM tblalchemist_recipesource WHERE fdRecipeNum=@r", c))
			{
				cmd.Parameters.AddWithValue("@r", recipe);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					while (rd.Read())
					{
						sources.Add(new KeyValuePair<int, int>(rd.GetInt32(0), rd.GetInt32(1)));
					}
				}
			}
			if (sources.Count == 0)
			{
				fail.Reason = 6;
				return fail;
			}
			TlCatalogueItem made = FindItem(result);
			int character = made != null ? made.FdCharacter : 0;
			int days = made != null && made.LeaseDays > 0 ? made.LeaseDays : 30;
			int grade = MixGrade();
			List<KeyValuePair<int, int>> attrs = new List<KeyValuePair<int, int>>();
			List<KeyValuePair<int, decimal>> stored = new List<KeyValuePair<int, decimal>>();
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdProbability, fdAttrType, fdAttrValueMin, fdAttrValueMax " +
				"FROM tblalchemist_recipe_mix_condition WHERE fdRecipeNum=@r AND fdClass=@c", c))
			{
				cmd.Parameters.AddWithValue("@r", recipe);
				cmd.Parameters.AddWithValue("@c", grade);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					Random roll = new Random();
					while (rd.Read())
					{
						int probability = rd.GetInt32(0);
						if (probability < 100 && roll.Next(100) >= probability)
						{
							continue;
						}
						int attr = rd.GetInt32(1);
						decimal min = rd.GetDecimal(2);
						decimal max = rd.GetDecimal(3);
						decimal value = min;
						if (max > min)
						{
							value = min + (decimal)roll.NextDouble() * (max - min);
						}
						stored.Add(new KeyValuePair<int, decimal>(attr, value));
						attrs.Add(new KeyValuePair<int, int>(attr, FloatBits(value)));
					}
				}
			}
			using (MySqlConnection c = Open())
			using (MySqlTransaction tx = c.BeginTransaction())
			{
				using (MySqlCommand owned = new MySqlCommand(
					"SELECT IFNULL(MAX(fdCount),0) FROM tblavataruser WHERE fdUserNum=@u AND fdItemDescNum=@n", c, tx))
				{
					owned.Parameters.AddWithValue("@u", userNum);
					owned.Parameters.AddWithValue("@n", result);
					int have = Convert.ToInt32(owned.ExecuteScalar());
					if (have > 0)
					{
						tx.Rollback();
						fail.Reason = 2;
						return fail;
					}
				}
				foreach (KeyValuePair<int, int> source in sources)
				{
					if (!SpendCard(c, tx, userNum, source.Key, source.Value))
					{
						tx.Rollback();
						fail.Reason = 3;
						return fail;
					}
				}
				StoreOwned(c, tx, userNum, result, character, days);
				foreach (KeyValuePair<int, decimal> attr in stored)
				{
					using (MySqlCommand ins = new MySqlCommand(
						"INSERT INTO tblavataruserattr (fdUserNum, fdItemDescNum, fdAttrType, fdAttrValue) " +
						"VALUES (@u,@n,@t,@v) ON DUPLICATE KEY UPDATE fdAttrValue=@v", c, tx))
					{
						ins.Parameters.AddWithValue("@u", userNum);
						ins.Parameters.AddWithValue("@n", result);
						ins.Parameters.AddWithValue("@t", attr.Key);
						ins.Parameters.AddWithValue("@v", attr.Value);
						ins.ExecuteNonQuery();
					}
				}
				tx.Commit();
			}
			return new TlMixResult { Ok = true, ItemNum = result, Attrs = attrs };
		}

		private static bool SpendCard(MySqlConnection c, MySqlTransaction tx, int userNum, int itemNum, int count)
		{
			if (count < 1)
			{
				return true;
			}
			using (MySqlCommand au = new MySqlCommand(
				"UPDATE tblavataruser SET fdCount=fdCount-@c WHERE fdUserNum=@u AND fdItemDescNum=@n AND fdCount>=@c", c, tx))
			{
				au.Parameters.AddWithValue("@c", count);
				au.Parameters.AddWithValue("@u", userNum);
				au.Parameters.AddWithValue("@n", itemNum);
				if (au.ExecuteNonQuery() != 1)
				{
					return false;
				}
			}
			using (MySqlCommand ch = new MySqlCommand(
				"UPDATE tblavatarusercharacter SET fdCount=fdCount-@c WHERE fdUserNum=@u AND fdItemDescNum=@n AND fdCount>=@c", c, tx))
			{
				ch.Parameters.AddWithValue("@c", count);
				ch.Parameters.AddWithValue("@u", userNum);
				ch.Parameters.AddWithValue("@n", itemNum);
				ch.ExecuteNonQuery();
			}
			using (MySqlCommand del = new MySqlCommand(
				"DELETE FROM tblavataruser WHERE fdUserNum=@u AND fdItemDescNum=@n AND fdCount<=0", c, tx))
			{
				del.Parameters.AddWithValue("@u", userNum);
				del.Parameters.AddWithValue("@n", itemNum);
				del.ExecuteNonQuery();
			}
			using (MySqlCommand del = new MySqlCommand(
				"DELETE FROM tblavatarusercharacter WHERE fdUserNum=@u AND fdItemDescNum=@n AND fdCount<=0", c, tx))
			{
				del.Parameters.AddWithValue("@u", userNum);
				del.Parameters.AddWithValue("@n", itemNum);
				del.ExecuteNonQuery();
			}
			return true;
		}

		private static int MixGrade()
		{
			int roll = new Random().Next(10001);
			if (roll >= 9900)
			{
				return 100;
			}
			if (roll >= 9500)
			{
				return 200;
			}
			if (roll >= 8500)
			{
				return 300;
			}
			if (roll >= 6000)
			{
				return 400;
			}
			return 500;
		}

		private static int FloatBits(decimal raw)
		{
			return BitConverter.ToInt32(BitConverter.GetBytes((float)raw), 0);
		}

		public TlCatalogueItem FindItem(int itemNum)
		{
			foreach (TlCatalogueItem d in _catalogue)
			{
				if (d.ItemNum == itemNum)
				{
					return d;
				}
			}
			return null;
		}

		/// <summary>
		/// Charge the ticket's cash price, or its TR price, then store the wearable.
		/// A type-3 row such as "ロマンリボンヘア 1日" stores the preceding type-2 item for that many days.
		/// </summary>
		public bool TryBuy(int userNum, int itemNum, out int money, out int cash, out int grantedNum, out int leaseDays)
		{
			money = 0;
			cash = 0;
			TlCatalogueItem ticket = FindItem(itemNum);
			TlCatalogueItem granted = ticket;
			leaseDays = 0;
			if (ticket != null && ticket.LeaseDays > 0 && ticket.GrantItemNum != ticket.ItemNum)
			{
				granted = FindItem(ticket.GrantItemNum) ?? ticket;
				leaseDays = ticket.LeaseDays;
			}
			grantedNum = granted != null ? granted.ItemNum : itemNum;
			int character = granted != null ? granted.FdCharacter : 0;
			int priceCash = ticket != null ? ticket.Cash : 0;
			int priceTr = ticket != null ? ticket.GameMoney : 0;
			int storeDays = leaseDays > 0 ? leaseDays : 30;
			using (MySqlConnection c = Open())
			using (MySqlTransaction tx = c.BeginTransaction())
			{
				using (MySqlCommand read = new MySqlCommand(
					"SELECT IFNULL(g.fdGameMoney,0), IFNULL(p.fdCash,0) FROM userinfo u " +
					"LEFT JOIN userinfogame g ON g.fdUserNum=u.fdUserNum " +
					"LEFT JOIN userinfofrompublisher p ON p.fdUserID=u.fdUID WHERE u.fdUserNum=@u", c, tx))
				{
					read.Parameters.AddWithValue("@u", userNum);
					using (MySqlDataReader rd = read.ExecuteReader())
					{
						if (rd.Read())
						{
							money = ToInt(rd, 0);
							cash = ToInt(rd, 1);
						}
					}
				}
				if (priceCash > 0)
				{
					if (cash < priceCash)
					{
						tx.Rollback();
						return false;
					}
					cash -= priceCash;
					using (MySqlCommand pay = new MySqlCommand(
						"UPDATE userinfofrompublisher p JOIN userinfo u ON u.fdUID=p.fdUserID SET p.fdCash=@c WHERE u.fdUserNum=@u", c, tx))
					{
						pay.Parameters.AddWithValue("@c", cash);
						pay.Parameters.AddWithValue("@u", userNum);
						pay.ExecuteNonQuery();
					}
				}
				else if (priceTr > 0)
				{
					if (money < priceTr)
					{
						tx.Rollback();
						return false;
					}
					money -= priceTr;
					using (MySqlCommand pay = new MySqlCommand(
						"UPDATE userinfogame SET fdGameMoney=@m WHERE fdUserNum=@u", c, tx))
					{
						pay.Parameters.AddWithValue("@m", money);
						pay.Parameters.AddWithValue("@u", userNum);
						pay.ExecuteNonQuery();
					}
				}
				StoreOwned(c, tx, userNum, grantedNum, character, storeDays);
				tx.Commit();
				return true;
			}
		}

		public List<KeyValuePair<int, int>> LicenseClears(int userNum, int licenseType)
		{
			List<KeyValuePair<int, int>> rows = new List<KeyValuePair<int, int>>();
			if (userNum <= 0)
			{
				return rows;
			}
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdMissionNum, fdAchievedPoint FROM tbluserlicensemissioninfo WHERE fdUserNum=@u AND fdLicenseNum=@t AND fdMissionState=2 ORDER BY fdMissionNum", c))
			{
				cmd.Parameters.AddWithValue("@u", userNum);
				cmd.Parameters.AddWithValue("@t", licenseType);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					while (rd.Read())
					{
						int mission = ToInt(rd, 0);
						// The mission list only treats 1 and 2 as a clear. 2 unlocks the next mission.
						rows.Add(new KeyValuePair<int, int>(mission, 2));
					}
				}
			}
			return rows;
		}

		public void SaveLicenseClear(int userNum, int licenseType, int mission, int point)
		{
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"INSERT INTO tbluserlicensemissioninfo (fdUserNum, fdLicenseNum, fdMissionNum, fdMissionState, fdAchievedPoint) " +
				"VALUES (@u, @t, @m, 2, @p) ON DUPLICATE KEY UPDATE fdMissionState=2, fdAchievedPoint=GREATEST(fdAchievedPoint, @p)", c))
			{
				cmd.Parameters.AddWithValue("@u", userNum);
				cmd.Parameters.AddWithValue("@t", licenseType);
				cmd.Parameters.AddWithValue("@m", mission);
				cmd.Parameters.AddWithValue("@p", point < 0 ? 0 : point);
				cmd.ExecuteNonQuery();
			}
		}

		public List<KeyValuePair<string, int>> TimeAttackRanks(int map)
		{
			List<KeyValuePair<string, int>> rows = new List<KeyValuePair<string, int>>();
			if (map <= 0)
			{
				return rows;
			}
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdNickname, fdLapTime FROM tbltimeattack WHERE fdMapNum=@map AND fdLapTime>0 ORDER BY fdLapTime ASC LIMIT 3", c))
			{
				cmd.Parameters.AddWithValue("@map", map);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					while (rd.Read())
					{
						string name = rd.IsDBNull(0) ? "" : rd.GetString(0);
						rows.Add(new KeyValuePair<string, int>(name, ToInt(rd, 1)));
					}
				}
			}
			return rows;
		}

		public void SaveTimeAttack(int userNum, int map, int lap, string nickname)
		{
			if (lap <= 0)
			{
				return;
			}
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"INSERT INTO tbltimeattack (fdNum, fdMapNum, fdLapTime, fdNickname, fdDateTime) " +
				"SELECT IFNULL(MAX(fdNum),0)+1, @map, @lap, @name, NOW() FROM tbltimeattack", c))
			{
				cmd.Parameters.AddWithValue("@map", map);
				cmd.Parameters.AddWithValue("@lap", lap);
				cmd.Parameters.AddWithValue("@name", nickname ?? "");
				cmd.ExecuteNonQuery();
			}
		}

		public bool TryMiniGameOffer(int id, out int price, out int life)
		{
			price = 0;
			life = 0;
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdMapPrice, fdLife FROM tblminigameinfo WHERE fdIsUse=1 AND (fdMapNum=@id OR fdGameNum=@id) LIMIT 1", c))
			{
				cmd.Parameters.AddWithValue("@id", id);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					if (!rd.Read())
					{
						return false;
					}
					price = ToInt(rd, 0);
					life = ToInt(rd, 1);
					return true;
				}
			}
		}

		public bool TrySpendTr(int userNum, int price, out int money)
		{
			money = 0;
			if (price < 0)
			{
				return false;
			}
			using (MySqlConnection c = Open())
			using (MySqlTransaction tx = c.BeginTransaction())
			{
				using (MySqlCommand read = new MySqlCommand(
					"SELECT IFNULL(fdGameMoney,0) FROM userinfogame WHERE fdUserNum=@u", c, tx))
				{
					read.Parameters.AddWithValue("@u", userNum);
					object value = read.ExecuteScalar();
					if (value != null && value != DBNull.Value)
					{
						int.TryParse(Convert.ToString(value), out money);
					}
				}
				if (money < price)
				{
					tx.Rollback();
					return false;
				}
				money -= price;
				using (MySqlCommand pay = new MySqlCommand(
					"UPDATE userinfogame SET fdGameMoney=@m WHERE fdUserNum=@u", c, tx))
				{
					pay.Parameters.AddWithValue("@m", money);
					pay.Parameters.AddWithValue("@u", userNum);
					pay.ExecuteNonQuery();
				}
				tx.Commit();
				return true;
			}
		}

		public int Grant(int userNum, int itemNum)
		{
			TlCatalogueItem ticket = FindItem(itemNum);
			TlCatalogueItem granted = ticket;
			int days = 30;
			if (ticket != null && ticket.LeaseDays > 0 && ticket.GrantItemNum != ticket.ItemNum)
			{
				granted = FindItem(ticket.GrantItemNum) ?? ticket;
				days = ticket.LeaseDays;
			}
			int grantedNum = granted != null ? granted.ItemNum : itemNum;
			int character = granted != null ? granted.FdCharacter : 0;
			using (MySqlConnection c = Open())
			using (MySqlTransaction tx = c.BeginTransaction())
			{
				StoreOwned(c, tx, userNum, grantedNum, character, days);
				tx.Commit();
			}
			return grantedNum;
		}

		/// <summary>
		/// A gift of a period ticket is shown as the wearable that accepting it stores.
		/// </summary>
		public int GiftShownItem(int itemNum)
		{
			TlCatalogueItem ticket = FindItem(itemNum);
			if (ticket != null && ticket.LeaseDays > 0 && ticket.GrantItemNum != ticket.ItemNum)
			{
				return ticket.GrantItemNum;
			}
			return itemNum;
		}

		public List<TlGift> Gifts(int userNum, int page, int pageSize)
		{
			List<TlGift> rows = new List<TlGift>();
			if (userNum <= 0)
			{
				return rows;
			}
			int size = pageSize < 1 ? 5 : (pageSize > 20 ? 20 : pageSize);
			int index = page < 1 ? 1 : page;
			int offset = (index - 1) * size;
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdNum, fdSendNickname, fdGiftItemDescNum, fdSendDateTime, IFNULL(fdMemo,'') " +
				"FROM tblgift WHERE fdReceiveUserNum=@u AND (fdExpireDate IS NULL OR fdExpireDate > NOW()) " +
				"ORDER BY fdNum LIMIT " + size + " OFFSET " + offset, c))
			{
				cmd.Parameters.AddWithValue("@u", userNum);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					while (rd.Read())
					{
						rows.Add(ReadGift(rd));
					}
				}
			}
			return rows;
		}

		public TlGift FindGift(int userNum, int giftId)
		{
			if (userNum <= 0 || giftId <= 0)
			{
				return null;
			}
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"SELECT fdNum, fdSendNickname, fdGiftItemDescNum, fdSendDateTime, IFNULL(fdMemo,'') " +
				"FROM tblgift WHERE fdNum=@n AND fdReceiveUserNum=@u AND (fdExpireDate IS NULL OR fdExpireDate > NOW())", c))
			{
				cmd.Parameters.AddWithValue("@n", giftId);
				cmd.Parameters.AddWithValue("@u", userNum);
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					if (!rd.Read())
					{
						return null;
					}
					return ReadGift(rd);
				}
			}
		}

		public void DeleteGift(int userNum, int giftId)
		{
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"DELETE FROM tblgift WHERE fdNum=@n AND fdReceiveUserNum=@u", c))
			{
				cmd.Parameters.AddWithValue("@n", giftId);
				cmd.Parameters.AddWithValue("@u", userNum);
				cmd.ExecuteNonQuery();
			}
		}

		private static TlGift ReadGift(MySqlDataReader rd)
		{
			DateTime sent = rd.IsDBNull(3) ? DateTime.Now : rd.GetDateTime(3);
			return new TlGift
			{
				Id = ToInt(rd, 0),
				Sender = rd.IsDBNull(1) ? "" : rd.GetString(1),
				ItemNum = ToInt(rd, 2),
				SentAt = sent,
				Memo = rd.IsDBNull(4) ? "" : rd.GetString(4)
			};
		}

		public void TakeCapsule(int machine, int itemNum)
		{
			List<TlCapsulePrize> rows;
			if (!_capsule.TryGetValue(machine, out rows))
			{
				return;
			}
			int left = -1;
			foreach (TlCapsulePrize prize in rows)
			{
				if (prize.ItemNum == itemNum && prize.Count > 0)
				{
					prize.Count--;
					left = prize.Count;
					break;
				}
			}
			if (left < 0)
			{
				return;
			}
			using (MySqlConnection c = Open())
			using (MySqlCommand cmd = new MySqlCommand(
				"INSERT INTO gamedatacapsulemachineitemcount (fdMachineNum, fdItemNum, fdItemCount) VALUES (@m,@n,@c) " +
				"ON DUPLICATE KEY UPDATE fdItemCount=@c", c))
			{
				cmd.Parameters.AddWithValue("@m", machine);
				cmd.Parameters.AddWithValue("@n", itemNum);
				cmd.Parameters.AddWithValue("@c", left);
				cmd.ExecuteNonQuery();
			}
		}

		private static void StoreOwned(MySqlConnection c, MySqlTransaction tx, int userNum, int itemNum, int character, int days)
		{
			using (MySqlCommand au = new MySqlCommand(
				"INSERT INTO tblavataruser (fdUserNum, fdItemDescNum, fdCharacter, fdCount, fdUsing, fdGotDateTime, fdExpireDateTime) " +
				"VALUES (@u,@n,@ch,1,0,NOW(),DATE_ADD(NOW(), INTERVAL @d DAY)) " +
				"ON DUPLICATE KEY UPDATE fdCount=fdCount+1, fdExpireDateTime=DATE_ADD(NOW(), INTERVAL @d DAY)", c, tx))
			{
				au.Parameters.AddWithValue("@u", userNum);
				au.Parameters.AddWithValue("@n", itemNum);
				au.Parameters.AddWithValue("@ch", character);
				au.Parameters.AddWithValue("@d", days);
				au.ExecuteNonQuery();
			}
			using (MySqlCommand ii = new MySqlCommand(
				"INSERT INTO tblavatarusercharacter (fdUserNum, fdItemDescNum, fdCharacter, fdCount, fdUsing, fdGotDateTime, fdExpireDateTime) " +
				"VALUES (@u,@n,@ch,1,0,NOW(),DATE_ADD(NOW(), INTERVAL @d DAY)) " +
				"ON DUPLICATE KEY UPDATE fdCount=fdCount+1, fdExpireDateTime=DATE_ADD(NOW(), INTERVAL @d DAY)", c, tx))
			{
				ii.Parameters.AddWithValue("@u", userNum);
				ii.Parameters.AddWithValue("@n", itemNum);
				ii.Parameters.AddWithValue("@ch", character);
				ii.Parameters.AddWithValue("@d", days);
				ii.ExecuteNonQuery();
			}
		}

		public List<KeyValuePair<int, List<KeyValuePair<int, int>>>> UserItemAttrs(int userNum)
		{
			List<KeyValuePair<int, List<KeyValuePair<int, int>>>> items = new List<KeyValuePair<int, List<KeyValuePair<int, int>>>>();
			if (userNum <= 0)
			{
				return items;
			}
			Dictionary<int, Dictionary<int, decimal>> extra = new Dictionary<int, Dictionary<int, decimal>>();
			Dictionary<int, Dictionary<int, decimal>> merged = new Dictionary<int, Dictionary<int, decimal>>();
			using (MySqlConnection c = Open())
			{
				using (MySqlCommand cmd = new MySqlCommand(
					"SELECT fdItemDescNum, fdAttrType, fdAttrValue FROM tblavataruserattr WHERE fdUserNum=@u ORDER BY fdItemDescNum, fdAttrType", c))
				{
					cmd.Parameters.AddWithValue("@u", userNum);
					using (MySqlDataReader rd = cmd.ExecuteReader())
					{
						while (rd.Read())
						{
							PutAttr(extra, rd.GetInt32(0), rd.GetInt32(1), AttrDecimal(rd.GetValue(2)));
						}
					}
				}
				if (extra.Count == 0)
				{
					return items;
				}
				List<int> ids = new List<int>(extra.Keys);
				string list = string.Join(",", ids.ConvertAll(delegate(int n) { return n.ToString(); }).ToArray());
				using (MySqlCommand cmd = new MySqlCommand(
					"SELECT fdItemNum, fdAttr, fdValue FROM tblavataritemdescattr WHERE fdItemNum IN (" + list + ")", c))
				using (MySqlDataReader rd = cmd.ExecuteReader())
				{
					while (rd.Read())
					{
						PutAttr(merged, rd.GetInt32(0), rd.GetInt32(1), AttrDecimal(rd.GetValue(2)));
					}
				}
			}
			foreach (KeyValuePair<int, Dictionary<int, decimal>> item in extra)
			{
				foreach (KeyValuePair<int, decimal> attr in item.Value)
				{
					decimal baseValue = 0m;
					Dictionary<int, decimal> row;
					if (merged.TryGetValue(item.Key, out row))
					{
						row.TryGetValue(attr.Key, out baseValue);
					}
					PutAttr(merged, item.Key, attr.Key, baseValue + attr.Value);
				}
			}
			foreach (KeyValuePair<int, Dictionary<int, decimal>> item in merged)
			{
				List<KeyValuePair<int, int>> attrs = new List<KeyValuePair<int, int>>();
				foreach (KeyValuePair<int, decimal> attr in item.Value)
				{
					attrs.Add(new KeyValuePair<int, int>(attr.Key, FloatBits(attr.Value)));
				}
				items.Add(new KeyValuePair<int, List<KeyValuePair<int, int>>>(item.Key, attrs));
			}
			return items;
		}

		private static decimal AttrDecimal(object raw)
		{
			decimal value;
			if (decimal.TryParse(Convert.ToString(raw), out value))
			{
				return value;
			}
			return 0m;
		}

		private static void PutAttr(Dictionary<int, Dictionary<int, decimal>> items, int item, int attr, decimal value)
		{
			Dictionary<int, decimal> row;
			if (!items.TryGetValue(item, out row))
			{
				row = new Dictionary<int, decimal>();
				items.Add(item, row);
			}
			row[attr] = value;
		}

		public void SetItemWorn(int userNum, int itemNum, bool worn)
		{
			using (MySqlConnection c = Open())
			{
				using (MySqlCommand au = new MySqlCommand(
					"UPDATE tblavataruser SET fdUsing=@on WHERE fdUserNum=@u AND fdItemDescNum=@n", c))
				{
					au.Parameters.AddWithValue("@on", worn ? 1 : 0);
					au.Parameters.AddWithValue("@u", userNum);
					au.Parameters.AddWithValue("@n", itemNum);
					au.ExecuteNonQuery();
				}
				using (MySqlCommand ii = new MySqlCommand(
					"UPDATE tblavatarusercharacter SET fdUsing=@on WHERE fdUserNum=@u AND fdItemDescNum=@n", c))
				{
					ii.Parameters.AddWithValue("@on", worn ? 1 : 0);
					ii.Parameters.AddWithValue("@u", userNum);
					ii.Parameters.AddWithValue("@n", itemNum);
					ii.ExecuteNonQuery();
				}
			}
		}

		public TlCharacter CreateCharacter(int accountId, string nickname, int modelId, int setNum, List<TlItem> items)
		{
			using (MySqlConnection c = Open())
			using (MySqlTransaction tx = c.BeginTransaction())
			{
				try
				{
					using (MySqlCommand taken = new MySqlCommand(
						"SELECT fdUserNum FROM userinfo WHERE fdNickname=@n AND fdUserNum<>@id LIMIT 1", c, tx))
					{
						taken.Parameters.AddWithValue("@n", nickname);
						taken.Parameters.AddWithValue("@id", accountId);
						object other = taken.ExecuteScalar();
						if (other != null && other != DBNull.Value)
						{
							tx.Rollback();
							return null;
						}
					}
					using (MySqlCommand upd = new MySqlCommand(
						"UPDATE userinfo SET fdNickname=@n WHERE fdUserNum=@id AND (fdNickname IS NULL OR fdNickname='')", c, tx))
					{
						upd.Parameters.AddWithValue("@n", nickname);
						upd.Parameters.AddWithValue("@id", accountId);
						if (upd.ExecuteNonQuery() != 1)
						{
							tx.Rollback();
							return null;
						}
					}
					using (MySqlCommand game = new MySqlCommand(
						"INSERT INTO userinfogame (fdUserNum, fdExp, fdGameMoney, fdAvatarCharacterSettingNum, fdLikeable) VALUES (@id,0,0,@s,0) " +
						"ON DUPLICATE KEY UPDATE fdAvatarCharacterSettingNum=@s", c, tx))
					{
						game.Parameters.AddWithValue("@id", accountId);
						game.Parameters.AddWithValue("@s", setNum);
						game.ExecuteNonQuery();
					}
					foreach (TlItem it in items)
					{
						using (MySqlCommand ii = new MySqlCommand(
							"INSERT INTO tblavatarusercharacter (fdUserNum, fdItemDescNum, fdCharacter, fdCount, fdUsing, fdGotDateTime) VALUES (@u,@n,@ch,1,0,FROM_UNIXTIME(@t))", c, tx))
						{
							ii.Parameters.AddWithValue("@u", accountId);
							ii.Parameters.AddWithValue("@n", it.ItemNum);
							ii.Parameters.AddWithValue("@ch", it.FdCharacter);
							ii.Parameters.AddWithValue("@t", it.AcquiredAt);
							ii.ExecuteNonQuery();
						}
						using (MySqlCommand au = new MySqlCommand(
							"INSERT INTO tblavataruser (fdUserNum, fdItemDescNum, fdCharacter, fdCount, fdUsing, fdGotDateTime) VALUES (@u,@n,@ch,1,0,FROM_UNIXTIME(@t))", c, tx))
						{
							au.Parameters.AddWithValue("@u", accountId);
							au.Parameters.AddWithValue("@n", it.ItemNum);
							au.Parameters.AddWithValue("@ch", it.FdCharacter);
							au.Parameters.AddWithValue("@t", it.AcquiredAt);
							au.ExecuteNonQuery();
						}
					}
					tx.Commit();
					return new TlCharacter
					{
						Id = accountId,
						AccountId = accountId,
						Nickname = nickname,
						ModelId = modelId,
						CreatorSetNum = setNum,
						Items = items
					};
				}
				catch (MySqlException)
				{
					tx.Rollback();
					return null;
				}
			}
		}
	}
}
