using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.RomFs;
using LibHac.Util;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Services.Fs.FileSystemProxy;
using Ryujinx.HLE.Loaders.Executables;
using Ryujinx.HLE.Loaders.Mods;
using Ryujinx.HLE.Loaders.Processes;

namespace Ryujinx.HLE.HOS;

public class ModLoader
{
	public readonly struct Mod<T>(string name, T path, bool enabled) where T : FileSystemInfo
	{
		public readonly string Name = name;

		public readonly T Path = path;

		public readonly bool Enabled = enabled;
	}

	public struct Cheat(string name, FileInfo path, IEnumerable<string> instructions)
	{
		public const int CheatIdSize = 16;

		public readonly string Name = name;

		public readonly FileInfo Path = path;

		public readonly IEnumerable<string> Instructions = instructions;
	}

	public class ModCache
	{
		public List<Mod<FileInfo>> RomfsContainers { get; }

		public List<Mod<FileInfo>> ExefsContainers { get; }

		public List<Mod<DirectoryInfo>> RomfsDirs { get; }

		public List<Mod<DirectoryInfo>> ExefsDirs { get; }

		public List<Cheat> Cheats { get; }

		public ModCache()
		{
			RomfsContainers = new List<Mod<FileInfo>>();
			ExefsContainers = new List<Mod<FileInfo>>();
			RomfsDirs = new List<Mod<DirectoryInfo>>();
			ExefsDirs = new List<Mod<DirectoryInfo>>();
			Cheats = new List<Cheat>();
		}
	}

	private class PatchCache
	{
		public List<Mod<DirectoryInfo>> NsoPatches { get; }

		public List<Mod<DirectoryInfo>> NroPatches { get; }

		public List<Mod<DirectoryInfo>> KipPatches { get; }

		internal bool Initialized { get; set; }

		public PatchCache()
		{
			NsoPatches = new List<Mod<DirectoryInfo>>();
			NroPatches = new List<Mod<DirectoryInfo>>();
			KipPatches = new List<Mod<DirectoryInfo>>();
			Initialized = false;
		}
	}

	public struct ModLoadResult
	{
		public BitVector32 Stubs;

		public BitVector32 Replaces;

		public MetaLoader Npdm;

		public string Hash;

		public bool Modified => (Stubs.Data | Replaces.Data) != 0;
	}

	private const string RomfsDir = "romfs";

	private const string ExefsDir = "exefs";

	private const string CheatDir = "cheats";

	private const string RomfsContainer = "romfs.bin";

	private const string ExefsContainer = "exefs.nsp";

	private const string StubExtension = ".stub";

	private const string CheatExtension = ".txt";

	private const string DefaultCheatName = "<default>";

	private const string AmsContentsDir = "contents";

	private const string AmsNsoPatchDir = "exefs_patches";

	private const string AmsNroPatchDir = "nro_patches";

	private const string AmsKipPatchDir = "kip_patches";

	private static readonly ModMetadataJsonSerializerContext _serializerContext;

	private readonly Dictionary<ulong, ModCache> _appMods;

	private PatchCache _patches;

	private static readonly EnumerationOptions _dirEnumOptions;

	internal bool HasModsForPatchGuard
	{
		get
		{
			if (!_patches.NsoPatches.Any((Mod<DirectoryInfo> m) => m.Enabled) && !_patches.NroPatches.Any((Mod<DirectoryInfo> m) => m.Enabled) && !_patches.KipPatches.Any((Mod<DirectoryInfo> m) => m.Enabled))
			{
				return _appMods.Values.Any((ModCache m) => m.RomfsContainers.Any((Mod<FileInfo> x) => x.Enabled) || m.ExefsContainers.Any((Mod<FileInfo> x) => x.Enabled) || m.RomfsDirs.Any((Mod<DirectoryInfo> x) => x.Enabled) || m.ExefsDirs.Any((Mod<DirectoryInfo> x) => x.Enabled) || m.Cheats.Count != 0);
			}
			return true;
		}
	}

	static ModLoader()
	{
		_serializerContext = new ModMetadataJsonSerializerContext(JsonHelper.GetDefaultSerializerOptions());
		_dirEnumOptions = new EnumerationOptions
		{
			MatchCasing = MatchCasing.CaseInsensitive,
			MatchType = MatchType.Simple,
			RecurseSubdirectories = false,
			ReturnSpecialDirectories = false
		};
	}

	public ModLoader()
	{
		_appMods = new Dictionary<ulong, ModCache>();
		_patches = new PatchCache();
	}

	private void Clear()
	{
		_appMods.Clear();
		_patches = new PatchCache();
	}

	private static bool StrEquals(string s1, string s2)
	{
		return string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase);
	}

	public static string GetModsBasePath()
	{
		return EnsureBaseDirStructure(AppDataManager.GetModsPath());
	}

	public static string GetSdModsBasePath()
	{
		return EnsureBaseDirStructure(AppDataManager.GetSdModsPath());
	}

	private static string EnsureBaseDirStructure(string modsBasePath)
	{
		DirectoryInfo directoryInfo = new DirectoryInfo(modsBasePath);
		directoryInfo.CreateSubdirectory("contents");
		directoryInfo.CreateSubdirectory("exefs_patches");
		directoryInfo.CreateSubdirectory("nro_patches");
		return directoryInfo.FullName;
	}

	private static DirectoryInfo FindApplicationDir(DirectoryInfo contentsDir, string applicationId)
	{
		return contentsDir.EnumerateDirectories(applicationId, _dirEnumOptions).FirstOrDefault();
	}

	private static void AddModsFromDirectory(ModCache mods, DirectoryInfo dir, ModMetadata modMetadata)
	{
		StringBuilder stringBuilder = new StringBuilder();
		foreach (DirectoryInfo modDir in dir.EnumerateDirectories())
		{
			stringBuilder.Clear();
			Mod<DirectoryInfo> item = new Mod<DirectoryInfo>("", null, enabled: true);
			if (StrEquals("romfs", modDir.Name))
			{
				bool enabled = modMetadata.Mods.FirstOrDefault((Mod x) => modDir.Parent.FullName.Equals(x.Path))?.Enabled ?? true;
				List<Mod<DirectoryInfo>> romfsDirs = mods.RomfsDirs;
				item = new Mod<DirectoryInfo>(dir.Name, modDir, enabled);
				romfsDirs.Add(item);
				stringBuilder.Append('R');
			}
			else if (StrEquals("exefs", modDir.Name))
			{
				bool enabled2 = modMetadata.Mods.FirstOrDefault((Mod x) => modDir.Parent.FullName.Equals(x.Path))?.Enabled ?? true;
				List<Mod<DirectoryInfo>> exefsDirs = mods.ExefsDirs;
				item = new Mod<DirectoryInfo>(dir.Name, modDir, enabled2);
				exefsDirs.Add(item);
				stringBuilder.Append('E');
			}
			else if (StrEquals("cheats", modDir.Name))
			{
				stringBuilder.Append('C', QueryCheatsDir(mods, modDir));
			}
			else
			{
				AddModsFromDirectory(mods, modDir, modMetadata);
			}
			if (stringBuilder.Length > 0)
			{
				Logger.Info?.Print(LogClass.ModLoader, $"Found {(item.Enabled ? "enabled" : "disabled")} mod '{item.Name}' [{stringBuilder}]", "AddModsFromDirectory");
			}
		}
	}

	public static string GetApplicationDir(string modsBasePath, string applicationId)
	{
		DirectoryInfo directoryInfo = new DirectoryInfo(System.IO.Path.Combine(modsBasePath, "contents"));
		DirectoryInfo directoryInfo2 = FindApplicationDir(directoryInfo, applicationId);
		if (directoryInfo2 == null)
		{
			Logger.Info?.Print(LogClass.ModLoader, "Creating mods directory for Application " + applicationId.ToUpper(), "GetApplicationDir");
			directoryInfo2 = directoryInfo.CreateSubdirectory(applicationId);
		}
		return directoryInfo2.FullName;
	}

	private static void QueryPatchDirs(PatchCache cache, DirectoryInfo patchDir)
	{
		if (cache.Initialized || !patchDir.Exists)
		{
			return;
		}
		List<Mod<DirectoryInfo>> list;
		string value;
		if (StrEquals("exefs_patches", patchDir.Name))
		{
			list = cache.NsoPatches;
			value = "NSO";
		}
		else if (StrEquals("nro_patches", patchDir.Name))
		{
			list = cache.NroPatches;
			value = "NRO";
		}
		else
		{
			if (!StrEquals("kip_patches", patchDir.Name))
			{
				return;
			}
			list = cache.KipPatches;
			value = "KIP";
		}
		foreach (DirectoryInfo item in patchDir.EnumerateDirectories())
		{
			list.Add(new Mod<DirectoryInfo>(item.Name, item, enabled: true));
			Logger.Info?.Print(LogClass.ModLoader, $"Found {value} patch '{item.Name}'", "QueryPatchDirs");
		}
	}

	private static void QueryApplicationDir(ModCache mods, DirectoryInfo applicationDir, ulong applicationId)
	{
		if (!applicationDir.Exists)
		{
			return;
		}
		string text = System.IO.Path.Combine(AppDataManager.GamesDirPath, applicationId.ToString("x16"), "mods.json");
		ModMetadata modMetadata = new ModMetadata();
		if (File.Exists(text))
		{
			try
			{
				modMetadata = JsonHelper.DeserializeFromFile(text, _serializerContext.ModMetadata);
			}
			catch
			{
				Logger.Warning?.Print(LogClass.ModLoader, $"Failed to deserialize mod data for {applicationId:X16} at {text}", "QueryApplicationDir");
			}
		}
		FileInfo fsFile = new FileInfo(System.IO.Path.Combine(applicationDir.FullName, "romfs.bin"));
		if (fsFile.Exists)
		{
			bool enabled = modMetadata.Mods.FirstOrDefault((Mod x) => fsFile.FullName.Contains(x.Path))?.Enabled ?? true;
			mods.RomfsContainers.Add(new Mod<FileInfo>("<" + applicationDir.Name + " RomFs>", fsFile, enabled));
		}
		fsFile = new FileInfo(System.IO.Path.Combine(applicationDir.FullName, "exefs.nsp"));
		if (fsFile.Exists)
		{
			bool enabled2 = modMetadata.Mods.FirstOrDefault((Mod x) => fsFile.FullName.Contains(x.Path))?.Enabled ?? true;
			mods.ExefsContainers.Add(new Mod<FileInfo>("<" + applicationDir.Name + " ExeFs>", fsFile, enabled2));
		}
		AddModsFromDirectory(mods, applicationDir, modMetadata);
	}

	public static void QueryContentsDir(ModCache mods, DirectoryInfo contentsDir, ulong applicationId, ulong[] installedDlcs)
	{
		if (!contentsDir.Exists)
		{
			return;
		}
		Logger.Info?.Print(LogClass.ModLoader, $"Searching mods for {(((applicationId & 0x1000) != 0L) ? "DLC" : "Application")} {applicationId:X16} in \"{contentsDir.FullName}\"", "QueryContentsDir");
		DirectoryInfo directoryInfo = FindApplicationDir(contentsDir, $"{applicationId:x16}");
		if (directoryInfo != null)
		{
			QueryApplicationDir(mods, directoryInfo, applicationId);
		}
		foreach (ulong value in installedDlcs)
		{
			DirectoryInfo directoryInfo2 = FindApplicationDir(contentsDir, $"{value:x16}");
			if (directoryInfo2 != null)
			{
				QueryApplicationDir(mods, directoryInfo2, applicationId);
			}
		}
	}

	private static int QueryCheatsDir(ModCache mods, DirectoryInfo cheatsDir)
	{
		if (!cheatsDir.Exists)
		{
			return 0;
		}
		int num = 0;
		foreach (FileInfo item in cheatsDir.EnumerateFiles())
		{
			if (!StrEquals(".txt", item.Extension))
			{
				continue;
			}
			string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(item.Name);
			if (fileNameWithoutExtension.Length == 16 && ulong.TryParse(fileNameWithoutExtension, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var _))
			{
				int count = mods.Cheats.Count;
				mods.Cheats.AddRange(GetCheatsInFile(item));
				if (mods.Cheats.Count - count > 0)
				{
					num++;
				}
			}
		}
		return num;
	}

	private static IEnumerable<Cheat> GetCheatsInFile(FileInfo cheatFile)
	{
		string text = "<default>";
		List<string> list = new List<string>();
		List<Cheat> list2 = new List<Cheat>();
		using StreamReader streamReader = cheatFile.OpenText();
		while (true)
		{
			string text2 = streamReader.ReadLine();
			if (text2 == null)
			{
				break;
			}
			text2 = text2.Trim();
			if (text2.StartsWith('['))
			{
				if (!text2.EndsWith(']') || text2.Length < 3)
				{
					Logger.Warning?.Print(LogClass.ModLoader, "Ignoring cheat '" + cheatFile.FullName + "' because it is malformed", "GetCheatsInFile");
					return Array.Empty<Cheat>();
				}
				if (list.Count > 0)
				{
					list2.Add(new Cheat("<" + text + " Cheat>", cheatFile, list));
				}
				string text3 = text2;
				text = text3.Substring(1, text3.Length - 1 - 1);
				list = new List<string>();
			}
			else if (text2.Length > 0)
			{
				list.Add(text2);
			}
		}
		if (list.Count > 0)
		{
			list2.Add(new Cheat("<" + text + " Cheat>", cheatFile, list));
		}
		return list2;
	}

	private static void CollectMods(Dictionary<ulong, ModCache> modCaches, PatchCache patches, params ReadOnlySpan<string> searchDirPaths)
	{
		ReadOnlySpan<string> readOnlySpan = searchDirPaths;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			DirectoryInfo directoryInfo = new DirectoryInfo(readOnlySpan[i]);
			if (!directoryInfo.Exists)
			{
				Logger.Warning?.Print(LogClass.ModLoader, "Mod Search Dir '" + directoryInfo.FullName + "' doesn't exist", "CollectMods");
				return;
			}
			if (TryQuery(directoryInfo, patches, modCaches))
			{
				continue;
			}
			foreach (DirectoryInfo item in directoryInfo.EnumerateDirectories())
			{
				TryQuery(item, patches, modCaches);
			}
		}
		patches.Initialized = true;
		static bool IsContentsDir(string name)
		{
			return StrEquals("contents", name);
		}
		static bool IsPatchesDir(string name)
		{
			if (!StrEquals("exefs_patches", name) && !StrEquals("nro_patches", name))
			{
				return StrEquals("kip_patches", name);
			}
			return true;
		}
		static bool TryQuery(DirectoryInfo searchDir, PatchCache cache, Dictionary<ulong, ModCache> dictionary)
		{
			if (IsContentsDir(searchDir.Name))
			{
				foreach (var (applicationId, mods) in dictionary)
				{
					QueryContentsDir(mods, searchDir, applicationId, Array.Empty<ulong>());
				}
				return true;
			}
			if (IsPatchesDir(searchDir.Name))
			{
				QueryPatchDirs(cache, searchDir);
				return true;
			}
			return false;
		}
	}

	public void CollectMods(IEnumerable<ulong> applications, params ReadOnlySpan<string> searchDirPaths)
	{
		Clear();
		foreach (ulong application in applications)
		{
			_appMods[application] = new ModCache();
		}
		CollectMods(_appMods, _patches, searchDirPaths);
	}

	internal LibHac.Fs.IStorage ApplyRomFsMods(ulong applicationId, LibHac.Fs.IStorage baseStorage)
	{
		if (!_appMods.TryGetValue(applicationId, out var value) || value.RomfsDirs.Count + value.RomfsContainers.Count == 0)
		{
			return baseStorage;
		}
		HashSet<string> fileSet = new HashSet<string>();
		RomFsBuilder romFsBuilder = new RomFsBuilder();
		int num = 0;
		Logger.Info?.Print(LogClass.ModLoader, $"Applying RomFS mods for Application {applicationId:X16}", "ApplyRomFsMods");
		foreach (Mod<DirectoryInfo> romfsDir in value.RomfsDirs)
		{
			if (romfsDir.Enabled)
			{
				using (LibHac.Fs.Fsa.IFileSystem fs = new LocalFileSystem(romfsDir.Path.FullName))
				{
					AddFiles(fs, romfsDir.Name, romfsDir.Path.FullName, fileSet, romFsBuilder);
				}
				num++;
			}
		}
		foreach (Mod<FileInfo> romfsContainer in value.RomfsContainers)
		{
			if (romfsContainer.Enabled)
			{
				Logger.Info?.Print(LogClass.ModLoader, $"Found 'romfs.bin' for Application {applicationId:X16}", "ApplyRomFsMods");
				using (LibHac.Fs.Fsa.IFileSystem fs2 = new RomFsFileSystem(romfsContainer.Path.OpenRead().AsStorage()))
				{
					AddFiles(fs2, romfsContainer.Name, romfsContainer.Path.FullName, fileSet, romFsBuilder);
				}
				num++;
			}
		}
		if (fileSet.Count == 0)
		{
			Logger.Info?.Print(LogClass.ModLoader, "No files found. Using base RomFS", "ApplyRomFsMods");
			return baseStorage;
		}
		Logger.Info?.Print(LogClass.ModLoader, $"Replaced {fileSet.Count} file(s) over {num} mod(s). Processing base storage...", "ApplyRomFsMods");
		RomFsFileSystem romFsFileSystem = new RomFsFileSystem(baseStorage);
		foreach (DirectoryEntryEx item in (from f in romFsFileSystem.EnumerateEntries()
			where f.Type == DirectoryEntryType.File && !fileSet.Contains(f.FullPath)
			select f).OrderBy((DirectoryEntryEx f) => f.FullPath, StringComparer.Ordinal))
		{
			using UniqueRef<LibHac.Fs.Fsa.IFile> uniqueRef = default;
			romFsFileSystem.OpenFile(ref uniqueRef.Ref, item.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
			romFsBuilder.AddFile(item.FullPath, uniqueRef.Release());
		}
		Logger.Info?.Print(LogClass.ModLoader, "Building new RomFS...", "ApplyRomFsMods");
		LibHac.Fs.IStorage result = romFsBuilder.Build();
		Logger.Log? info = Logger.Info;
		if (info.HasValue)
		{
			info.GetValueOrDefault().Print(LogClass.ModLoader, "Using modded RomFS", "ApplyRomFsMods");
			return result;
		}
		return result;
	}

	private static void AddFiles(LibHac.Fs.Fsa.IFileSystem fs, string modName, string rootPath, ISet<string> fileSet, RomFsBuilder builder)
	{
		foreach (DirectoryEntryEx item in (from f in fs.EnumerateEntries().AsParallel()
			where f.Type == DirectoryEntryType.File
			select f).OrderBy((DirectoryEntryEx f) => f.FullPath, StringComparer.Ordinal))
		{
			LazyFile file = new LazyFile(item.FullPath, rootPath, fs);
			if (fileSet.Add(item.FullPath))
			{
				builder.AddFile(item.FullPath, file);
				continue;
			}
			Logger.Warning?.Print(LogClass.ModLoader, $"    Skipped duplicate file '{item.FullPath}' from '{modName}'", "ApplyRomFsMods");
		}
	}

	internal bool ReplaceExefsPartition(ulong applicationId, ref LibHac.Fs.Fsa.IFileSystem exefs)
	{
		if (!_appMods.TryGetValue(applicationId, out var value) || value.ExefsContainers.Count == 0)
		{
			return false;
		}
		if (value.ExefsContainers.Count > 1)
		{
			Logger.Warning?.Print(LogClass.ModLoader, "Multiple ExeFS partition replacements detected", "ReplaceExefsPartition");
		}
		Logger.Info?.Print(LogClass.ModLoader, "Using replacement ExeFS partition", "ReplaceExefsPartition");
		PartitionFileSystem partitionFileSystem = new PartitionFileSystem();
		partitionFileSystem.Initialize(value.ExefsContainers[0].Path.OpenRead().AsStorage()).ThrowIfFailure();
		exefs = partitionFileSystem;
		return true;
	}

	internal ModLoadResult ApplyExefsMods(ulong applicationId, NsoExecutable[] nsos)
	{
		ModLoadResult result = new ModLoadResult
		{
			Stubs = default,
			Replaces = default,
			Hash = null
		};
		string text = string.Empty;
		if (!_appMods.TryGetValue(applicationId, out var value) || value.ExefsDirs.Count == 0)
		{
			return result;
		}
		if (nsos.Length != ProcessConst.ExeFsPrefixes.Length)
		{
			throw new ArgumentOutOfRangeException("nsos", nsos.Length, "NSO Count is incorrect");
		}
		foreach (Mod<DirectoryInfo> exefsDir in value.ExefsDirs)
		{
			if (!exefsDir.Enabled)
			{
				continue;
			}
			for (int i = 0; i < ProcessConst.ExeFsPrefixes.Length; i++)
			{
				string text2 = ProcessConst.ExeFsPrefixes[i];
				FileInfo fileInfo = new FileInfo(System.IO.Path.Combine(exefsDir.Path.FullName, text2));
				if (fileInfo.Exists)
				{
					if (result.Replaces[1 << i])
					{
						Logger.Warning?.Print(LogClass.ModLoader, "Multiple replacements to '" + text2 + "'", "ApplyExefsMods");
						continue;
					}
					result.Replaces[1 << i] = true;
					using FileStream fileStream = fileInfo.OpenRead();
					nsos[i] = new NsoExecutable(fileStream.AsStorage(), text2);
					Logger.Info?.Print(LogClass.ModLoader, "NSO '" + text2 + "' replaced", "ApplyExefsMods");
					using MD5 mD = MD5.Create();
					fileStream.Seek(0L, SeekOrigin.Begin);
					text += BitConverter.ToString(mD.ComputeHash(fileStream)).Replace("-", "").ToLowerInvariant();
				}
				result.Stubs[1 << i] |= File.Exists(System.IO.Path.Combine(exefsDir.Path.FullName, text2 + ".stub"));
			}
			FileInfo fileInfo2 = new FileInfo(System.IO.Path.Combine(exefsDir.Path.FullName, "main.npdm"));
			if (fileInfo2.Exists)
			{
				if (result.Npdm != null)
				{
					Logger.Warning?.Print(LogClass.ModLoader, "Multiple replacements to 'main.npdm'", "ApplyExefsMods");
					continue;
				}
				result.Npdm = new MetaLoader();
				result.Npdm.Load(File.ReadAllBytes(fileInfo2.FullName));
				Logger.Info?.Print(LogClass.ModLoader, "main.npdm replaced", "ApplyExefsMods");
			}
		}
		for (int num = ProcessConst.ExeFsPrefixes.Length - 1; num >= 0; num--)
		{
			if (result.Stubs[1 << num] && !result.Replaces[1 << num])
			{
				Logger.Info?.Print(LogClass.ModLoader, "    NSO '" + nsos[num].Name + "' stubbed", "ApplyExefsMods");
				nsos[num] = null;
			}
		}
		if (!string.IsNullOrEmpty(text))
		{
			using MD5 mD2 = MD5.Create();
			result.Hash += BitConverter.ToString(mD2.ComputeHash(text.ToBytes())).Replace("-", "").ToLowerInvariant();
		}
		return result;
	}

	internal void ApplyNroPatches(NroExecutable nro)
	{
		List<Mod<DirectoryInfo>> nroPatches = _patches.NroPatches;
		if (nroPatches.Count != 0)
		{
			ApplyProgramPatches(nroPatches, 0, (IExecutable)nro);
		}
	}

	internal bool ApplyNsoPatches(ulong applicationId, params ReadOnlySpan<IExecutable> programs)
	{
		IEnumerable<Mod<DirectoryInfo>> enumerable = _patches.NsoPatches;
		if (_appMods.TryGetValue(applicationId, out var value))
		{
			enumerable = enumerable.Concat(value.ExefsDirs);
		}
		return ApplyProgramPatches(enumerable, 256, programs);
	}

	internal void LoadCheats(ulong applicationId, ProcessTamperInfo tamperInfo, TamperMachine tamperMachine)
	{
		if (tamperInfo?.BuildIds == null || tamperInfo.CodeAddresses == null)
		{
			Logger.Error?.Print(LogClass.ModLoader, "Unable to install cheat because the associated process is invalid", "LoadCheats");
			return;
		}
		Logger.Info?.Print(LogClass.ModLoader, $"Build ids found for application {applicationId:X16}:\n    {string.Join("\n    ", tamperInfo.BuildIds)}", "LoadCheats");
		if (!_appMods.TryGetValue(applicationId, out var value) || value.Cheats.Count == 0)
		{
			return;
		}
		List<Cheat> cheats = value.Cheats;
		Dictionary<string, ulong> dictionary = tamperInfo.BuildIds.Zip(tamperInfo.CodeAddresses, (string k, ulong v) => new { k, v }).ToDictionary(x => x.k.Substring(0, Math.Min(16, x.k.Length)), x => x.v);
		foreach (Cheat item in cheats)
		{
			string text = System.IO.Path.GetFileNameWithoutExtension(item.Path.Name).ToUpper();
			if (!dictionary.TryGetValue(text, out var value2))
			{
				Logger.Warning?.Print(LogClass.ModLoader, $"Skipping cheat '{item.Name}' because no executable matches its BuildId {text} (check if the game title and version are correct)", "LoadCheats");
			}
			else
			{
				Logger.Info?.Print(LogClass.ModLoader, "Installing cheat '" + item.Name + "'", "LoadCheats");
				tamperMachine.InstallAtmosphereCheat(item.Name, text, item.Instructions, tamperInfo, value2);
			}
		}
		EnableCheats(applicationId, tamperMachine);
	}

	internal static void EnableCheats(ulong applicationId, TamperMachine tamperMachine)
	{
		string path = System.IO.Path.Combine(FindApplicationDir(new DirectoryInfo(System.IO.Path.Combine(GetModsBasePath(), "contents")), $"{applicationId:x16}").FullName, "cheats", "enabled.txt");
		if (File.Exists(path))
		{
			tamperMachine.EnableCheats(File.ReadAllLines(path));
		}
	}

	private static bool ApplyProgramPatches(IEnumerable<Mod<DirectoryInfo>> mods, int protectedOffset, params ReadOnlySpan<IExecutable> programs)
	{
		int num = 0;
		MemPatch[] array = new MemPatch[programs.Length];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = new MemPatch();
		}
		List<string> buildIds = new List<string>(programs.Length);
		ReadOnlySpan<IExecutable> readOnlySpan = programs;
		for (int j = 0; j < readOnlySpan.Length; j++)
		{
			IExecutable executable = readOnlySpan[j];
			string text;
			if (executable is NsoExecutable nsoExecutable)
			{
				text = Convert.ToHexString(nsoExecutable.BuildId).TrimEnd('0');
			}
			else
			{
				text = ((!(executable is NroExecutable nroExecutable)) ? string.Empty : Convert.ToHexString(nroExecutable.Header.BuildId).TrimEnd('0'));
			}
			string item = text;
			buildIds.Add(item);
		}
		foreach (Mod<DirectoryInfo> mod in mods)
		{
			if (!mod.Enabled)
			{
				continue;
			}
			foreach (FileInfo item2 in mod.Path.EnumerateFiles())
			{
				if (StrEquals(".ips", item2.Extension))
				{
					string text2 = System.IO.Path.GetFileNameWithoutExtension(item2.FullName).Split('.')[0].TrimEnd('0');
					int num2 = GetIndex(text2);
					if (num2 == -1)
					{
						continue;
					}
					Logger.Info?.Print(LogClass.ModLoader, $"Matching IPS patch '{item2.Name}' in '{mod.Name}' bid={text2}", "ApplyProgramPatches");
					using FileStream input = item2.OpenRead();
					using BinaryReader reader = new BinaryReader(input);
					new IpsPatcher(reader).AddPatches(array[num2]);
				}
				else
				{
					if (!StrEquals(".pchtxt", item2.Extension))
					{
						continue;
					}
					using FileStream stream = item2.OpenRead();
					using StreamReader reader2 = new StreamReader(stream);
					IPSwitchPatcher iPSwitchPatcher = new IPSwitchPatcher(reader2);
					int num3 = GetIndex(iPSwitchPatcher.BuildId);
					if (num3 != -1)
					{
						Logger.Info?.Print(LogClass.ModLoader, $"Matching IPSwitch patch '{item2.Name}' in '{mod.Name}' bid={iPSwitchPatcher.BuildId}", "ApplyProgramPatches");
						iPSwitchPatcher.AddPatches(array[num3]);
					}
				}
			}
		}
		for (int k = 0; k < programs.Length; k++)
		{
			num += array[k].Patch(programs[k].Program, protectedOffset);
		}
		return num > 0;
		int GetIndex(string buildId)
		{
			return buildIds.FindIndex((string id) => id == buildId);
		}
	}
}
