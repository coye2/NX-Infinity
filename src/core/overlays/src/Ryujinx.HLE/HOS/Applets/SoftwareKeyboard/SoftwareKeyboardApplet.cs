using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Ryujinx.Common;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Applets.SoftwareKeyboard;
using Ryujinx.HLE.HOS.Services.Am.AppletAE;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.Npad;
using Ryujinx.HLE.UI;
using Ryujinx.HLE.UI.Input;
using Ryujinx.Memory;

namespace Ryujinx.HLE.HOS.Applets;

internal class SoftwareKeyboardApplet : IApplet
{
	private const string DefaultInputText = "Ryujinx";

	private const int StandardBufferSize = 2008;

	private const int InteractiveBufferSize = 2004;

	private const int MaxUserWords = 5000;

	private const int MaxUiTextSize = 100;

	private const Key CycleInputModesKey = Key.F6;

	private readonly Switch _device;

	private SoftwareKeyboardState _foregroundState;

	private volatile InlineKeyboardState _backgroundState;

	private bool _isBackground;

	private AppletSession _normalSession;

	private AppletSession _interactiveSession;

	private SoftwareKeyboardConfig _keyboardForegroundConfig;

	private SoftwareKeyboardInitialize _keyboardBackgroundInitialize;

	private SoftwareKeyboardCustomizeDic _keyboardBackgroundDic;

	private SoftwareKeyboardDictSet _keyboardBackgroundDictSet;

	private SoftwareKeyboardUserWord[] _keyboardBackgroundUserWords;

	private byte[] _transferMemory;

	private string _textValue = "";

	private int _cursorBegin;

	private Encoding _encoding = Encoding.Unicode;

	private KeyboardResult _lastResult;

	private IDynamicTextInputHandler _dynamicTextInputHandler;

	private SoftwareKeyboardRenderer _keyboardRenderer;

	private NpadReader _npads;

	private bool _canAcceptController;

	private KeyboardInputMode _inputMode;

	private readonly Lock _lock = new Lock();

	public event EventHandler AppletStateChanged;

	public SoftwareKeyboardApplet(Horizon system)
	{
		_device = system.Device;
	}

	public ResultCode Start(AppletSession normalSession, AppletSession interactiveSession)
	{
		using (_lock.EnterScope())
		{
			_normalSession = normalSession;
			_interactiveSession = interactiveSession;
			_interactiveSession.DataAvailable += OnInteractiveData;
			_normalSession.Pop();
			byte[] array = _normalSession.Pop();
			_isBackground = array.Length == Unsafe.SizeOf<SoftwareKeyboardInitialize>();
			if (_isBackground)
			{
				_keyboardBackgroundInitialize = MemoryMarshal.Read<SoftwareKeyboardInitialize>(array);
				_backgroundState = InlineKeyboardState.Uninitialized;
				if (_device.UIHandler == null)
				{
					Logger.Error?.Print(LogClass.ServiceAm, "GUI Handler is not set, software keyboard applet will not work properly", "Start");
				}
				else
				{
					_dynamicTextInputHandler = _device.UIHandler.CreateDynamicTextInputHandler();
					_dynamicTextInputHandler.TextChangedEvent += HandleTextChangedEvent;
					_dynamicTextInputHandler.KeyPressedEvent += HandleKeyPressedEvent;
					_npads = new NpadReader(_device);
					_npads.NpadButtonDownEvent += HandleNpadButtonDownEvent;
					_npads.NpadButtonUpEvent += HandleNpadButtonUpEvent;
					_keyboardRenderer = new SoftwareKeyboardRenderer(_device.UIHandler.HostUITheme);
				}
				return ResultCode.Success;
			}
			if (array.Length < Marshal.SizeOf<SoftwareKeyboardConfig>())
			{
				Logger.Error?.Print(LogClass.ServiceAm, $"SoftwareKeyboardConfig size mismatch. Expected {Marshal.SizeOf<SoftwareKeyboardConfig>():x}. Got {array.Length:x}", "Start");
			}
			else
			{
				_keyboardForegroundConfig = ReadStruct<SoftwareKeyboardConfig>(array);
			}
			if (!_normalSession.TryPop(out _transferMemory))
			{
				Logger.Error?.Print(LogClass.ServiceAm, "SwKbd Transfer Memory is null", "Start");
			}
			if (_keyboardForegroundConfig.UseUtf8)
			{
				_encoding = Encoding.UTF8;
			}
			_foregroundState = SoftwareKeyboardState.Ready;
			ExecuteForegroundKeyboard();
			return ResultCode.Success;
		}
	}

	private bool IsKeyboardActive()
	{
		if (_backgroundState >= InlineKeyboardState.Appearing)
		{
			return _backgroundState < InlineKeyboardState.Disappearing;
		}
		return false;
	}

	private bool InputModeControllerEnabled()
	{
		if (_inputMode != KeyboardInputMode.ControllerAndKeyboard)
		{
			return _inputMode == KeyboardInputMode.ControllerOnly;
		}
		return true;
	}

	private bool InputModeTypingEnabled()
	{
		if (_inputMode != KeyboardInputMode.ControllerAndKeyboard)
		{
			return _inputMode == KeyboardInputMode.KeyboardOnly;
		}
		return true;
	}

	private void AdvanceInputMode()
	{
		_inputMode = (KeyboardInputMode)((int)(_inputMode + 1) % 3);
	}

	public bool DrawTo(RenderingSurfaceInfo surfaceInfo, IVirtualMemoryManager destination, ulong position)
	{
		_npads?.Update();
		_keyboardRenderer?.SetSurfaceInfo(surfaceInfo);
		return _keyboardRenderer?.DrawTo(destination, position) ?? false;
	}

	private void ExecuteForegroundKeyboard(string retryText = null)
	{
		string text = null;
		if (_transferMemory != null && _keyboardForegroundConfig.InitialStringLength > 0)
		{
			text = Encoding.Unicode.GetString(_transferMemory, _keyboardForegroundConfig.InitialStringOffset, 2 * _keyboardForegroundConfig.InitialStringLength);
		}
		text = retryText ?? text;
		if (_keyboardForegroundConfig.StringLengthMax == 0)
		{
			_keyboardForegroundConfig.StringLengthMax = 100;
		}
		if (_device.UIHandler == null)
		{
			Logger.Warning?.Print(LogClass.Application, "GUI Handler is not set. Falling back to default", "ExecuteForegroundKeyboard");
			_textValue = "Ryujinx";
			_lastResult = KeyboardResult.Accept;
		}
		else
		{
			SoftwareKeyboardUIArgs args = new SoftwareKeyboardUIArgs
			{
				KeyboardMode = _keyboardForegroundConfig.Mode,
				DisabledCharacters = (uint)_keyboardForegroundConfig.InvalidCharFlag,
				ExtraSymbols = new string(new char[2] { _keyboardForegroundConfig.LeftOptionalSymbolKey, _keyboardForegroundConfig.RightOptionalSymbolKey }).Replace("\0", ""),
				Password = (_keyboardForegroundConfig.PasswordMode == PasswordMode.Enabled),
				CursorPosition = ((_keyboardForegroundConfig.InitialCursorPosition != InitialCursorPosition.Start) ? (text ?? "").Length : 0),
				HeaderText = StripUnicodeControlCodes(_keyboardForegroundConfig.HeaderText),
				SubtitleText = StripUnicodeControlCodes(_keyboardForegroundConfig.SubtitleText),
				GuideText = StripUnicodeControlCodes(_keyboardForegroundConfig.GuideText),
				SubmitText = ((!string.IsNullOrWhiteSpace(_keyboardForegroundConfig.SubmitText)) ? _keyboardForegroundConfig.SubmitText : "OK"),
				StringLengthMin = _keyboardForegroundConfig.StringLengthMin,
				StringLengthMax = _keyboardForegroundConfig.StringLengthMax,
				InitialText = text
			};
			_lastResult = (_device.UIHandler.DisplayInputDialog(args, out _textValue) ? KeyboardResult.Accept : KeyboardResult.Cancel);
			if (_textValue == null)
			{
				_textValue = text ?? "Ryujinx";
			}
		}
		if (_lastResult == KeyboardResult.Accept && (_textValue.Length < _keyboardForegroundConfig.StringLengthMin || _textValue.Length > _keyboardForegroundConfig.StringLengthMax))
		{
			_lastResult = KeyboardResult.Cancel;
		}
		if (_keyboardForegroundConfig.CheckText && _lastResult == KeyboardResult.Accept)
		{
			_foregroundState = SoftwareKeyboardState.ValidationPending;
			PushForegroundResponse(interactive: true);
		}
		else
		{
			_foregroundState = SoftwareKeyboardState.Complete;
			PushForegroundResponse(interactive: false);
			AppletStateChanged?.Invoke(this, null);
		}
	}

	private void OnInteractiveData(object sender, EventArgs e)
	{
		byte[] data = _interactiveSession.Pop();
		if (_isBackground)
		{
			using (_lock.EnterScope())
			{
				OnBackgroundInteractiveData(data);
				return;
			}
		}
		OnForegroundInteractiveData(data);
	}

	private void OnForegroundInteractiveData(byte[] data)
	{
		if (_foregroundState == SoftwareKeyboardState.ValidationPending)
		{
			uint num = ((data.Length >= 4) ? BitConverter.ToUInt32(data, 0) : uint.MaxValue);
			string message = ((data.Length > 4) ? _encoding.GetString(data, 4, Math.Min(data.Length - 4, 2000)).TrimEnd('\0') : "");
			switch (num)
			{
			case 1u:
				_device.UIHandler?.DisplayMessageDialog("Check your text", message);
				ExecuteForegroundKeyboard(_textValue);
				return;
			case 2u:
			{
				IHostUIHandler uIHandler = _device.UIHandler;
				if (uIHandler == null || !uIHandler.DisplayMessageDialog("Confirm text", message))
				{
					ExecuteForegroundKeyboard(_textValue);
					return;
				}
				break;
			}
			}
			if (num > 3)
			{
				_lastResult = KeyboardResult.Cancel;
			}
			PushForegroundResponse(interactive: false);
			AppletStateChanged?.Invoke(this, null);
			_foregroundState = SoftwareKeyboardState.Complete;
		}
		else
		{
			if (_foregroundState != SoftwareKeyboardState.Complete)
			{
				throw new InvalidOperationException("Software Keyboard is in an invalid state.");
			}
			PushForegroundResponse(interactive: false);
			AppletStateChanged?.Invoke(this, null);
		}
	}

	private void OnBackgroundInteractiveData(byte[] data)
	{
		using MemoryStream memoryStream = new MemoryStream(data);
		using BinaryReader binaryReader = new BinaryReader(memoryStream);
		InlineKeyboardRequest inlineKeyboardRequest = (InlineKeyboardRequest)binaryReader.ReadUInt32();
		Logger.Debug?.Print(LogClass.ServiceAm, $"Keyboard received command {inlineKeyboardRequest} in state {_backgroundState}", "OnBackgroundInteractiveData");
		switch (inlineKeyboardRequest)
		{
		case InlineKeyboardRequest.UseChangedStringV2:
			Logger.Stub?.Print(LogClass.ServiceAm, "Inline keyboard request UseChangedStringV2", "OnBackgroundInteractiveData");
			break;
		case InlineKeyboardRequest.UseMovedCursorV2:
			Logger.Stub?.Print(LogClass.ServiceAm, "Inline keyboard request UseMovedCursorV2", "OnBackgroundInteractiveData");
			break;
		case InlineKeyboardRequest.SetUserWordInfo:
		{
			long num = memoryStream.Length - memoryStream.Position;
			if (num < 4)
			{
				Logger.Warning?.Print(LogClass.ServiceAm, $"Received invalid Software Keyboard User Word Info of {num} bytes", "OnBackgroundInteractiveData");
			}
			else
			{
				int num2 = binaryReader.ReadInt32();
				int num3 = Unsafe.SizeOf<SoftwareKeyboardUserWord>();
				num = memoryStream.Length - memoryStream.Position;
				if (num2 > 5000)
				{
					Logger.Warning?.Print(LogClass.ServiceAm, $"Received {num2} User Words but the maximum is {5000}", "OnBackgroundInteractiveData");
				}
				else if (num2 * num3 != num)
				{
					Logger.Warning?.Print(LogClass.ServiceAm, $"Received invalid Software Keyboard User Word Info data of {num} bytes for {num2} words", "OnBackgroundInteractiveData");
				}
				else
				{
					_keyboardBackgroundUserWords = new SoftwareKeyboardUserWord[num2];
					for (int i = 0; i < num2; i++)
					{
						_keyboardBackgroundUserWords[i] = binaryReader.ReadStruct<SoftwareKeyboardUserWord>();
					}
				}
			}
			_interactiveSession.Push(InlineResponses.ReleasedUserWordInfo(_backgroundState));
			break;
		}
		case InlineKeyboardRequest.SetCustomizeDic:
		{
			long num = memoryStream.Length - memoryStream.Position;
			if (num != Unsafe.SizeOf<SoftwareKeyboardCustomizeDic>())
			{
				Logger.Warning?.Print(LogClass.ServiceAm, $"Received invalid Software Keyboard Customize Dic of {num} bytes", "OnBackgroundInteractiveData");
			}
			else
			{
				_keyboardBackgroundDic = binaryReader.ReadStruct<SoftwareKeyboardCustomizeDic>();
			}
			break;
		}
		case InlineKeyboardRequest.SetCustomizedDictionaries:
		{
			long num = memoryStream.Length - memoryStream.Position;
			if (num != Unsafe.SizeOf<SoftwareKeyboardDictSet>())
			{
				Logger.Warning?.Print(LogClass.ServiceAm, $"Received invalid Software Keyboard DictSet of {num} bytes", "OnBackgroundInteractiveData");
			}
			else
			{
				_keyboardBackgroundDictSet = binaryReader.ReadStruct<SoftwareKeyboardDictSet>();
			}
			break;
		}
		case InlineKeyboardRequest.Calc:
		{
			long num = memoryStream.Length - memoryStream.Position;
			SoftwareKeyboardCalcEx softwareKeyboardCalcEx;
			if (num == Marshal.SizeOf<SoftwareKeyboardCalc>())
			{
				softwareKeyboardCalcEx = ReadStruct<SoftwareKeyboardCalc>(binaryReader.ReadBytes((int)num)).ToExtended();
			}
			else if (num == Marshal.SizeOf<SoftwareKeyboardCalcEx>() || num == 1256)
			{
				softwareKeyboardCalcEx = ReadStruct<SoftwareKeyboardCalcEx>(binaryReader.ReadBytes((int)num));
			}
			else
			{
				Logger.Error?.Print(LogClass.ServiceAm, $"Received invalid Software Keyboard Calc of {num} bytes", "OnBackgroundInteractiveData");
				softwareKeyboardCalcEx = default;
			}
			bool flag = false;
			if ((softwareKeyboardCalcEx.Flags & KeyboardCalcFlags.Initialize) != (KeyboardCalcFlags)0uL)
			{
				_interactiveSession.Push(InlineResponses.FinishedInitialize(_backgroundState));
				_backgroundState = InlineKeyboardState.Initialized;
			}
			if ((softwareKeyboardCalcEx.Flags & KeyboardCalcFlags.SetCursorPos) != (KeyboardCalcFlags)0uL)
			{
				_cursorBegin = softwareKeyboardCalcEx.CursorPos;
				flag = true;
				Logger.Debug?.Print(LogClass.ServiceAm, $"Cursor position set to {_cursorBegin}", "OnBackgroundInteractiveData");
			}
			if ((softwareKeyboardCalcEx.Flags & KeyboardCalcFlags.SetInputText) != (KeyboardCalcFlags)0uL)
			{
				_textValue = softwareKeyboardCalcEx.InputText;
				flag = true;
				Logger.Debug?.Print(LogClass.ServiceAm, "Input text set to " + _textValue, "OnBackgroundInteractiveData");
			}
			if ((softwareKeyboardCalcEx.Flags & KeyboardCalcFlags.SetUtf8Mode) != (KeyboardCalcFlags)0uL)
			{
				_encoding = (softwareKeyboardCalcEx.UseUtf8 ? Encoding.UTF8 : Encoding.Default);
				Logger.Debug?.Print(LogClass.ServiceAm, $"Encoding set to {_encoding}", "OnBackgroundInteractiveData");
			}
			if (flag)
			{
				_dynamicTextInputHandler.SetText(_textValue, _cursorBegin);
				_keyboardRenderer.UpdateTextState(_textValue, _cursorBegin, _cursorBegin, null, null);
			}
			if ((softwareKeyboardCalcEx.Flags & KeyboardCalcFlags.MustShow) != (KeyboardCalcFlags)0uL)
			{
				ActivateFrontend();
				_backgroundState = InlineKeyboardState.Shown;
				PushChangedString(_textValue, (uint)_cursorBegin, _backgroundState);
			}
			_interactiveSession.Push(InlineResponses.Default(_backgroundState));
			break;
		}
		case InlineKeyboardRequest.Finalize:
			DestroyFrontend();
			_backgroundState = InlineKeyboardState.Uninitialized;
			AppletStateChanged?.Invoke(this, null);
			break;
		default:
			Logger.Warning?.Print(LogClass.ServiceAm, $"Invalid Software Keyboard request {inlineKeyboardRequest} during state {_backgroundState}", "OnBackgroundInteractiveData");
			_interactiveSession.Push(InlineResponses.Default(_backgroundState));
			break;
		}
	}

	private void ActivateFrontend()
	{
		Logger.Debug?.Print(LogClass.ServiceAm, "Activating software keyboard frontend", "ActivateFrontend");
		_inputMode = KeyboardInputMode.ControllerAndKeyboard;
		_npads.Update(supressEvents: true);
		NpadButton currentButtonsOfAllNpads = _npads.GetCurrentButtonsOfAllNpads();
		_canAcceptController = (currentButtonsOfAllNpads & NpadButton.A) == 0;
		_dynamicTextInputHandler.TextProcessingEnabled = true;
		_keyboardRenderer.UpdateCommandState(null, null, true);
		_keyboardRenderer.UpdateTextState(null, null, null, null, true);
	}

	private void DeactivateFrontend()
	{
		Logger.Debug?.Print(LogClass.ServiceAm, "Deactivating software keyboard frontend", "DeactivateFrontend");
		_inputMode = KeyboardInputMode.ControllerAndKeyboard;
		_canAcceptController = false;
		_dynamicTextInputHandler.TextProcessingEnabled = false;
		_dynamicTextInputHandler.SetText(_textValue, _cursorBegin);
	}

	private void DestroyFrontend()
	{
		Logger.Debug?.Print(LogClass.ServiceAm, "Destroying software keyboard frontend", "DestroyFrontend");
		_keyboardRenderer?.Dispose();
		_keyboardRenderer = null;
		if (_dynamicTextInputHandler != null)
		{
			_dynamicTextInputHandler.TextChangedEvent -= HandleTextChangedEvent;
			_dynamicTextInputHandler.KeyPressedEvent -= HandleKeyPressedEvent;
			_dynamicTextInputHandler.Dispose();
			_dynamicTextInputHandler = null;
		}
		if (_npads != null)
		{
			_npads.NpadButtonDownEvent -= HandleNpadButtonDownEvent;
			_npads.NpadButtonUpEvent -= HandleNpadButtonUpEvent;
			_npads = null;
		}
	}

	private bool HandleKeyPressedEvent(Key key)
	{
		if ((uint)(key - 49) <= 1u)
		{
			using (_lock.EnterScope())
			{
				if (IsKeyboardActive() && InputModeTypingEnabled())
				{
					PushUpdatedState(_textValue, _cursorBegin, (key == Key.Enter) ? KeyboardResult.Accept : KeyboardResult.Cancel);
				}
			}
			return false;
		}
		if (key == Key.F6)
		{
			using (_lock.EnterScope())
			{
				if (IsKeyboardActive())
				{
					AdvanceInputMode();
					bool flag = InputModeTypingEnabled();
					bool value = InputModeControllerEnabled();
					_dynamicTextInputHandler.TextProcessingEnabled = flag;
					_keyboardRenderer.UpdateTextState(null, null, null, null, flag);
					_keyboardRenderer.UpdateCommandState(null, null, value);
				}
			}
		}
		return true;
	}

	private void HandleTextChangedEvent(string text, int cursorBegin, int cursorEnd, bool overwriteMode)
	{
		using (_lock.EnterScope())
		{
			if (text.Length > 100)
			{
				text = text.Substring(0, 100);
				cursorBegin = Math.Min(cursorBegin, 100);
				cursorEnd = Math.Min(cursorEnd, 100);
				_dynamicTextInputHandler.SetText(text, cursorBegin, cursorEnd);
			}
			_textValue = text;
			_cursorBegin = cursorBegin;
			_keyboardRenderer.UpdateTextState(text, cursorBegin, cursorEnd, overwriteMode, null);
			PushUpdatedState(text, cursorBegin, KeyboardResult.NotSet);
		}
	}

	private void HandleNpadButtonDownEvent(int npadIndex, NpadButton button)
	{
		using (_lock.EnterScope())
		{
			if (IsKeyboardActive())
			{
				switch (button)
				{
				case NpadButton.A:
					_keyboardRenderer.UpdateCommandState(_canAcceptController, null, null);
					break;
				case NpadButton.B:
					_keyboardRenderer.UpdateCommandState(null, _canAcceptController, null);
					break;
				}
			}
		}
	}

	private void HandleNpadButtonUpEvent(int npadIndex, NpadButton button)
	{
		using (_lock.EnterScope())
		{
			KeyboardResult result = KeyboardResult.NotSet;
			switch (button)
			{
			case NpadButton.A:
				result = KeyboardResult.Accept;
				_keyboardRenderer.UpdateCommandState(false, null, null);
				break;
			case NpadButton.B:
				result = KeyboardResult.Cancel;
				_keyboardRenderer.UpdateCommandState(null, false, null);
				break;
			}
			if (IsKeyboardActive())
			{
				if (!_canAcceptController)
				{
					_canAcceptController = true;
				}
				else if (InputModeControllerEnabled())
				{
					PushUpdatedState(_textValue, _cursorBegin, result);
				}
			}
		}
	}

	private void PushUpdatedState(string text, int cursorBegin, KeyboardResult result)
	{
		_lastResult = result;
		_textValue = text;
		bool flag = result == KeyboardResult.Cancel;
		bool flag2 = result == KeyboardResult.Accept;
		if (!IsKeyboardActive())
		{
			return;
		}
		if (!flag2 && !flag)
		{
			Logger.Debug?.Print(LogClass.ServiceAm, $"Updating keyboard text to {text} and cursor position to {cursorBegin}", "PushUpdatedState");
			PushChangedString(text, (uint)cursorBegin, _backgroundState);
			return;
		}
		DeactivateFrontend();
		_backgroundState = InlineKeyboardState.Disappearing;
		if (flag2)
		{
			Logger.Debug?.Print(LogClass.ServiceAm, "Sending keyboard OK with text " + text, "PushUpdatedState");
			DecidedEnter(text, _backgroundState);
		}
		else if (flag)
		{
			Logger.Debug?.Print(LogClass.ServiceAm, "Sending keyboard Cancel", "PushUpdatedState");
			DecidedCancel(_backgroundState);
		}
		_interactiveSession.Push(InlineResponses.Default(_backgroundState));
		Logger.Debug?.Print(LogClass.ServiceAm, $"Resetting state of the keyboard to {_backgroundState}", "PushUpdatedState");
		_backgroundState = InlineKeyboardState.Initialized;
		_interactiveSession.Push(InlineResponses.Default(_backgroundState));
	}

	private void PushChangedString(string text, uint cursor, InlineKeyboardState state)
	{
		if (_encoding == Encoding.UTF8)
		{
			_interactiveSession.Push(InlineResponses.ChangedStringUtf8(text, cursor, state));
		}
		else
		{
			_interactiveSession.Push(InlineResponses.ChangedString(text, cursor, state));
		}
	}

	private void DecidedEnter(string text, InlineKeyboardState state)
	{
		if (_encoding == Encoding.UTF8)
		{
			_interactiveSession.Push(InlineResponses.DecidedEnterUtf8(text, state));
		}
		else
		{
			_interactiveSession.Push(InlineResponses.DecidedEnter(text, state));
		}
	}

	private void DecidedCancel(InlineKeyboardState state)
	{
		_interactiveSession.Push(InlineResponses.DecidedCancel(state));
	}

	private void PushForegroundResponse(bool interactive)
	{
		using MemoryStream memoryStream = new MemoryStream(new byte[interactive ? 2004 : 2008]);
		using BinaryWriter binaryWriter = new BinaryWriter(memoryStream);
		byte[] bytes = _encoding.GetBytes(_textValue);
		if (!interactive)
		{
			binaryWriter.Write((_lastResult != KeyboardResult.Accept) ? 1u : 0u);
		}
		else
		{
			binaryWriter.Write((long)bytes.Length + 8L);
		}
		binaryWriter.Write(bytes);
		if (!interactive)
		{
			_normalSession.Push(memoryStream.ToArray());
		}
		else
		{
			_interactiveSession.Push(memoryStream.ToArray());
		}
	}

	internal static string StripUnicodeControlCodes(string input)
	{
		if (input == null)
		{
			return null;
		}
		if (input.Length == 0)
		{
			return string.Empty;
		}
		StringBuilder stringBuilder = new StringBuilder(input.Length);
		foreach (char c in input)
		{
			if (!char.IsControl(c))
			{
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.ToString();
	}

	private static T ReadStruct<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(byte[] data) where T : struct
	{
		GCHandle gCHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
		try
		{
			return Marshal.PtrToStructure<T>(gCHandle.AddrOfPinnedObject());
		}
		finally
		{
			gCHandle.Free();
		}
	}
}
