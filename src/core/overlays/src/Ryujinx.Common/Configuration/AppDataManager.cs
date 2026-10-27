using System;
using System.IO;
using System.Runtime.Versioning;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;

namespace Ryujinx.Common.Configuration;

public static class AppDataManager
{
	public enum LaunchMode
	{
		UserProfile,
		Portable,
		Custom
	}

	private const string DefaultBaseDir = "Ryujinx";

	private const string DefaultPortableDir = "portable";

	private const string GamesDir = "games";

	private const string ProfilesDir = "profiles";

	private const string KeysDir = "system";

	public const string DefaultNandDir = "bis";

	public const string DefaultSdcardDir = "sdcard";

	private const string DefaultModsDir = "mods";

	public static LaunchMode Mode { get; private set; }

	public static string BaseDirPath { get; private set; }

	public static string GamesDirPath { get; private set; }

	public static string ProfilesDirPath { get; private set; }

	public static string KeysDirPath { get; private set; }

	public static string KeysDirPathUser { get; }

	public static string LogsDirPath { get; private set; }

	public static string CustomModsPath { get; set; }

	public static string CustomSdModsPath { get; set; }

	public static string CustomNandPath { get; set; }

	public static string CustomSdCardPath { get; set; }

	static AppDataManager()
	{
		KeysDirPathUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".switch");
	}

	public static void Initialize(string baseDirPath)
	{
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		if (folderPath.Length == 0)
		{
			folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		}
		string text = Path.Combine(folderPath, "Ryujinx");
		string text2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable");
		if (OperatingSystem.IsMacOS() && !Directory.Exists(text2))
		{
			string fullPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
			if (fullPath.EndsWith(".app"))
			{
				text2 = Path.GetFullPath(Path.Combine(fullPath, "..", "portable"));
			}
		}
		if (Directory.Exists(text2))
		{
			BaseDirPath = text2;
			Mode = LaunchMode.Portable;
		}
		else
		{
			BaseDirPath = text;
			Mode = LaunchMode.UserProfile;
		}
		if (baseDirPath != null && baseDirPath != text)
		{
			if (!Directory.Exists(baseDirPath))
			{
				Logger.Error?.Print(LogClass.Application, $"Custom Data Directory '{baseDirPath}' does not exist. Falling back to {Mode}...", "Initialize");
			}
			else
			{
				BaseDirPath = baseDirPath;
				Mode = LaunchMode.Custom;
			}
		}
		BaseDirPath = Path.GetFullPath(BaseDirPath);
		if (IsPathSymlink(BaseDirPath))
		{
			Logger.Warning?.Print(LogClass.Application, "Application data directory is a symlink. This may be unintended.", "Initialize");
		}
		SetupBasePaths();
	}

	public static string GetOrCreateLogsDir()
	{
		if (Directory.Exists(LogsDirPath))
		{
			return LogsDirPath;
		}
		Logger.Notice.Print(LogClass.Application, "Logging directory not found; attempting to create new logging directory.", "GetOrCreateLogsDir");
		LogsDirPath = SetUpLogsDir();
		return LogsDirPath;
	}

	private static string SetUpLogsDir()
	{
		string text = "";
		if (Mode == LaunchMode.Portable || Mode == LaunchMode.Custom)
		{
			text = Path.Combine(BaseDirPath, "Logs");
			try
			{
				Directory.CreateDirectory(text);
			}
			catch
			{
				Logger.Warning?.Print(LogClass.Application, "Logging directory could not be created '" + text + "'", "SetUpLogsDir");
				return null;
			}
		}
		else if (OperatingSystem.IsMacOS())
		{
			text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Ryujinx");
			try
			{
				Directory.CreateDirectory(text);
			}
			catch
			{
				Logger.Warning?.Print(LogClass.Application, "Logging directory could not be created '" + text + "'", "SetUpLogsDir");
				text = "";
			}
			if (string.IsNullOrEmpty(text))
			{
				text = Path.Combine(BaseDirPath, "Logs");
				try
				{
					Directory.CreateDirectory(text);
				}
				catch
				{
					Logger.Warning?.Print(LogClass.Application, "Logging directory could not be created '" + text + "'", "SetUpLogsDir");
					return null;
				}
			}
		}
		else if (OperatingSystem.IsWindows())
		{
			text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
			try
			{
				Directory.CreateDirectory(text);
			}
			catch
			{
				Logger.Warning?.Print(LogClass.Application, "Logging directory could not be created '" + text + "'", "SetUpLogsDir");
				text = "";
			}
			if (string.IsNullOrEmpty(text))
			{
				text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ryujinx", "Logs");
				try
				{
					Directory.CreateDirectory(text);
				}
				catch
				{
					Logger.Warning?.Print(LogClass.Application, "Logging directory could not be created '" + text + "'", "SetUpLogsDir");
					return null;
				}
			}
		}
		else if (OperatingSystem.IsLinux())
		{
			text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ryujinx", "Logs");
			try
			{
				Directory.CreateDirectory(text);
			}
			catch
			{
				Logger.Warning?.Print(LogClass.Application, "Logging directory could not be created '" + text + "'", "SetUpLogsDir");
				return null;
			}
		}
		return text;
	}

	private static void SetupBasePaths()
	{
		Directory.CreateDirectory(BaseDirPath);
		LogsDirPath = SetUpLogsDir();
		Directory.CreateDirectory(GamesDirPath = Path.Combine(BaseDirPath, "games"));
		Directory.CreateDirectory(ProfilesDirPath = Path.Combine(BaseDirPath, "profiles"));
		Directory.CreateDirectory(KeysDirPath = Path.Combine(BaseDirPath, "system"));
	}

	private static bool IsPathSymlink(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
		}
		catch
		{
			return false;
		}
	}

	[SupportedOSPlatform("macos")]
	public static void FixMacOSConfigurationFolders()
	{
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Ryujinx");
		if (Path.Exists(text) && !IsPathSymlink(text) && !Path.Exists(BaseDirPath))
		{
			FileSystemUtils.MoveDirectory(text, BaseDirPath);
			Directory.CreateSymbolicLink(text, BaseDirPath);
		}
		string text2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ryujinx");
		if (!IsPathSymlink(text2))
		{
			return;
		}
		string text3 = Path.Combine(Path.GetTempPath(), "Ryujinx");
		try
		{
			FileSystemUtils.CopyDirectory(text2, text3, recursive: true);
		}
		catch (Exception value)
		{
			Logger.Error?.Print(LogClass.Application, $"Critical error copying Ryujinx application data into the temp folder. {value}", "FixMacOSConfigurationFolders");
			try
			{
				string value2 = Directory.ResolveLinkTarget(text2, returnFinalTarget: true)?.FullName;
				Logger.Error?.Print(LogClass.Application, $"Please manually move your Ryujinx data from {value2} to {text2}, and remove the symlink.", "FixMacOSConfigurationFolders");
				return;
			}
			catch (Exception value3)
			{
				Logger.Error?.Print(LogClass.Application, $"Unable to resolve the symlink for Ryujinx application data: {value3}. Follow the symlink at {text2} and move your data back to the Application Support folder.", "FixMacOSConfigurationFolders");
				return;
			}
		}
		try
		{
			File.Delete(text2);
		}
		catch (Exception value4)
		{
			Logger.Error?.Print(LogClass.Application, $"Critical error deleting the Ryujinx application data folder symlink at {text2}. {value4}", "FixMacOSConfigurationFolders");
			try
			{
				string value5 = Directory.ResolveLinkTarget(text2, returnFinalTarget: true)?.FullName;
				Logger.Error?.Print(LogClass.Application, $"Please manually move your Ryujinx data from {value5} to {text2}, and remove the symlink.", "FixMacOSConfigurationFolders");
				return;
			}
			catch (Exception value6)
			{
				Logger.Error?.Print(LogClass.Application, $"Unable to resolve the symlink for Ryujinx application data: {value6}. Follow the symlink at {text2} and move your data back to the Application Support folder.", "FixMacOSConfigurationFolders");
				return;
			}
		}
		try
		{
			FileSystemUtils.CopyDirectory(text3, text2, recursive: true);
		}
		catch (Exception value7)
		{
			Logger.Error?.Print(LogClass.Application, $"Critical error copying Ryujinx application data into the correct location. {value7}. Please manually move your application data from {text3} to {text2}.", "FixMacOSConfigurationFolders");
		}
	}

	public static string GetModsPath()
	{
		return CustomModsPath ?? Directory.CreateDirectory(Path.Combine(BaseDirPath, "mods")).FullName;
	}

	public static string GetSdModsPath()
	{
		return CustomSdModsPath ?? Directory.CreateDirectory(Path.Combine(BaseDirPath, "sdcard", "atmosphere")).FullName;
	}
}
