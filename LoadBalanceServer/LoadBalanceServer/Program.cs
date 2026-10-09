using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using Akka.Actor;
using Akka.Configuration;
using IniParser;
using IniParser.Model;
using Serilog;
using Serilog.Events;

namespace LoadBalanceServer
{
	public static class Program
	{
		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool SetDllDirectory(string lpPathName);

		public static void Main(string[] args)
		{
			try
			{
				string baseDir = AppDomain.CurrentDomain.BaseDirectory;
				if (!string.IsNullOrEmpty(baseDir))
				{
					Directory.SetCurrentDirectory(baseDir);
					string deps = Path.Combine(baseDir, "bin");
					if (Directory.Exists(deps))
					{
						SetDllDirectory(deps);
					}
				}
			}
			catch
			{
			}
			DeleteEmptyDirsAndFiles(".\\Logs\\LBS\\");
			string dates = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_LBS.log";
			Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().WriteTo.Logger(delegate(LoggerConfiguration l)
			{
				l.Filter.ByIncludingOnly((LogEvent e) => e.Level == LogEventLevel.Information).WriteTo.File(".\\Logs\\LBS\\Info\\" + dates, LogEventLevel.Verbose, "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", null, 1073741824L, null, buffered: false, shared: false, null, RollingInterval.Infinite, rollOnFileSizeLimit: false, 31);
			}).WriteTo.Logger(delegate(LoggerConfiguration l)
			{
				l.Filter.ByIncludingOnly((LogEvent e) => e.Level == LogEventLevel.Warning).WriteTo.File(".\\Logs\\LBS\\Warning\\" + dates, LogEventLevel.Verbose, "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", null, 1073741824L, null, buffered: false, shared: false, null, RollingInterval.Infinite, rollOnFileSizeLimit: false, 31);
			}).WriteTo.Logger(delegate(LoggerConfiguration l)
			{
				l.Filter.ByIncludingOnly((LogEvent e) => e.Level == LogEventLevel.Error).WriteTo.File(".\\Logs\\LBS\\Error\\" + dates, LogEventLevel.Verbose, "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", null, 1073741824L, null, buffered: false, shared: false, null, RollingInterval.Infinite, rollOnFileSizeLimit: false, 31);
			}).CreateLogger();
			Console.Title = "TR SERVER : LoadBalanceServer";
			if (!File.Exists("settings.ini"))
			{
				Log.Error("settings.ini doesn't exist!");
				return;
			}
			Boot();
			Console.Title = "TR SERVER : LoadBalanceServer · TalesLand";
			TalesLand.TlLog.SinkInfo = delegate(string m) { Log.Information(m); };
			TalesLand.TlLog.SinkWarn = delegate(string m) { Log.Warning(m); };
			TalesLand.TlLoadBalanceHost.Start(Conf.ServerIP, Conf.LoadBalanceServerPort, Conf.AgentPort, Conf.HashCheck, Server.HashList.ToArray());
			Log.Information("LoadBalance listening on {0}:{1} -> agent {2}", Conf.ServerIP, Conf.LoadBalanceServerPort, Conf.AgentPort);
			Console.ReadLine();
		}

		private static bool Boot()
		{
			try
			{
				IniData iniData = new FileIniDataParser().ReadFile("settings.ini");
				Conf.ServerIP = iniData["Server"]["AgentServerIP"].Replace(" ", "");
				Conf.AgentPort = Convert.ToUInt16(iniData["Server"]["AgentServerTCPPort"].Replace(" ", ""));
				Conf.LoadBalanceServerPort = Convert.ToUInt16(iniData["Server"]["LoadBalanceServerPort"].Replace(" ", ""));
				Conf.Connstr = iniData["Server"]["MySQLConnection"];
				Conf.HashCheck = Convert.ToBoolean(iniData["Server"]["HashCheck"].Replace(" ", ""));
				TalesLand.TlLog.DebugMode = iniData["Server"].ContainsKey("DebugMode")
					&& Convert.ToBoolean(iniData["Server"]["DebugMode"].Replace(" ", ""));
				Server.HashList.Clear();
				if (File.Exists("hash.ini"))
				{
					foreach (string line in File.ReadAllLines("hash.ini"))
					{
						string hash = line.Trim();
						if (hash.Length > 0)
						{
							Server.HashList.Add(hash);
						}
					}
				}
				Log.Information("Load Hash Count: {0} HashCheck={1}", Server.HashList.Count, Conf.HashCheck);
				Log.Information("Loading Settings.ini........Done");
				return true;
			}
			catch (Exception ex)
			{
				Log.Error("Boot Error:{0}", ex.ToString());
				return false;
			}
		}

		private static void DeleteEmptyDirsAndFiles(string dir)
		{
			if (string.IsNullOrEmpty(dir))
			{
				throw new ArgumentException("Starting directory is a null reference or an empty string", "dir");
			}
			if (!Directory.Exists(dir))
			{
				return;
			}
			try
			{
				foreach (string item in Directory.EnumerateDirectories(dir))
				{
					DeleteEmptyDirsAndFiles(item);
				}
				if (!Directory.EnumerateFileSystemEntries(dir).Any())
				{
					try
					{
						Directory.Delete(dir);
					}
					catch (UnauthorizedAccessException)
					{
					}
					catch (DirectoryNotFoundException)
					{
					}
				}
				string[] files = Directory.GetFiles(dir);
				foreach (string text in files)
				{
					if (new FileInfo(text).Length == 0L)
					{
						try
						{
							File.Delete(text);
						}
						catch (Exception)
						{
						}
					}
				}
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}
}
