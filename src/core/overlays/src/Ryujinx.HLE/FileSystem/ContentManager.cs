using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Crypto;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.Exceptions;
using Ryujinx.HLE.HOS.Services.Ssl;
using Ryujinx.HLE.HOS.Services.Time;
using Ryujinx.HLE.Utilities;

namespace Ryujinx.HLE.FileSystem;

public class ContentManager
{
	private readonly struct AocItem(Stream containerStream, string ncaPath, string extension)
	{
		public readonly Stream ContainerStream = containerStream;

		public readonly string NcaPath = ncaPath;

		public readonly string Extension = extension;
	}

	private const ulong SystemVersionTitleId = 72057594037929993uL;

	private const ulong SystemUpdateTitleId = 72057594037930006uL;

	private Dictionary<StorageId, LinkedList<LocationEntry>> _locationEntries;

	private readonly Dictionary<string, ulong> _sharedFontTitleDictionary;

	private readonly Dictionary<ulong, string> _systemTitlesNameDictionary;

	private readonly Dictionary<string, string> _sharedFontFilenameDictionary;

	private SortedDictionary<(ulong titleId, NcaContentType type), string> _contentDictionary;

	private readonly VirtualFileSystem _virtualFileSystem;

	private readonly Lock _lock = new Lock();

	private SortedList<ulong, AocItem> AocData { get; }

	public ContentManager(VirtualFileSystem virtualFileSystem)
	{
		_contentDictionary = new SortedDictionary<(ulong, NcaContentType), string>();
		_locationEntries = new Dictionary<StorageId, LinkedList<LocationEntry>>();
		_sharedFontTitleDictionary = new Dictionary<string, ulong>
		{
			{ "FontStandard", 72057594037930001uL },
			{ "FontChineseSimplified", 72057594037930004uL },
			{ "FontExtendedChineseSimplified", 72057594037930004uL },
			{ "FontKorean", 72057594037930002uL },
			{ "FontChineseTraditional", 72057594037930003uL },
			{ "FontNintendoExtended", 72057594037930000uL }
		};
		_systemTitlesNameDictionary = new Dictionary<ulong, string>
		{
			{ 72057594037929998uL, "TimeZoneBinary" },
			{ 72057594037930000uL, "FontNintendoExtension" },
			{ 72057594037930001uL, "FontStandard" },
			{ 72057594037930002uL, "FontKorean" },
			{ 72057594037930003uL, "FontChineseTraditional" },
			{ 72057594037930004uL, "FontChineseSimple" }
		};
		_sharedFontFilenameDictionary = new Dictionary<string, string>
		{
			{ "FontStandard", "nintendo_udsg-r_std_003.bfttf" },
			{ "FontChineseSimplified", "nintendo_udsg-r_org_zh-cn_003.bfttf" },
			{ "FontExtendedChineseSimplified", "nintendo_udsg-r_ext_zh-cn_003.bfttf" },
			{ "FontKorean", "nintendo_udsg-r_ko_003.bfttf" },
			{ "FontChineseTraditional", "nintendo_udjxh-db_zh-tw_003.bfttf" },
			{ "FontNintendoExtended", "nintendo_ext_003.bfttf" }
		};
		_virtualFileSystem = virtualFileSystem;
		AocData = new SortedList<ulong, AocItem>();
	}

	public void LoadEntries(Switch device = null)
	{
		using (_lock.EnterScope())
		{
			_contentDictionary = new SortedDictionary<(ulong, NcaContentType), string>();
			_locationEntries = new Dictionary<StorageId, LinkedList<LocationEntry>>();
			StorageId[] values = Enum.GetValues<StorageId>();
			foreach (StorageId storageId in values)
			{
				if (!ContentPath.TryGetContentPath(storageId, out var contentPath) || !ContentPath.TryGetRealPath(contentPath, out var realPath))
				{
					continue;
				}
				string path = System.IO.Path.Combine(realPath, "registered");
				Directory.CreateDirectory(path);
				LinkedList<LocationEntry> locationList = new LinkedList<LocationEntry>();
				foreach (string item in Directory.EnumerateDirectories(path))
				{
					if (Directory.GetFiles(item).Length != 0)
					{
						string value = new DirectoryInfo(item).Name.Replace(".nca", string.Empty);
						using FileStream fileStream = File.OpenRead(Directory.GetFiles(item)[0]);
						Nca nca = new Nca(_virtualFileSystem.KeySet, fileStream.AsStorage());
						string text = contentPath + ":/" + fileStream.Name.Replace(realPath, string.Empty).TrimStart(System.IO.Path.DirectorySeparatorChar);
						text = text.Replace('\\', '/');
						AddEntry(new LocationEntry(text, 0, nca.Header.TitleId, nca.Header.ContentType));
						_contentDictionary.Add((nca.Header.TitleId, nca.Header.ContentType), value);
					}
				}
				foreach (string item2 in Directory.EnumerateFiles(realPath))
				{
					if (System.IO.Path.GetExtension(item2) == ".nca")
					{
						string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(item2);
						using FileStream stream = new FileStream(item2, FileMode.Open, FileAccess.Read);
						Nca nca2 = new Nca(_virtualFileSystem.KeySet, stream.AsStorage());
						string text2 = contentPath + ":/" + item2.Replace(realPath, string.Empty).TrimStart(System.IO.Path.DirectorySeparatorChar);
						text2 = text2.Replace('\\', '/');
						AddEntry(new LocationEntry(text2, 0, nca2.Header.TitleId, nca2.Header.ContentType));
						_contentDictionary.Add((nca2.Header.TitleId, nca2.Header.ContentType), fileNameWithoutExtension);
					}
				}
				if (_locationEntries.TryGetValue(storageId, out var value2) && value2 != null && value2.Count == 0)
				{
					_locationEntries.Remove(storageId);
				}
				_locationEntries.TryAdd(storageId, locationList);
				void AddEntry(LocationEntry entry)
				{
					locationList.AddLast(entry);
				}
			}
			if (device != null)
			{
				TimeManager.Instance.InitializeTimeZone(device);
				BuiltInCertificateManager.Instance.Initialize(device);
				device.System.SharedFontManager.Initialize();
			}
		}
	}

	public void AddAocItemFromFile(ulong titleId, string containerPath, string ncaPath)
	{
		AddAocItem(titleId, File.OpenRead(containerPath), ncaPath, System.IO.Path.GetExtension(containerPath).ToLowerInvariant());
	}

	public void AddAocItem(ulong titleId, Stream containerStream, string ncaPath, string extension, bool mergedToContainer = false)
	{
		if (AocData.ContainsKey(titleId))
		{
			containerStream.Dispose();
			Logger.Warning?.Print(LogClass.Application, $"Duplicate AddOnContent detected. TitleId {titleId:X16}", "AddAocItem");
			return;
		}
		try
		{
			if (!mergedToContainer)
			{
				using (PartitionFileSystemUtils.OpenApplicationFileSystem(containerStream, extension == ".xci", _virtualFileSystem, throwOnFailure: true, leaveOpen: true))
				{
				}
			}
			AocData.Add(titleId, new AocItem(containerStream, ncaPath, extension));
			Logger.Info?.Print(LogClass.Application, $"Found AddOnContent with TitleId {titleId:X16}", "AddAocItem");
		}
		catch
		{
			containerStream.Dispose();
			throw;
		}
	}

	public void ClearAocData()
	{
		foreach (KeyValuePair<ulong, AocItem> aocDatum in AocData)
		{
			aocDatum.Value.ContainerStream?.Dispose();
		}
		AocData.Clear();
	}

	public int GetAocCount()
	{
		return AocData.Count;
	}

	public IList<ulong> GetAocTitleIds()
	{
		return AocData.Select((KeyValuePair<ulong, AocItem> e) => e.Key).ToList();
	}

	public bool GetAocDataStorage(ulong aocTitleId, out IStorage aocStorage, IntegrityCheckLevel integrityCheckLevel)
	{
		aocStorage = null;
		if (AocData.TryGetValue(aocTitleId, out var value))
		{
			using (UniqueRef<IFile> uniqueRef = default)
			{
				string extension = value.Extension;
				if (!(extension == ".xci"))
				{
					if (!(extension == ".nsp"))
					{
						return false;
					}
					PartitionFileSystem partitionFileSystem = new PartitionFileSystem();
					partitionFileSystem.Initialize(value.ContainerStream.AsStorage(keepOpen: true));
					partitionFileSystem.OpenFile(ref uniqueRef.Ref, value.NcaPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
				}
				else
				{
					new Xci(_virtualFileSystem.KeySet, value.ContainerStream.AsStorage(keepOpen: true)).OpenPartition(XciPartitionType.Secure).OpenFile(ref uniqueRef.Ref, value.NcaPath.ToU8Span(), OpenMode.Read);
				}
				aocStorage = new Nca(_virtualFileSystem.KeySet, uniqueRef.Release().AsStorage()).OpenStorage(NcaSectionType.Data, integrityCheckLevel);
				return true;
			}
		}
		return false;
	}

	public void ClearEntry(ulong titleId, NcaContentType contentType, StorageId storageId)
	{
		using (_lock.EnterScope())
		{
			RemoveLocationEntry(titleId, contentType, storageId);
		}
	}

	public void RefreshEntries(StorageId storageId, int flag)
	{
		using (_lock.EnterScope())
		{
			LinkedList<LocationEntry> linkedList = _locationEntries[storageId];
			LinkedListNode<LocationEntry> linkedListNode = linkedList.First;
			while (linkedListNode != null)
			{
				LinkedListNode<LocationEntry>? next = linkedListNode.Next;
				if (linkedListNode.Value.Flag == flag)
				{
					linkedList.Remove(linkedListNode.Value);
				}
				linkedListNode = next;
			}
		}
	}

	public bool HasNca(string ncaId, StorageId storageId)
	{
		using (_lock.EnterScope())
		{
			if (_contentDictionary.ContainsValue(ncaId))
			{
				KeyValuePair<(ulong, NcaContentType), string> keyValuePair = _contentDictionary.FirstOrDefault((KeyValuePair<(ulong titleId, NcaContentType type), string> x) => x.Value == ncaId);
				ulong item = keyValuePair.Key.Item1;
				NcaContentType item2 = keyValuePair.Key.Item2;
				return GetInstalledStorage(item, item2, storageId) == storageId;
			}
		}
		return false;
	}

	public UInt128 GetInstalledNcaId(ulong titleId, NcaContentType contentType)
	{
		using (_lock.EnterScope())
		{
			if (_contentDictionary.TryGetValue((titleId, contentType), out var value))
			{
				return UInt128Utils.FromHex(value);
			}
		}
		return default;
	}

	public StorageId GetInstalledStorage(ulong titleId, NcaContentType contentType, StorageId storageId)
	{
		using (_lock.EnterScope())
		{
			LocationEntry location = GetLocation(titleId, contentType, storageId);
			return (location.ContentPath != null) ? ContentPath.GetStorageId(location.ContentPath) : StorageId.None;
		}
	}

	public string GetInstalledContentPath(ulong titleId, StorageId storageId, NcaContentType contentType)
	{
		using (_lock.EnterScope())
		{
			LocationEntry location = GetLocation(titleId, contentType, storageId);
			if (VerifyContentType(location, contentType))
			{
				return location.ContentPath;
			}
		}
		return string.Empty;
	}

	public void RedirectLocation(LocationEntry newEntry, StorageId storageId)
	{
		using (_lock.EnterScope())
		{
			if (GetLocation(newEntry.TitleId, newEntry.ContentType, storageId).ContentPath != null)
			{
				RemoveLocationEntry(newEntry.TitleId, newEntry.ContentType, storageId);
			}
			AddLocationEntry(newEntry, storageId);
		}
	}

	private bool VerifyContentType(LocationEntry locationEntry, NcaContentType contentType)
	{
		if (locationEntry.ContentPath == null)
		{
			return false;
		}
		string text = VirtualFileSystem.SwitchPathToSystemPath(locationEntry.ContentPath);
		if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
		{
			using (FileStream stream = new FileStream(text, FileMode.Open, FileAccess.Read))
			{
				return new Nca(_virtualFileSystem.KeySet, stream.AsStorage()).Header.ContentType == contentType;
			}
		}
		return false;
	}

	private void AddLocationEntry(LocationEntry entry, StorageId storageId)
	{
		LinkedList<LocationEntry> linkedList = null;
		if (_locationEntries.TryGetValue(storageId, out var value))
		{
			linkedList = value;
		}
		if (linkedList != null)
		{
			linkedList.Remove(entry);
			linkedList.AddLast(entry);
		}
	}

	private void RemoveLocationEntry(ulong titleId, NcaContentType contentType, StorageId storageId)
	{
		LinkedList<LocationEntry> linkedList = null;
		if (_locationEntries.TryGetValue(storageId, out var value))
		{
			linkedList = value;
		}
		if (linkedList != null)
		{
			LocationEntry value2 = linkedList.ToList().FirstOrDefault((LocationEntry x) => x.TitleId == titleId && x.ContentType == contentType);
			if (value2.ContentPath != null)
			{
				linkedList.Remove(value2);
			}
		}
	}

	public bool TryGetFontTitle(string fontName, out ulong titleId)
	{
		return _sharedFontTitleDictionary.TryGetValue(fontName, out titleId);
	}

	public bool TryGetFontFilename(string fontName, out string filename)
	{
		return _sharedFontFilenameDictionary.TryGetValue(fontName, out filename);
	}

	public bool TryGetSystemTitlesName(ulong titleId, out string name)
	{
		return _systemTitlesNameDictionary.TryGetValue(titleId, out name);
	}

	private LocationEntry GetLocation(ulong titleId, NcaContentType contentType, StorageId storageId)
	{
		return _locationEntries[storageId].ToList().FirstOrDefault((LocationEntry x) => x.TitleId == titleId && x.ContentType == contentType);
	}

	public void InstallFirmware(string firmwareSource)
	{
		ContentPath.TryGetContentPath(StorageId.BuiltInSystem, out var contentPath);
		ContentPath.TryGetRealPath(contentPath, out var realPath);
		string registeredDirectory = System.IO.Path.Combine(realPath, "registered");
		string text = System.IO.Path.Combine(realPath, "temp");
		if (Directory.Exists(text))
		{
			Directory.Delete(text, recursive: true);
		}
		if (Directory.Exists(firmwareSource))
		{
			InstallFromDirectory(firmwareSource, text);
			FinishInstallation(text, registeredDirectory);
			return;
		}
		if (!File.Exists(firmwareSource))
		{
			throw new FileNotFoundException("Firmware file does not exist.");
		}
		FileInfo fileInfo = new FileInfo(firmwareSource);
		using FileStream stream = File.OpenRead(firmwareSource);
		string extension = fileInfo.Extension;
		if (!(extension == ".zip"))
		{
			if (!(extension == ".xci"))
			{
				throw new InvalidFirmwarePackageException("Input file is not a valid firmware package");
			}
			Xci gameCard = new Xci(_virtualFileSystem.KeySet, stream.AsStorage());
			InstallFromCart(gameCard, text);
		}
		else
		{
			using ZipArchive archive = ZipFile.OpenRead(firmwareSource);
			InstallFromZip(archive, text);
		}
		FinishInstallation(text, registeredDirectory);
	}

	public void InstallFirmware(Stream stream, bool isXci)
	{
		ContentPath.TryGetContentPath(StorageId.BuiltInSystem, out var contentPath);
		ContentPath.TryGetRealPath(contentPath, out var realPath);
		string registeredDirectory = System.IO.Path.Combine(realPath, "registered");
		string temporaryDirectory = System.IO.Path.Combine(realPath, "temp");
		if (!isXci)
		{
			using ZipArchive archive = new ZipArchive(stream);
			InstallFromZip(archive, temporaryDirectory);
		}
		else
		{
			Xci gameCard = new Xci(_virtualFileSystem.KeySet, stream.AsStorage());
			InstallFromCart(gameCard, temporaryDirectory);
		}
		FinishInstallation(temporaryDirectory, registeredDirectory);
	}

	public void InstallKeys(string keysSource, string installDirectory)
	{
		if (Directory.Exists(keysSource))
		{
			foreach (string item in Directory.EnumerateFiles(keysSource, "*.keys"))
			{
				VerifyKeysFile(item);
				File.Copy(item, System.IO.Path.Combine(installDirectory, System.IO.Path.GetFileName(item)), overwrite: true);
			}
			return;
		}
		if (!File.Exists(keysSource))
		{
			throw new FileNotFoundException("Keys file does not exist.");
		}
		FileInfo fileInfo = new FileInfo(keysSource);
		using (File.OpenRead(keysSource))
		{
			string extension = fileInfo.Extension;
			if (!(extension == ".zip"))
			{
				if (extension == ".keys")
				{
					VerifyKeysFile(keysSource);
					File.Copy(keysSource, System.IO.Path.Combine(installDirectory, fileInfo.Name), overwrite: true);
					return;
				}
				throw new InvalidFirmwarePackageException("Input file is not a valid key package");
			}
			using ZipArchive archive = ZipFile.OpenRead(keysSource);
			InstallKeysFromZip(archive, installDirectory);
		}
	}

	private void InstallKeysFromZip(ZipArchive archive, string installDirectory)
	{
		string text = System.IO.Path.Combine(installDirectory, "temp");
		if (Directory.Exists(text))
		{
			Directory.Delete(text, recursive: true);
		}
		Directory.CreateDirectory(text);
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			if (System.IO.Path.GetExtension(entry.FullName).Equals(".keys", StringComparison.OrdinalIgnoreCase))
			{
				string text2 = System.IO.Path.Combine(text, entry.Name);
				entry.ExtractToFile(text2, overwrite: true);
				try
				{
					VerifyKeysFile(text2);
					File.Move(text2, System.IO.Path.Combine(installDirectory, entry.Name), overwrite: true);
				}
				catch (Exception)
				{
					Directory.Delete(text, recursive: true);
					throw;
				}
			}
		}
		Directory.Delete(text, recursive: true);
	}

	private void FinishInstallation(string temporaryDirectory, string registeredDirectory)
	{
		if (Directory.Exists(registeredDirectory))
		{
			new DirectoryInfo(registeredDirectory).Delete(recursive: true);
		}
		Directory.Move(temporaryDirectory, registeredDirectory ?? string.Empty);
		LoadEntries();
	}

	private void InstallFromDirectory(string firmwareDirectory, string temporaryDirectory)
	{
		InstallFromPartition(new LocalFileSystem(firmwareDirectory), temporaryDirectory);
	}

	private void InstallFromPartition(IFileSystem filesystem, string temporaryDirectory)
	{
		foreach (DirectoryEntryEx item in filesystem.EnumerateEntries("/", "*.nca"))
		{
			SaveNca(new Nca(_virtualFileSystem.KeySet, OpenPossibleFragmentedFile(filesystem, item.FullPath, OpenMode.Read).AsStorage()), item.Name.Remove(item.Name.IndexOf('.')), temporaryDirectory);
		}
	}

	private void InstallFromCart(Xci gameCard, string temporaryDirectory)
	{
		if (gameCard.HasPartition(XciPartitionType.Update))
		{
			XciPartition filesystem = gameCard.OpenPartition(XciPartitionType.Update);
			InstallFromPartition(filesystem, temporaryDirectory);
			return;
		}
		throw new Exception("Update not found in xci file.");
	}

	private static void InstallFromZip(ZipArchive archive, string temporaryDirectory)
	{
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			if (entry.FullName.EndsWith(".nca") || entry.FullName.EndsWith(".nca/00"))
			{
				string[] array = entry.FullName.Replace(".cnmt", "").Split('/');
				string text = array[^1];
				if (text.Equals("00"))
				{
					text = array[^2];
				}
				if (text.Contains(".nca"))
				{
					string text2 = System.IO.Path.Combine(temporaryDirectory, text);
					Directory.CreateDirectory(text2);
					entry.ExtractToFile(System.IO.Path.Combine(text2, "00"));
				}
			}
		}
	}

	public static void SaveNca(Nca nca, string ncaId, string temporaryDirectory)
	{
		string text = System.IO.Path.Combine(temporaryDirectory, ncaId + ".nca");
		Directory.CreateDirectory(text);
		using FileStream destination = File.Create(System.IO.Path.Combine(text, "00"));
		nca.BaseStorage.AsStream().CopyTo(destination);
	}

	private static IFile OpenPossibleFragmentedFile(IFileSystem filesystem, string path, OpenMode mode)
	{
		using UniqueRef<IFile> uniqueRef = default;
		if (filesystem.FileExists(path + "/00"))
		{
			filesystem.OpenFile(ref uniqueRef.Ref, (path + "/00").ToU8Span(), mode).ThrowIfFailure();
		}
		else
		{
			filesystem.OpenFile(ref uniqueRef.Ref, path.ToU8Span(), mode).ThrowIfFailure();
		}
		return uniqueRef.Release();
	}

	private static Stream GetZipStream(ZipArchiveEntry entry)
	{
		MemoryStream stream = MemoryStreamManager.Shared.GetStream();
		using Stream stream2 = entry.Open();
		stream2.CopyTo(stream);
		return stream;
	}

	public SystemVersion VerifyFirmwarePackage(string firmwarePackage)
	{
		_virtualFileSystem.ReloadKeySet();
		if (_virtualFileSystem.KeySet.HeaderKey.IsZeros())
		{
			throw new MissingKeyException("HeaderKey is empty. Cannot decrypt NCA headers.");
		}
		if (Directory.Exists(firmwarePackage))
		{
			return VerifyAndGetVersionDirectory(firmwarePackage);
		}
		if (!File.Exists(firmwarePackage))
		{
			throw new FileNotFoundException("Firmware file does not exist.");
		}
		FileInfo fileInfo = new FileInfo(firmwarePackage);
		if (fileInfo.Extension == ".zip" || fileInfo.Extension == ".xci")
		{
			using (FileStream file = File.OpenRead(firmwarePackage))
			{
				bool isXci = fileInfo.Extension == ".xci";
				return VerifyFirmwarePackage(file, isXci);
			}
		}
		return null;
		SystemVersion VerifyAndGetVersionDirectory(string firmwareDirectory)
		{
			return VerifyAndGetVersion(new LocalFileSystem(firmwareDirectory));
		}
	}

	public SystemVersion VerifyFirmwarePackage(Stream file, bool isXci)
	{
		if (!isXci)
		{
			using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Read))
			{
				return VerifyAndGetVersionZip(archive);
			}
		}
		Xci xci = new Xci(_virtualFileSystem.KeySet, file.AsStorage());
		if (xci.HasPartition(XciPartitionType.Update))
		{
			XciPartition filesystem = xci.OpenPartition(XciPartitionType.Update);
			return VerifyAndGetVersion(filesystem);
		}
		throw new InvalidFirmwarePackageException("Update not found in xci file.");
	}

	private SystemVersion VerifyAndGetVersionZip(ZipArchive archive)
	{
		Dictionary<ulong, List<(NcaContentType, string)>> dictionary = new Dictionary<ulong, List<(NcaContentType, string)>>();
		SystemVersion result = null;
		foreach (ZipArchiveEntry entry3 in archive.Entries)
		{
			if (!entry3.FullName.EndsWith(".nca") && !entry3.FullName.EndsWith(".nca/00"))
			{
				continue;
			}
			using Stream stream = GetZipStream(entry3);
			IStorage storage = stream.AsStorage();
			Nca nca = new Nca(_virtualFileSystem.KeySet, storage);
			if (dictionary.TryGetValue(nca.Header.TitleId, out var value))
			{
				value.Add((nca.Header.ContentType, entry3.FullName));
			}
			else if (dictionary.TryAdd(nca.Header.TitleId, new List<(NcaContentType, string)>()))
			{
				dictionary[nca.Header.TitleId].Add((nca.Header.ContentType, entry3.FullName));
			}
		}
		if (dictionary.TryGetValue(72057594037930006uL, out var value2))
		{
			string item = value2.FirstOrDefault(((NcaContentType type, string path) x) => x.type == NcaContentType.Meta).Item2;
			CnmtContentMetaEntry[] array = null;
			using (Stream stream2 = GetZipStream(archive.GetEntry(item)))
			{
				IFileSystem fileSystem = new Nca(_virtualFileSystem.KeySet, stream2.AsStorage()).OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
				string fullPath = fileSystem.EnumerateEntries("/", "*.cnmt").Single().FullPath;
				using UniqueRef<IFile> uniqueRef = default;
				if (fileSystem.OpenFile(ref uniqueRef.Ref, fullPath.ToU8Span(), OpenMode.Read).IsSuccess())
				{
					Cnmt cnmt = new Cnmt(uniqueRef.Get.AsStream());
					if (cnmt.Type == ContentMetaType.SystemUpdate)
					{
						array = cnmt.MetaEntries;
						dictionary.Remove(72057594037930006uL);
					}
				}
			}
			if (array == null)
			{
				throw new FileNotFoundException("System update title was not found in the firmware package.");
			}
			if (dictionary.TryGetValue(72057594037929993uL, out var value3))
			{
				string item2 = value3.FirstOrDefault(((NcaContentType type, string path) x) => x.type != NcaContentType.Meta).Item2;
				using Stream stream3 = GetZipStream(archive.GetEntry(item2));
				IFileSystem fileSystem2 = new Nca(_virtualFileSystem.KeySet, stream3.AsStorage()).OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
				using UniqueRef<IFile> uniqueRef2 = default;
				if (fileSystem2.OpenFile(ref uniqueRef2.Ref, "/file".ToU8Span(), OpenMode.Read).IsSuccess())
				{
					result = new SystemVersion(uniqueRef2.Get.AsStream());
				}
			}
			CnmtContentMetaEntry[] array2 = array;
			foreach (CnmtContentMetaEntry cnmtContentMetaEntry in array2)
			{
				if (!dictionary.TryGetValue(cnmtContentMetaEntry.TitleId, out value2))
				{
					continue;
				}
				item = value2.FirstOrDefault(((NcaContentType type, string path) x) => x.type == NcaContentType.Meta).Item2;
				string item3 = value2.FirstOrDefault(((NcaContentType type, string path) x) => x.type != NcaContentType.Meta).Item2;
				if (item3 == null)
				{
					dictionary.Remove(cnmtContentMetaEntry.TitleId);
					continue;
				}
				ZipArchiveEntry? entry = archive.GetEntry(item);
				ZipArchiveEntry entry2 = archive.GetEntry(item3);
				using Stream stream4 = GetZipStream(entry);
				using Stream stream5 = GetZipStream(entry2);
				IFileSystem fileSystem3 = new Nca(_virtualFileSystem.KeySet, stream4.AsStorage()).OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
				string fullPath2 = fileSystem3.EnumerateEntries("/", "*.cnmt").Single().FullPath;
				using UniqueRef<IFile> uniqueRef3 = default;
				if (!fileSystem3.OpenFile(ref uniqueRef3.Ref, fullPath2.ToU8Span(), OpenMode.Read).IsSuccess())
				{
					continue;
				}
				Cnmt cnmt2 = new Cnmt(uniqueRef3.Get.AsStream());
				IStorage storage2 = stream5.AsStorage();
				if (storage2.GetSize(out var size).IsSuccess())
				{
					byte[] array3 = new byte[size];
					Span<byte> span = new Span<byte>(array3);
					storage2.Read(0L, span);
					Span<byte> hashBuffer = new Span<byte>(new byte[32]);
					Sha256.GenerateSha256Hash(span, hashBuffer);
					if (LibHac.Common.Utilities.ArraysEqual(hashBuffer.ToArray(), cnmt2.ContentEntries[0].Hash))
					{
						dictionary.Remove(cnmtContentMetaEntry.TitleId);
					}
				}
			}
			if (dictionary.Count > 0)
			{
				StringBuilder stringBuilder = new StringBuilder();
				foreach (KeyValuePair<ulong, List<(NcaContentType, string)>> item5 in dictionary)
				{
					foreach (var item6 in item5.Value)
					{
						string item4 = item6.Item2;
						stringBuilder.AppendLine(item4);
					}
				}
				throw new InvalidFirmwarePackageException($"Firmware package contains unrelated archives. Please remove these paths: {Environment.NewLine}{stringBuilder}");
			}
			return result;
		}
		throw new FileNotFoundException("System update title was not found in the firmware package.");
	}

	private SystemVersion VerifyAndGetVersion(IFileSystem filesystem)
	{
		Dictionary<ulong, List<(NcaContentType, string)>> dictionary = new Dictionary<ulong, List<(NcaContentType, string)>>();
		SystemVersion result = null;
		CnmtContentMetaEntry[] array = null;
		foreach (DirectoryEntryEx item4 in filesystem.EnumerateEntries("/", "*.nca"))
		{
			IStorage storage = OpenPossibleFragmentedFile(filesystem, item4.FullPath, OpenMode.Read).AsStorage();
			Nca nca = new Nca(_virtualFileSystem.KeySet, storage);
			if (nca.Header.TitleId == 72057594037930006L && nca.Header.ContentType == NcaContentType.Meta)
			{
				IFileSystem fileSystem = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
				string fullPath = fileSystem.EnumerateEntries("/", "*.cnmt").Single().FullPath;
				using (UniqueRef<IFile> uniqueRef = default)
				{
					if (fileSystem.OpenFile(ref uniqueRef.Ref, fullPath.ToU8Span(), OpenMode.Read).IsSuccess())
					{
						Cnmt cnmt = new Cnmt(uniqueRef.Get.AsStream());
						if (cnmt.Type == ContentMetaType.SystemUpdate)
						{
							array = cnmt.MetaEntries;
						}
					}
				}
				continue;
			}
			if (nca.Header.TitleId == 72057594037929993L && nca.Header.ContentType == NcaContentType.Data)
			{
				IFileSystem fileSystem2 = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
				using UniqueRef<IFile> uniqueRef2 = default;
				if (fileSystem2.OpenFile(ref uniqueRef2.Ref, "/file".ToU8Span(), OpenMode.Read).IsSuccess())
				{
					result = new SystemVersion(uniqueRef2.Get.AsStream());
				}
			}
			if (dictionary.TryGetValue(nca.Header.TitleId, out var value))
			{
				value.Add((nca.Header.ContentType, item4.FullPath));
			}
			else if (dictionary.TryAdd(nca.Header.TitleId, new List<(NcaContentType, string)>()))
			{
				dictionary[nca.Header.TitleId].Add((nca.Header.ContentType, item4.FullPath));
			}
			storage.Dispose();
		}
		if (array == null)
		{
			throw new FileNotFoundException("System update title was not found in the firmware package.");
		}
		CnmtContentMetaEntry[] array2 = array;
		foreach (CnmtContentMetaEntry cnmtContentMetaEntry in array2)
		{
			if (!dictionary.TryGetValue(cnmtContentMetaEntry.TitleId, out var value2))
			{
				continue;
			}
			string item = value2.FirstOrDefault(((NcaContentType type, string path) x) => x.type == NcaContentType.Meta).Item2;
			string item2 = value2.FirstOrDefault(((NcaContentType type, string path) x) => x.type != NcaContentType.Meta).Item2;
			if (item2 == null)
			{
				dictionary.Remove(cnmtContentMetaEntry.TitleId);
				continue;
			}
			IStorage storage2 = OpenPossibleFragmentedFile(filesystem, item, OpenMode.Read).AsStorage();
			IStorage storage3 = OpenPossibleFragmentedFile(filesystem, item2, OpenMode.Read).AsStorage();
			IFileSystem fileSystem3 = new Nca(_virtualFileSystem.KeySet, storage2).OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
			string fullPath2 = fileSystem3.EnumerateEntries("/", "*.cnmt").Single().FullPath;
			using UniqueRef<IFile> uniqueRef3 = default;
			if (!fileSystem3.OpenFile(ref uniqueRef3.Ref, fullPath2.ToU8Span(), OpenMode.Read).IsSuccess())
			{
				continue;
			}
			Cnmt cnmt2 = new Cnmt(uniqueRef3.Get.AsStream());
			if (storage3.GetSize(out var size).IsSuccess())
			{
				byte[] array3 = new byte[size];
				Span<byte> span = new Span<byte>(array3);
				storage3.Read(0L, span);
				Span<byte> hashBuffer = new Span<byte>(new byte[32]);
				Sha256.GenerateSha256Hash(span, hashBuffer);
				if (LibHac.Common.Utilities.ArraysEqual(hashBuffer.ToArray(), cnmt2.ContentEntries[0].Hash))
				{
					dictionary.Remove(cnmtContentMetaEntry.TitleId);
				}
			}
		}
		if (dictionary.Count > 0)
		{
			StringBuilder stringBuilder = new StringBuilder();
			foreach (KeyValuePair<ulong, List<(NcaContentType, string)>> item5 in dictionary)
			{
				foreach (var item6 in item5.Value)
				{
					string item3 = item6.Item2;
					stringBuilder.AppendLine(item3);
				}
			}
			throw new InvalidFirmwarePackageException($"Firmware package contains unrelated archives. Please remove these paths: {Environment.NewLine}{stringBuilder}");
		}
		return result;
	}

	public SystemVersion GetCurrentFirmwareVersion()
	{
		LoadEntries();
		using (_lock.EnterScope())
		{
			foreach (LocationEntry item in _locationEntries[StorageId.BuiltInSystem])
			{
				if (item.ContentType != NcaContentType.Data)
				{
					continue;
				}
				using FileStream stream = File.OpenRead(VirtualFileSystem.SwitchPathToSystemPath(item.ContentPath));
				Nca nca = new Nca(_virtualFileSystem.KeySet, stream.AsStorage());
				if (nca.Header.TitleId != 72057594037929993L || nca.Header.ContentType != NcaContentType.Data)
				{
					continue;
				}
				IFileSystem fileSystem = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);
				using UniqueRef<IFile> uniqueRef = default;
				if (fileSystem.OpenFile(ref uniqueRef.Ref, "/file".ToU8Span(), OpenMode.Read).IsSuccess())
				{
					return new SystemVersion(uniqueRef.Get.AsStream());
				}
			}
		}
		return null;
	}

	public void VerifyKeysFile(string filePath)
	{
		string regex = "^[a-z0-9_]+ = [a-z0-9]+$";
		string regex2 = "^[a-z0-9]{32} = [a-z0-9]{32}$";
		if (File.Exists(filePath))
		{
			string fileName = System.IO.Path.GetFileName(filePath);
			string[] lines = File.ReadAllLines(filePath);
			bool flag = false;
			switch (fileName)
			{
			case "prod.keys":
				flag = verifyKeys(lines, regex);
				break;
			case "title.keys":
				flag = verifyKeys(lines, regex2);
				break;
			case "console.keys":
			case "dev.keys":
				flag = verifyKeys(lines, regex);
				break;
			default:
				throw new FormatException("Keys file name \"" + fileName + "\" not supported. Only \"prod.keys\", \"title.keys\", \"console.keys\", \"dev.keys\" are supported.");
			}
			if (!flag)
			{
				throw new FormatException("Invalid \"" + filePath + "\" file format.");
			}
			return;
		}
		throw new FileNotFoundException("Keys file not found at \"" + filePath + "\".");
	}

	private bool verifyKeys(string[] lines, string regex)
	{
		for (int i = 0; i < lines.Length; i++)
		{
			if (!Regex.IsMatch(lines[i], regex))
			{
				return false;
			}
		}
		return true;
	}

	public bool AreKeysAlredyPresent(string pathToCheck)
	{
		string[] array = new string[4] { "prod.keys", "title.keys", "console.keys", "dev.keys" };
		foreach (string path in array)
		{
			if (File.Exists(System.IO.Path.Combine(pathToCheck, path)))
			{
				return true;
			}
		}
		return false;
	}
}
