using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Applets.SoftwareKeyboard;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.UI;

namespace NxInfinity.Core;

internal sealed class Interactions(Protocol protocol, CancellationToken cancellation, Action<Action> dispatch, Action<ProgramSpecifyKind, ulong> switchProgram, Action captureInput) : IHostUIHandler
{
    private sealed class HostTheme : IHostUITheme
    {
        public string FontFamily => "sans-serif";

        public ThemeColor DefaultBackgroundColor => new ThemeColor(1f, 0.08f, 0.08f, 0.1f);

        public ThemeColor DefaultForegroundColor => new ThemeColor(1f, 0.95f, 0.95f, 0.95f);

        public ThemeColor DefaultBorderColor => new ThemeColor(1f, 0.3f, 0.3f, 0.35f);

        public ThemeColor SelectionBackgroundColor => new ThemeColor(1f, 0.2f, 0.3f, 0.8f);

        public ThemeColor SelectionForegroundColor => new ThemeColor(1f, 1f, 1f, 1f);
    }

    private sealed class DynamicText(string id, Protocol protocol, Action onDispose, Action captureInput) : IDynamicTextInputHandler, IDisposable
    {
        private volatile bool _disposed;

        private long _generation;

        private readonly object _sync = new object();

        private volatile bool _textProcessingEnabled;

        private string _text = "";

        private int _cursorBegin;

        private int _cursorEnd;

        public long Generation => Interlocked.Read(in _generation);

        public bool TextProcessingEnabled
        {
            get
            {
                return _textProcessingEnabled;
            }
            set
            {
                lock (_sync)
                {
                    if (!_disposed && _textProcessingEnabled != value)
                    {
                        Interlocked.Increment(ref _generation);
                        _textProcessingEnabled = value;
                        if (value)
                        {
                            PublishText();
                        }
                        else
                        {
                            protocol.Event("interaction.closed", new
                            {
                                interactionId = id
                            });
                        }
                    }
                }
            }
        }

        public event DynamicTextChangedHandler TextChangedEvent;

        public event KeyPressedHandler KeyPressedEvent;

        public event KeyReleasedHandler KeyReleasedEvent;

        public void SetText(string text, int cursorBegin)
        {
            SetText(text, cursorBegin, cursorBegin);
        }

        public void SetText(string text, int cursorBegin, int cursorEnd)
        {
            lock (_sync)
            {
                _text = text ?? "";
                if (_text.Length > 4096)
                {
                    _text = _text.Substring(0, 4096);
                }
                _cursorBegin = Math.Clamp(cursorBegin, 0, _text.Length);
                _cursorEnd = Math.Clamp(cursorEnd, 0, _text.Length);
                if (TextProcessingEnabled)
                {
                    PublishText();
                }
            }
        }

        private void PublishText()
        {
            lock (_sync)
            {
                if (!_disposed && TextProcessingEnabled)
                {
                    captureInput();
                    protocol.Event("interaction.request", new
                    {
                        interactionId = id,
                        kind = "dynamic_text",
                        details = new
                        {
                            text = _text,
                            cursorBegin = _cursorBegin,
                            cursorEnd = _cursorEnd,
                            generation = Generation,
                            maximumLength = 100
                        }
                    });
                }
            }
        }

        public void Deliver(string text, int begin, int end, string key, long generation)
        {
            lock (_sync)
            {
                if (_disposed || !TextProcessingEnabled || generation != Generation)
                {
                    return;
                }
                _text = text;
                _cursorBegin = begin;
                _cursorEnd = end;
            }
            TextChangedEvent?.Invoke(text, begin, end, overwriteMode: false);
            if (key.Length > 0)
            {
                Key key2;
                if (key == "enter")
                {
                    key2 = Key.Enter;
                }
                else
                {
                    key2 = ((!(key == "escape")) ? Key.BackSpace : Key.Escape);
                }
                Key key3 = key2;
                KeyPressedEvent?.Invoke(key3);
                KeyReleasedEvent?.Invoke(key3);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                onDispose();
                protocol.Event("interaction.closed", new
                {
                    interactionId = id
                });
            }
        }
    }

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>();

    private readonly ConcurrentDictionary<string, DynamicText> _dynamic = new ConcurrentDictionary<string, DynamicText>();

    public bool InputCaptureActive
    {
        get
        {
            if (_pending.IsEmpty)
            {
                return _dynamic.Values.Any((DynamicText handler) => handler.TextProcessingEnabled);
            }
            return true;
        }
    }

    public IHostUITheme HostUITheme { get; } = new HostTheme();

    private JsonElement Ask(string kind, object details)
    {
        string text = Guid.NewGuid().ToString("N");
        TaskCompletionSource<JsonElement> taskCompletionSource = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[text] = taskCompletionSource;
        try
        {
            captureInput();
            protocol.Event("interaction.request", new
            {
                interactionId = text,
                kind = kind,
                details = details,
                timeoutSeconds = 0
            });
            return taskCompletionSource.Task.WaitAsync(cancellation).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            protocol.Event("interaction.canceled", new
            {
                interactionId = text,
                reason = "session_stopped"
            });
            return JsonSerializer.SerializeToElement(new
            {
                accepted = false
            });
        }
        finally
        {
            _pending.TryRemove(text, out var _);
            protocol.Event("interaction.closed", new
            {
                interactionId = text
            });
        }
    }

    public object Respond(JsonElement p)
    {
        string text = Fields.RequiredString(p, "interactionId");
        string text2;
        int begin;
        int end;
        string key;
        bool flag;
        if (_dynamic.TryGetValue(text, out var handler))
        {
            text2 = Fields.String(p, "text", "");
            if (text2.Length > 4096)
            {
                throw new NxError("invalid_argument", "Dynamic text is limited to 4096 characters.");
            }
            begin = Fields.Integer(p, "cursorBegin", text2.Length, 0, text2.Length);
            end = Fields.Integer(p, "cursorEnd", begin, 0, text2.Length);
            key = Fields.String(p, "key", "");
            string text3 = key;
            if (text3 == null || text3.Length != 0)
            {
                switch (text3)
                {
                    case "enter":
                    case "escape":
                    case "backspace":
                        break;
                    default:
                        goto IL_0106;
                }
            }
            flag = true;
            goto IL_0109;
        }
        if (!_pending.TryGetValue(text, out var value))
        {
            throw new NxError("invalid_argument", "Unknown or expired interaction ID.");
        }
        Fields.Boolean(p, "accepted", fallback: false);
        if (p.TryGetProperty("text", out var value2) && (value2.ValueKind != JsonValueKind.String || value2.GetString().Length > 4096))
        {
            throw new NxError("invalid_argument", "Interaction text must be a string of at most 4096 characters.");
        }
        if (!value.TrySetResult(p.Clone()))
        {
            throw new NxError("invalid_state", "Interaction already has a response.");
        }
        return new
        {
            accepted = true,
            interactionId = text
        };
    IL_0106:
        flag = false;
        goto IL_0109;
    IL_0109:
        if (!flag)
        {
            throw new NxError("invalid_argument", "Unsupported dynamic text key.");
        }
        long generation = handler.Generation;
        if (!handler.TextProcessingEnabled || (p.TryGetProperty("generation", out var value3) && (!value3.TryGetInt64(out var value4) || value4 != generation)))
        {
            throw new NxError("invalid_state", "This keyboard activation has expired.");
        }
        dispatch(() =>
        {
            handler.Deliver(text2, begin, end, key, generation);
        });
        return new
        {
            accepted = true,
            scheduled = true,
            interactionId = text
        };
    }

    public void CancelAll()
    {
        foreach (DynamicText value in _dynamic.Values)
        {
            value.Dispose();
        }
        foreach (TaskCompletionSource<JsonElement> value2 in _pending.Values)
        {
            value2.TrySetCanceled();
        }
    }

    public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
    {
        JsonElement p = Ask("text", new
        {
            title = args.HeaderText,
            subtitle = args.SubtitleText,
            initialText = args.InitialText,
            guide = args.GuideText,
            minimumLength = args.StringLengthMin,
            maximumLength = args.StringLengthMax,
            submitText = args.SubmitText,
            keyboardMode = args.KeyboardMode.ToString(),
            disabledCharacters = args.DisabledCharacters,
            extraSymbols = args.ExtraSymbols,
            password = args.Password,
            cursorBegin = args.CursorPosition,
            cursorEnd = args.CursorPosition
        });
        userText = Fields.String(p, "text", "");
        if (Fields.Boolean(p, "accepted", fallback: false))
        {
            return ValidText(userText, args);
        }
        return false;
    }

    private static bool ValidText(string text, SoftwareKeyboardUIArgs args)
    {
        if (text.Length < args.StringLengthMin || text.Length > args.StringLengthMax)
        {
            return false;
        }
        uint disabledCharacters = args.DisabledCharacters;
        foreach (char c in text)
        {
            if (args.KeyboardMode == KeyboardMode.Numeric && (c < '0' || c > '9') && !(args.ExtraSymbols ?? "").Contains(c))
            {
                return false;
            }
            if (args.KeyboardMode == KeyboardMode.ASCII && c > '\u007f')
            {
                return false;
            }
            if (((disabledCharacters & 2) != 0 && c == ' ') || ((disabledCharacters & 0x104) != 0 && c == '@') || ((disabledCharacters & 0x108) != 0 && c == '%') || ((disabledCharacters & 0x10) != 0 && c == '/') || ((disabledCharacters & 0x120) != 0 && c == '\\') || ((disabledCharacters & 0x40) != 0 && c >= '0' && c <= '9'))
            {
                return false;
            }
        }
        return true;
    }

    public bool DisplayMessageDialog(string title, string message)
    {
        return Fields.Boolean(Ask("message", new { title, message }), "accepted", fallback: false);
    }

    public bool DisplayMessageDialog(ControllerAppletUIArgs args)
    {
        return Fields.Boolean(Ask("controller", new
        {
            minimumPlayers = args.PlayerCountMin,
            maximumPlayers = args.PlayerCountMax,
            styles = args.SupportedStyles.ToString(),
            docked = args.IsDocked
        }), "accepted", fallback: false);
    }

    public bool DisplayCabinetDialog(out string userText)
    {
        JsonElement p = Ask("amiibo_name", new
        {
            title = "Amiibo name",
            maximumLength = 10
        });
        userText = Fields.String(p, "text", "");
        if (Fields.Boolean(p, "accepted", fallback: false))
        {
            return userText.Length <= 10;
        }
        return false;
    }

    public void DisplayCabinetMessageDialog()
    {
        Ask("message", new
        {
            title = "Amiibo scan",
            message = "The game requests an Amiibo scan. Amiibo injection is not integrated by this adapter."
        });
    }

    public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText)
    {
        return Fields.Integer(Ask("error", new
        {
            title = title,
            message = message,
            buttons = buttonsText
        }), "buttonIndex", 0, 0, Math.Max(0, buttonsText.Length - 1)) > 0;
    }

    public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value)
    {
        dispatch(() =>
        {
            switchProgram(kind, value);
        });
    }

    public IDynamicTextInputHandler CreateDynamicTextInputHandler()
    {
        string id = Guid.NewGuid().ToString("N");
        DynamicText dynamicText = new DynamicText(id, protocol, () =>
        {
            _dynamic.TryRemove(id, out var _);
        }, captureInput);
        _dynamic[id] = dynamicText;
        return dynamicText;
    }
}
