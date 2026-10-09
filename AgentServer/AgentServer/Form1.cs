using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgentServer.Database;
using AgentServer.Dialog;
using AgentServer.Holders;
using AgentServer.Network.Connections;
using AgentServer.Packet.Send;
using AgentServer.Structuring;
using Akka.Actor;
using Akka.Configuration;
using Akka.Quartz.Actor;
using Akka.Quartz.Actor.Commands;
using IniParser;
using IniParser.Model;
using MySql.Data.MySqlClient;
using NetMsg.LBS;
using NetMsg.Room;
using Quartz;
using Serilog;
using Serilog.Events;
using TRCommon;

namespace AgentServer
{
	public class Form1 : Form
	{
		private delegate void EnableDelegate(int id);

		private CapsuleMachineManager cpm;

		private ServerSettingManager ssm;

		private GMTool gmtool;

		private System.Threading.Timer sockettimer;

		private System.Threading.Timer checktimer;

		private System.Threading.Timer checktimer2;

		private static Form1 form;

		private IContainer components;

		private RichTextBox richTextBox1;

		private Button btnStopServer;

		private Label label1;

		private Label label2;

		private TextBox txtNotice;

		private Button btnNotice;

		private Button btnOpenHash;

		private Button btnOpenDir;

		private Button btnShowUserNum;

		private Button btnReloadtblServerSettingInfo;

		private Button btnCapsuleMachineManager;

		private Button btnReloadMap;

		private Button btnGMTool;

		private Button btnReloadHash;

		private Label label5;

		private Label label6;

        private Button btnServerSettingManager;
		private Label label4;
		private Label labelServerBuild;
		private Label labelServerIdValue;
		private Label labelServerBuildCaption;
		private Label titleLabel;
		private Button btnClose;
		private Panel logScrollRail;
		private Panel logScrollThumb;

		// TRPkgTools dark palette
		static readonly Color ThemeBg = Color.FromArgb(22, 22, 24);
		static readonly Color ThemePanel = Color.FromArgb(32, 32, 36);
		static readonly Color ThemeBorder = Color.FromArgb(58, 58, 64);
		static readonly Color ThemeText = Color.FromArgb(232, 232, 236);
		static readonly Color ThemeMuted = Color.FromArgb(150, 150, 158);
		static readonly Color ThemeAccent = Color.FromArgb(88, 166, 255);
		static readonly Color ThemeAccentText = Color.FromArgb(16, 16, 18);
		static readonly Color ThemeDanger = Color.FromArgb(180, 60, 60);
		static readonly Color ThemeErr = Color.FromArgb(232, 110, 110);
		static readonly Color ThemeSelect = Color.FromArgb(48, 72, 104);
		static readonly Color ThemeScroll = Color.FromArgb(64, 64, 72);
		static readonly Color ThemeScrollThumb = Color.FromArgb(72, 72, 80);

		Point _dragOffset;
		bool _dragging;
		bool _thumbDrag;
		int _thumbDragOffsetY;
		bool _logUiBusy;
		DateTime _lastScrollSyncUtc = DateTime.MinValue;

		[DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
		private static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList);

		[DllImport("user32.dll")]
		private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

		[DllImport("user32.dll")]
		private static extern bool GetScrollInfo(IntPtr hwnd, int nBar, ref SCROLLINFO lpsi);

		[DllImport("user32.dll")]
		private static extern int SetScrollPos(IntPtr hWnd, int nBar, int nPos, bool bRedraw);

		[DllImport("user32.dll")]
		private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

		private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

		private const int SB_VERT = 1;
		private const int WM_VSCROLL = 0x0115;
		private const int SB_THUMBPOSITION = 4;
		private const uint SIF_RANGE = 0x1;
		private const uint SIF_PAGE = 0x2;
		private const uint SIF_POS = 0x4;

		[StructLayout(LayoutKind.Sequential)]
		private struct SCROLLINFO
		{
			public uint cbSize;
			public uint fMask;
			public int nMin;
			public int nMax;
			public uint nPage;
			public int nPos;
			public int nTrackPos;
		}

		public Form1()
		{
			InitializeComponent();
			ApplyDarkTheme();
			SetupBorderlessChrome();
			SetupDarkLogScroll();
			form = this;
		}

		private void SetupDarkLogScroll()
		{
			// Use native scrollbar only. Custom overlay + GetScrollInfo/VScroll sync
			// was contributing to UI freezes under log load.
			richTextBox1.ScrollBars = RichTextBoxScrollBars.Vertical;
			richTextBox1.HandleCreated += (_, __) => ApplyDarkScrollBars(richTextBox1);
			if (richTextBox1.IsHandleCreated)
			{
				ApplyDarkScrollBars(richTextBox1);
			}
		}

		private bool TryGetLogScroll(out int pos, out int max, out int page)
		{
			pos = 0;
			max = 0;
			page = 0;
			if (!richTextBox1.IsHandleCreated)
			{
				return false;
			}
			SCROLLINFO si = new SCROLLINFO
			{
				cbSize = (uint)Marshal.SizeOf(typeof(SCROLLINFO)),
				fMask = SIF_RANGE | SIF_PAGE | SIF_POS
			};
			if (!GetScrollInfo(richTextBox1.Handle, SB_VERT, ref si))
			{
				return false;
			}
			pos = si.nPos;
			max = si.nMax;
			page = (int)si.nPage;
			return true;
		}

		private void SyncLogScrollThumb()
		{
			if (logScrollRail == null || logScrollThumb == null || _thumbDrag)
			{
				return;
			}
			if (!TryGetLogScroll(out int pos, out int max, out int page))
			{
				logScrollThumb.Visible = false;
				return;
			}
			int range = Math.Max(1, max - page + 1);
			bool needed = max > page && page > 0;
			logScrollThumb.Visible = needed;
			logScrollRail.Visible = true;
			if (!needed)
			{
				return;
			}
			int track = Math.Max(20, logScrollRail.Height);
			int thumbH = Math.Max(24, (int)(track * ((double)page / (max + 1))));
			thumbH = Math.Min(track, thumbH);
			int maxTop = Math.Max(0, track - thumbH);
			int top = (int)(maxTop * ((double)pos / Math.Max(1, range - 1)));
			logScrollThumb.Height = thumbH;
			logScrollThumb.Top = Math.Max(0, Math.Min(maxTop, top));
		}

		private void ScrollLogToThumb()
		{
			if (!TryGetLogScroll(out _, out int max, out int page))
			{
				return;
			}
			int range = Math.Max(1, max - page + 1);
			int maxTop = Math.Max(1, logScrollRail.Height - logScrollThumb.Height);
			int pos = (int)((range - 1) * ((double)logScrollThumb.Top / maxTop));
			pos = Math.Max(0, Math.Min(range - 1, pos));
			SetScrollPos(richTextBox1.Handle, SB_VERT, pos, true);
			SendMessage(richTextBox1.Handle, WM_VSCROLL, (IntPtr)(SB_THUMBPOSITION | (pos << 16)), IntPtr.Zero);
			richTextBox1.Invalidate();
		}

		private static void ApplyDarkScrollBars(Control control)
		{
			if (control == null || !control.IsHandleCreated)
			{
				return;
			}
			SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
			EnumChildWindows(control.Handle, (hwnd, _) =>
			{
				StringBuilder name = new StringBuilder(64);
				GetClassName(hwnd, name, name.Capacity);
				if (string.Equals(name.ToString(), "ScrollBar", StringComparison.OrdinalIgnoreCase))
				{
					SetWindowTheme(hwnd, "DarkMode_Explorer", null);
				}
				return true;
			}, IntPtr.Zero);
		}

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams cp = base.CreateParams;
				cp.ClassStyle |= 0x20000;
				return cp;
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			using (Pen pen = new Pen(ThemeBorder))
			{
				e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
			}
		}

		private void SetupBorderlessChrome()
		{
			DoubleBuffered = true;
			MouseDown += OnDragStart;
			MouseMove += OnDragMove;
			MouseUp += OnDragEnd;
			foreach (Control c in new Control[] { titleLabel, label6, labelServerIdValue, label1, label2, labelServerBuildCaption, labelServerBuild })
			{
				c.MouseDown += OnDragStart;
				c.MouseMove += OnDragMove;
				c.MouseUp += OnDragEnd;
			}
		}

		private void OnDragStart(object sender, MouseEventArgs e)
		{
			if (e.Button != MouseButtons.Left)
			{
				return;
			}
			_dragging = true;
			_dragOffset = e.Location;
			if (sender != this && sender is Control c)
			{
				_dragOffset = new Point(e.X + c.Left, e.Y + c.Top);
			}
		}

		private void OnDragMove(object sender, MouseEventArgs e)
		{
			if (!_dragging)
			{
				return;
			}
			Point screen = PointToScreen(e.Location);
			if (sender != this && sender is Control c)
			{
				screen = c.PointToScreen(e.Location);
			}
			Location = new Point(screen.X - _dragOffset.X, screen.Y - _dragOffset.Y);
		}

		private void OnDragEnd(object sender, MouseEventArgs e)
		{
			_dragging = false;
		}

		private void btnClose_Click(object sender, EventArgs e)
		{
			btnStopServer_Click(sender, e);
		}

		private void ApplyDarkTheme()
		{
			BackColor = ThemeBg;
			ForeColor = ThemeText;
			Font = new Font("Segoe UI", 9F);

			richTextBox1.BackColor = ThemePanel;
			richTextBox1.ForeColor = ThemeText;
			richTextBox1.BorderStyle = BorderStyle.None;
			// Match selection colors to the log background so any residual selection is invisible.
			richTextBox1.SelectionBackColor = ThemePanel;
			richTextBox1.SelectionColor = ThemeText;
			richTextBox1.HideSelection = true;

			txtNotice.BackColor = ThemePanel;
			txtNotice.ForeColor = ThemeText;
			txtNotice.BorderStyle = BorderStyle.FixedSingle;

			foreach (Control c in Controls)
			{
				if (c is Label lbl)
				{
					lbl.BackColor = Color.Transparent;
					bool value = ReferenceEquals(lbl, label2)
						|| ReferenceEquals(lbl, labelServerIdValue)
						|| ReferenceEquals(lbl, labelServerBuild)
						|| ReferenceEquals(lbl, titleLabel);
					if (ReferenceEquals(lbl, label5))
					{
						lbl.ForeColor = ThemeErr;
					}
					else if (value)
					{
						lbl.ForeColor = ThemeAccent;
					}
					else
					{
						lbl.ForeColor = ThemeMuted;
					}
				}
				else if (c is CheckBox chk)
				{
					chk.BackColor = ThemeBg;
					chk.ForeColor = ThemeMuted;
					chk.FlatStyle = FlatStyle.Standard;
					chk.UseVisualStyleBackColor = false;
				}
				else if (c is Button btn)
				{
					bool primary = ReferenceEquals(btn, btnNotice);
					bool danger = ReferenceEquals(btn, btnStopServer) || ReferenceEquals(btn, btnClose);
					btn.FlatStyle = FlatStyle.Flat;
					btn.FlatAppearance.BorderSize = 0;
					btn.Cursor = Cursors.Hand;
					btn.UseVisualStyleBackColor = false;
					if (primary)
					{
						btn.BackColor = ThemeAccent;
						btn.ForeColor = ThemeAccentText;
						btn.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
						btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(110, 180, 255);
					}
					else if (danger)
					{
						btn.BackColor = ReferenceEquals(btn, btnClose) ? ThemePanel : ThemeDanger;
						btn.ForeColor = ThemeText;
						btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 70, 70);
					}
					else
					{
						btn.BackColor = ThemePanel;
						btn.ForeColor = ThemeText;
						btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 48, 54);
					}
				}
			}

			label4.BackColor = ThemeBg;
			label4.ForeColor = ThemeMuted;
		}

		private bool _bootStarted;

		private void Form1_Load(object sender, EventArgs e)
		{
			// Boot on Shown (not Activated): Cursor/background launches often never
			// raise Activated, so the UI sat empty and the server never listened.
			Shown += Form1_ShownBoot;
		}

		private void Form1_ShownBoot(object sender, EventArgs e)
		{
			Shown -= Form1_ShownBoot;
			BeginBoot();
		}

		private void BeginBoot()
		{
			if (_bootStarted)
			{
				return;
			}
			_bootStarted = true;
			ShowInTaskbar = true;
			if (WindowState == FormWindowState.Minimized)
			{
				WindowState = FormWindowState.Normal;
			}
			BringToFront();
			Activate();
			Init(this, EventArgs.Empty);
		}

		private void Init(object sender, EventArgs e)
		{
			string dates = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_Agent.log";
			InMemorySink logEventSink = new InMemorySink(richTextBox1);
			// UI sink: Information+ only. Debug/Akka spam was freezing the WinForms UI (Not Responding).
			Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
				.MinimumLevel.Override("Quartz", LogEventLevel.Warning)
				.MinimumLevel.Override("Akka", LogEventLevel.Warning)
				.WriteTo.Sink(logEventSink, restrictedToMinimumLevel: LogEventLevel.Information)
				.WriteTo.Logger(delegate(LoggerConfiguration l)
			{
				l.Filter.ByIncludingOnly((LogEvent e1) => e1.Level == LogEventLevel.Information).WriteTo.File(".\\Logs\\Agent\\Info\\" + dates, LogEventLevel.Verbose, "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", null, 1073741824L, null, buffered: false, shared: false, null, RollingInterval.Infinite, rollOnFileSizeLimit: false, 31);
			}).WriteTo.Logger(delegate(LoggerConfiguration l)
			{
				l.Filter.ByIncludingOnly((LogEvent e1) => e1.Level == LogEventLevel.Warning).WriteTo.File(".\\Logs\\Agent\\Warning\\" + dates, LogEventLevel.Verbose, "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", null, 1073741824L, null, buffered: false, shared: false, null, RollingInterval.Infinite, rollOnFileSizeLimit: false, 31);
			}).WriteTo.Logger(delegate(LoggerConfiguration l)
			{
				l.Filter.ByIncludingOnly((LogEvent e1) => e1.Level == LogEventLevel.Error).WriteTo.File(".\\Logs\\Agent\\Error\\" + dates, LogEventLevel.Verbose, "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", null, 1073741824L, null, buffered: false, shared: false, null, RollingInterval.Infinite, rollOnFileSizeLimit: false, 31);
			}).CreateLogger();
			if (!Boot())
			{
				return;
			}
			Text = "TalesLand Server";
			titleLabel.Text = Text;
			TalesLand.TlLog.SinkInfo = delegate(string m) { Log.Information(m); };
			TalesLand.TlLog.SinkWarn = delegate(string m) { Log.Warning(m); };
			try
			{
				TalesLand.TlAgentHost.Start(Conf.ServerIP, Conf.AgentPort, Conf.Connstr);
				Log.Information("AgentServer listening on {0}:{1} (TalesLand, talesland_db)", Conf.ServerIP, Conf.AgentPort);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "AgentServer failed to start");
			}
		}

		private static string RequiredIni(IniData ini, string section, string key)
		{
			try
			{
				if (ini == null || ini[section] == null || !ini[section].ContainsKey(key))
				{
					throw new InvalidOperationException("settings.ini [" + section + "] is missing " + key);
				}
				return ini[section][key];
			}
			catch (KeyNotFoundException)
			{
				throw new InvalidOperationException("settings.ini [" + section + "] is missing " + key);
			}
		}

		private bool Boot()
		{
			try
			{
				Text = "TalesLand Server";
				titleLabel.Text = Text;
				IniData iniData = new FileIniDataParser().ReadFile("settings.ini");
				Conf.ServerIP = RequiredIni(iniData, "Server", "AgentServerIP").Replace(" ", "");
				Conf.AgentPort = Convert.ToUInt16(RequiredIni(iniData, "Server", "AgentServerTCPPort").Replace(" ", ""));
				Conf.Connstr = RequiredIni(iniData, "Server", "MySQLConnection");
				TalesLand.TlLog.DebugMode = iniData["Server"].ContainsKey("DebugMode")
					&& Convert.ToBoolean(iniData["Server"]["DebugMode"].Replace(" ", ""));
				Log.Information("Loading Settings.ini........Done");
				string buildStamp = GetAssemblyBuildStamp();
				Log.Information("ServerBuild={0}", buildStamp);
				label2.Text = Conf.ServerIP + ":" + Conf.AgentPort;
				labelServerBuildCaption.Text = "ServerBuild:";
				labelServerBuild.Text = buildStamp;
				MySqlConnection mySqlConnection = CreateConnection();
				if (mySqlConnection == null)
				{
					return false;
				}
				mySqlConnection.Close();
				// Heavy DB init runs in Task.Run below so the form stays responsive.
				Log.Information("Settings OK; loading DB in background...");
				return true;
			}
			catch (Exception ex)
			{
				Log.Error(ex, "AgentServer boot failed");
			}
			return false;
		}

		private void SocketTimeoutCheck()
		{
		}

		private void btnStopServer_Click(object sender, EventArgs e)
		{
			if (DarkUi.Confirm(this, "Do you want to shut down the server?", "Shut Down") != DialogResult.Yes)
			{
				return;
			}
			foreach (Account item in ClientConnection.CurrentAccounts.Values.Where((Account w) => w.isLogin))
			{
				HandleLogout(item);
			}
			Close();
		}

		private void btnNotice_Click(object sender, EventArgs e)
		{
			string text = txtNotice != null ? txtNotice.Text : "";
			if (text.Length == 0)
			{
				return;
			}
			TalesLand.TlAgentHost.BroadcastNotice(text);
			Log.Information("Send notice {0}", text);
		}

		private void btnOpenHash_Click(object sender, EventArgs e)
		{
			Process.Start("hash.ini");
		}

		private void btnOpenDir_Click(object sender, EventArgs e)
		{
			string startupPath = Application.StartupPath;
			Process.Start("explorer.exe", startupPath);
		}

		private void btnShowUserNum_Click(object sender, EventArgs e)
		{
			Log.Information(TalesLand.TlAgentHost.OnlineSummary());
		}

		private void btnReloadtblServerSettingInfo_Click(object sender, EventArgs e)
		{
			try
			{
				TalesLand.TlAgentHost.ReloadServerSettings();
				Log.Information("Reloaded tblServerSettingInfo!");
			}
			catch (Exception ex)
			{
				Log.Error("Reload tblServerSettingInfo: {0}", ex.Message);
			}
		}

		private void btnCapsuleMachineManager_Click(object sender, EventArgs e)
		{
			if (cpm == null || cpm.IsDisposed)
			{
				cpm = new CapsuleMachineManager();
			}
			cpm.Show();
			cpm.Focus();
		}

		private void btnReloadMap_Click(object sender, EventArgs e)
		{
			try
			{
				int maps = TalesLand.TlAgentHost.ReloadMaps();
				Log.Information("Reloaded map info: {0}", maps);
			}
			catch (Exception ex)
			{
				Log.Error("Reload map: {0}", ex.Message);
			}
		}

		private void btnGMTool_Click(object sender, EventArgs e)
		{
			if (gmtool == null || gmtool.IsDisposed)
			{
				gmtool = new GMTool();
			}
			gmtool.Show();
			gmtool.Focus();
		}

		private void btnReloadHash_Click(object sender, EventArgs e)
		{
			try
			{
				ServerSettingHolder.LoadHashList();
			}
			catch (Exception ex)
			{
				Log.Error("Reload hash: {0}", ex.Message);
			}
		}

		private void HandleLogout(Account User)
		{
		}

		private MySqlConnection CreateConnection()
		{
			try
			{
				using MySqlConnection mySqlConnection = new MySqlConnection(Conf.Connstr);
				mySqlConnection.Open();
				return mySqlConnection;
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Error on DB connect");
				return null;
			}
		}

		public static void UpdateLableStatic(int ID)
		{
			if (form != null)
			{
				form.UpdateLable(ID);
			}
		}

		private void UpdateLable(int ID)
		{
			if (base.InvokeRequired)
			{
				Invoke(new EnableDelegate(UpdateLable), ID);
			}
			else
			{
				labelServerIdValue.Text = ID.ToString();
			}
		}

		private static string GetAssemblyBuildStamp()
		{
			try
			{
				string path = Assembly.GetEntryAssembly()?.Location;
				if (!string.IsNullOrEmpty(path) && File.Exists(path))
				{
					// InvariantCulture: Thai locale uses Buddhist year (2569) for "yyyy".
					return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
				}
			}
			catch
			{
			}
			return "unknown";
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && components != null)
			{
				components.Dispose();
			}
			base.Dispose(disposing);
		}

		private void InitializeComponent()
		{
            this.richTextBox1 = new System.Windows.Forms.RichTextBox();
            this.btnStopServer = new System.Windows.Forms.Button();
            this.label1 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.txtNotice = new System.Windows.Forms.TextBox();
            this.btnNotice = new System.Windows.Forms.Button();
            this.btnOpenHash = new System.Windows.Forms.Button();
            this.btnOpenDir = new System.Windows.Forms.Button();
            this.btnShowUserNum = new System.Windows.Forms.Button();
            this.btnReloadtblServerSettingInfo = new System.Windows.Forms.Button();
            this.btnCapsuleMachineManager = new System.Windows.Forms.Button();
            this.btnReloadMap = new System.Windows.Forms.Button();
            this.btnGMTool = new System.Windows.Forms.Button();
            this.btnReloadHash = new System.Windows.Forms.Button();
            this.label5 = new System.Windows.Forms.Label();
            this.label6 = new System.Windows.Forms.Label();
            this.labelServerIdValue = new System.Windows.Forms.Label();
            this.labelServerBuildCaption = new System.Windows.Forms.Label();
            this.labelServerBuild = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.btnServerSettingManager = new System.Windows.Forms.Button();
            this.titleLabel = new System.Windows.Forms.Label();
            this.btnClose = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // richTextBox1
            // 
            this.richTextBox1.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(32)))), ((int)(((byte)(32)))), ((int)(((byte)(36)))));
            this.richTextBox1.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.richTextBox1.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(232)))), ((int)(((byte)(232)))), ((int)(((byte)(236)))));
            this.richTextBox1.Location = new System.Drawing.Point(12, 56);
            this.richTextBox1.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.richTextBox1.Name = "richTextBox1";
            this.richTextBox1.ReadOnly = true;
            this.richTextBox1.Size = new System.Drawing.Size(731, 475);
            this.richTextBox1.TabIndex = 0;
            this.richTextBox1.Text = "";
            // 
            // btnStopServer
            // 
            this.btnStopServer.Location = new System.Drawing.Point(608, 578);
            this.btnStopServer.Name = "btnStopServer";
            this.btnStopServer.Size = new System.Drawing.Size(137, 31);
            this.btnStopServer.TabIndex = 1;
            this.btnStopServer.Text = "Shut Down";
            this.btnStopServer.UseVisualStyleBackColor = true;
            this.btnStopServer.Click += new System.EventHandler(this.btnStopServer_Click);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(130, 34);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(52, 15);
            this.label1.TabIndex = 2;
            this.label1.Text = "ServerIP:";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(190, 34);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(79, 15);
            this.label2.TabIndex = 3;
            this.label2.Text = "127.0.0.1:9153";
            // 
            // txtNotice
            // 
            this.txtNotice.Location = new System.Drawing.Point(10, 619);
            this.txtNotice.Name = "txtNotice";
            this.txtNotice.Size = new System.Drawing.Size(615, 23);
            this.txtNotice.TabIndex = 5;
            // 
            // btnNotice
            // 
            this.btnNotice.Location = new System.Drawing.Point(633, 615);
            this.btnNotice.Name = "btnNotice";
            this.btnNotice.Size = new System.Drawing.Size(108, 31);
            this.btnNotice.TabIndex = 6;
            this.btnNotice.Text = "Send Notice";
            this.btnNotice.UseVisualStyleBackColor = true;
            this.btnNotice.Click += new System.EventHandler(this.btnNotice_Click);
            // 
            // btnOpenHash
            // 
            this.btnOpenHash.Location = new System.Drawing.Point(221, 578);
            this.btnOpenHash.Name = "btnOpenHash";
            this.btnOpenHash.Size = new System.Drawing.Size(101, 31);
            this.btnOpenHash.TabIndex = 7;
            this.btnOpenHash.Text = "Open Hash";
            this.btnOpenHash.UseVisualStyleBackColor = true;
            this.btnOpenHash.Click += new System.EventHandler(this.btnOpenHash_Click);
            // 
            // btnOpenDir
            // 
            this.btnOpenDir.Location = new System.Drawing.Point(129, 578);
            this.btnOpenDir.Name = "btnOpenDir";
            this.btnOpenDir.Size = new System.Drawing.Size(86, 31);
            this.btnOpenDir.TabIndex = 8;
            this.btnOpenDir.Text = "Open Dir";
            this.btnOpenDir.UseVisualStyleBackColor = true;
            this.btnOpenDir.Click += new System.EventHandler(this.btnOpenDir_Click);
            // 
            // btnShowUserNum
            // 
            this.btnShowUserNum.Location = new System.Drawing.Point(10, 578);
            this.btnShowUserNum.Name = "btnShowUserNum";
            this.btnShowUserNum.Size = new System.Drawing.Size(113, 31);
            this.btnShowUserNum.TabIndex = 9;
            this.btnShowUserNum.Text = "Show User Num";
            this.btnShowUserNum.UseVisualStyleBackColor = true;
            this.btnShowUserNum.Click += new System.EventHandler(this.btnShowUserNum_Click);
            // 
            // btnReloadtblServerSettingInfo
            // 
            this.btnReloadtblServerSettingInfo.Location = new System.Drawing.Point(10, 542);
            this.btnReloadtblServerSettingInfo.Name = "btnReloadtblServerSettingInfo";
            this.btnReloadtblServerSettingInfo.Size = new System.Drawing.Size(205, 30);
            this.btnReloadtblServerSettingInfo.TabIndex = 11;
            this.btnReloadtblServerSettingInfo.Text = "Reload tblServerSettingInfo";
            this.btnReloadtblServerSettingInfo.UseVisualStyleBackColor = true;
            this.btnReloadtblServerSettingInfo.Click += new System.EventHandler(this.btnReloadtblServerSettingInfo_Click);
            // 
            // btnCapsuleMachineManager
            // 
            this.btnCapsuleMachineManager.Location = new System.Drawing.Point(221, 542);
            this.btnCapsuleMachineManager.Name = "btnCapsuleMachineManager";
            this.btnCapsuleMachineManager.Size = new System.Drawing.Size(187, 30);
            this.btnCapsuleMachineManager.TabIndex = 12;
            this.btnCapsuleMachineManager.Text = "CapsuleMachine Manager";
            this.btnCapsuleMachineManager.UseVisualStyleBackColor = true;
            this.btnCapsuleMachineManager.Click += new System.EventHandler(this.btnCapsuleMachineManager_Click);
            // 
            // btnReloadMap
            // 
            this.btnReloadMap.Location = new System.Drawing.Point(328, 578);
            this.btnReloadMap.Name = "btnReloadMap";
            this.btnReloadMap.Size = new System.Drawing.Size(119, 31);
            this.btnReloadMap.TabIndex = 14;
            this.btnReloadMap.Text = "Reload MapInfo";
            this.btnReloadMap.UseVisualStyleBackColor = true;
            this.btnReloadMap.Click += new System.EventHandler(this.btnReloadMap_Click);
            // 
            // btnGMTool
            // 
            this.btnGMTool.Location = new System.Drawing.Point(583, 542);
            this.btnGMTool.Name = "btnGMTool";
            this.btnGMTool.Size = new System.Drawing.Size(96, 30);
            this.btnGMTool.TabIndex = 15;
            this.btnGMTool.Text = "GM Tool";
            this.btnGMTool.UseVisualStyleBackColor = true;
            this.btnGMTool.Click += new System.EventHandler(this.btnGMTool_Click);
            // 
            // btnReloadHash
            // 
            this.btnReloadHash.Location = new System.Drawing.Point(453, 578);
            this.btnReloadHash.Name = "btnReloadHash";
            this.btnReloadHash.Size = new System.Drawing.Size(110, 31);
            this.btnReloadHash.TabIndex = 17;
            this.btnReloadHash.Text = "Reload Hash";
            this.btnReloadHash.UseVisualStyleBackColor = true;
            this.btnReloadHash.Click += new System.EventHandler(this.btnReloadHash_Click);
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Bold);
            this.label5.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(232)))), ((int)(((byte)(110)))), ((int)(((byte)(110)))));
            this.label5.Location = new System.Drawing.Point(85, 559);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(0, 21);
            this.label5.TabIndex = 24;
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(12, 34);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(53, 15);
            this.label6.TabIndex = 30;
            this.label6.Text = "ServerID:";
            // 
            // labelServerIdValue
            // 
            this.labelServerIdValue.AutoSize = true;
            this.labelServerIdValue.Location = new System.Drawing.Point(72, 34);
            this.labelServerIdValue.Name = "labelServerIdValue";
            this.labelServerIdValue.Size = new System.Drawing.Size(13, 15);
            this.labelServerIdValue.TabIndex = 31;
            this.labelServerIdValue.Text = "0";
            // 
            // labelServerBuildCaption
            // 
            this.labelServerBuildCaption.AutoSize = true;
            this.labelServerBuildCaption.Location = new System.Drawing.Point(320, 34);
            this.labelServerBuildCaption.Name = "labelServerBuildCaption";
            this.labelServerBuildCaption.Size = new System.Drawing.Size(69, 15);
            this.labelServerBuildCaption.TabIndex = 32;
            this.labelServerBuildCaption.Text = "ServerBuild:";
            // 
            // labelServerBuild
            // 
            this.labelServerBuild.AutoSize = true;
            this.labelServerBuild.Location = new System.Drawing.Point(400, 34);
            this.labelServerBuild.Name = "labelServerBuild";
            this.labelServerBuild.Size = new System.Drawing.Size(19, 15);
            this.labelServerBuild.TabIndex = 34;
            this.labelServerBuild.Text = "—";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(22)))), ((int)(((byte)(22)))), ((int)(((byte)(24)))));
            this.label4.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.label4.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(150)))), ((int)(((byte)(150)))), ((int)(((byte)(158)))));
            this.label4.Location = new System.Drawing.Point(503, 766);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(0, 15);
            this.label4.TabIndex = 33;
            this.label4.Visible = false;
            // 
            // btnServerSettingManager
            // 
            this.btnServerSettingManager.Location = new System.Drawing.Point(414, 542);
            this.btnServerSettingManager.Name = "btnServerSettingManager";
            this.btnServerSettingManager.Size = new System.Drawing.Size(163, 30);
            this.btnServerSettingManager.TabIndex = 38;
            this.btnServerSettingManager.Text = "ServerSetting Manager";
            this.btnServerSettingManager.UseVisualStyleBackColor = true;
            this.btnServerSettingManager.Click += new System.EventHandler(this.btnServerSettingManager_Click);
            // 
            // titleLabel
            // 
            this.titleLabel.AutoSize = true;
            this.titleLabel.Font = new System.Drawing.Font("Segoe UI", 11F, System.Drawing.FontStyle.Bold);
            this.titleLabel.Location = new System.Drawing.Point(12, 10);
            this.titleLabel.Name = "titleLabel";
            this.titleLabel.Size = new System.Drawing.Size(314, 20);
            this.titleLabel.TabIndex = 50;
            this.titleLabel.Text = "TalesLand Server";
            this.titleLabel.Click += new System.EventHandler(this.titleLabel_Click);
            // 
            // btnClose
            // 
            this.btnClose.FlatAppearance.BorderSize = 0;
            this.btnClose.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnClose.Location = new System.Drawing.Point(715, 8);
            this.btnClose.Name = "btnClose";
            this.btnClose.Size = new System.Drawing.Size(28, 28);
            this.btnClose.TabIndex = 51;
            this.btnClose.TabStop = false;
            this.btnClose.Text = "×";
            this.btnClose.Click += new System.EventHandler(this.btnClose_Click);
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(22)))), ((int)(((byte)(22)))), ((int)(((byte)(24)))));
            this.ClientSize = new System.Drawing.Size(755, 660);
            this.Controls.Add(this.btnClose);
            this.Controls.Add(this.titleLabel);
            this.Controls.Add(this.btnServerSettingManager);
            this.Controls.Add(this.btnReloadHash);
            this.Controls.Add(this.btnShowUserNum);
            this.Controls.Add(this.btnReloadMap);
            this.Controls.Add(this.btnGMTool);
            this.Controls.Add(this.btnOpenHash);
            this.Controls.Add(this.btnOpenDir);
            this.Controls.Add(this.btnCapsuleMachineManager);
            this.Controls.Add(this.label4);
            this.Controls.Add(this.labelServerBuild);
            this.Controls.Add(this.labelServerBuildCaption);
            this.Controls.Add(this.labelServerIdValue);
            this.Controls.Add(this.btnReloadtblServerSettingInfo);
            this.Controls.Add(this.label6);
            this.Controls.Add(this.label5);
            this.Controls.Add(this.btnNotice);
            this.Controls.Add(this.txtNotice);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.btnStopServer);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.richTextBox1);
            this.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(232)))), ((int)(((byte)(232)))), ((int)(((byte)(236)))));
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.MaximizeBox = false;
            this.Name = "Form1";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "TalesLand Server";
            this.Load += new System.EventHandler(this.Form1_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

		}

        private void btnServerSettingManager_Click(object sender, EventArgs e)
        {
			if (ssm == null || ssm.IsDisposed)
			{
				ssm = new ServerSettingManager();
			}
			ssm.Show();
			ssm.Focus();
		}

        private void titleLabel_Click(object sender, EventArgs e)
        {

        }
    }
}
