using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CodePrompter;

public sealed class CodePrompterService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkCapital = 0x14;
    private const int VkMenu = 0x12;
    private static readonly Regex PlaceholderRegex = new(@"/\*\*(.*?)\*\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly SpeechSynthesizer _synth = new();
    private readonly LowLevelKeyboardProc _proc;
    private readonly object _syncRoot = new();

    private IntPtr _hookId = IntPtr.Zero;
    private bool _disposed;
    private bool _useVisualOverlay;
    private bool _overlayEnabled = true;
    private bool _useRussianSpeech = true;
    private string _currentCode = string.Empty;
    private string _typedText = string.Empty;
    private string _activeScriptName = "TXT-файл не выбран";
    private string _status = "Положи .txt файлы рядом с программой";
    private bool _isWildcardMode;
    private int _currentIndex;
    private int _replaySequenceStage;
    private string _replaySequenceSnapshot = string.Empty;
    private List<ScriptSlot> _scriptSlots = [];
    private List<TemplateSegment> _segments = [];

    public CodePrompterService()
    {
        _proc = HookCallback;
        _synth.SetOutputToDefaultAudioDevice();
        ConfigureSpeechVoice();
        RefreshScripts();
    }

    public event Action<PrompterState>? StateChanged;

    public void RefreshScripts()
    {
        HashSet<string> discoveredFiles = new(StringComparer.OrdinalIgnoreCase);
        string[] probeDirectories =
        [
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        ];

        foreach (string directory in probeDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(directory, "*.txt", SearchOption.TopDirectoryOnly))
            {
                discoveredFiles.Add(file);
            }
        }

        string[] files = [.. discoveredFiles];
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        List<ScriptSlot> slots = [];
        for (int i = 0; i < files.Length && i < 9; i++)
        {
            string content = NormalizeLineEndings(File.ReadAllText(files[i]));
            slots.Add(new ScriptSlot(i + 1, Path.GetFileName(files[i]), files[i], content));
        }

        lock (_syncRoot)
        {
            UpdateOutputModeCore();
            _scriptSlots = slots;

            if (_scriptSlots.Count == 0)
            {
                _currentCode = string.Empty;
                _typedText = string.Empty;
                _segments = [];
                _currentIndex = 0;
                _isWildcardMode = false;
                _activeScriptName = "TXT-файлы не найдены";
                _status = "Положи .txt файлы рядом с exe и нажми обновить";
            }
            else
            {
                ScriptSlot? activeSlot = _scriptSlots.Find(slot => string.Equals(slot.FileName, _activeScriptName, StringComparison.OrdinalIgnoreCase));
                LoadSlotCore(activeSlot ?? _scriptSlots[0], resetTypedText: true, status: "TXT-файлы загружены");
            }
        }

        NotifyStateChanged();
    }

    public void LoadScriptSlot(int slotNumber)
    {
        lock (_syncRoot)
        {
            ScriptSlot? slot = _scriptSlots.Find(item => item.SlotNumber == slotNumber);
            if (slot is null)
            {
                _status = $"Для NumPad {slotNumber} файл не найден";
                NotifyStateChanged();
                return;
            }

            LoadSlotCore(slot, resetTypedText: true, status: $"Загружен {slot.FileName}");
        }

        Speak($"Загружен файл {slotNumber}");
        NarrateCurrentLine();
        NotifyStateChanged();
    }

    public void Start()
    {
        lock (_syncRoot)
        {
            UpdateOutputModeCore();
            if (string.IsNullOrWhiteSpace(_currentCode))
            {
                throw new InvalidOperationException("Нет TXT-файла для озвучки. Положи файл рядом с программой.");
            }

            if (_hookId != IntPtr.Zero)
            {
                _status = "Хук уже запущен";
                NotifyStateChanged();
                return;
            }

            _hookId = SetHook(_proc);
            if (_hookId == IntPtr.Zero)
            {
                throw new InvalidOperationException("Не удалось запустить глобальный хук клавиатуры.");
            }

            _status = _useVisualOverlay
                ? "Суфлер слушает клавиатуру. Включен визуальный оверлей"
                : "Суфлер слушает клавиатуру";
        }

        Speak("Суфлер запущен");
        NarrateCurrentLine();
        NotifyStateChanged();
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            if (_hookId == IntPtr.Zero)
            {
                _status = "Хук уже остановлен";
                NotifyStateChanged();
                return;
            }

            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _status = "Суфлер остановлен";
        }

        _synth.SpeakAsyncCancelAll();
        NotifyStateChanged();
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _typedText = string.Empty;
            _currentIndex = 0;
            _isWildcardMode = false;
            UpdateOutputModeCore();
            _status = "Позиция сброшена";
        }

        Speak("Позиция сброшена");
        NarrateCurrentLine();
        NotifyStateChanged();
    }

    public void SyncFromClipboard()
    {
        StartClipboardSyncThread(0);
    }

    public void SetOverlayEnabled(bool enabled)
    {
        lock (_syncRoot)
        {
            _overlayEnabled = enabled;
            UpdateOutputModeCore();
            _status = enabled ? "Визуальная подсказка включена" : "Визуальная подсказка выключена";
        }

        NotifyStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _synth.Dispose();
        _disposed = true;
    }

    private void LoadSlotCore(ScriptSlot slot, bool resetTypedText, string status)
    {
        UpdateOutputModeCore();
        _activeScriptName = slot.FileName;
        _currentCode = slot.Content;
        _segments = BuildSegments(_currentCode);
        _currentIndex = 0;
        _isWildcardMode = false;
        _replaySequenceStage = 0;
        _replaySequenceSnapshot = string.Empty;
        _status = status;

        if (resetTypedText)
        {
            _typedText = string.Empty;
        }
        else
        {
            ApplyTypedTextCore(_typedText, status);
        }
    }

    private IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule? curModule = curProcess.MainModule;

        if (curModule is null)
        {
            throw new InvalidOperationException("Не удалось получить модуль процесса.");
        }

        return SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || wParam != (IntPtr)WmKeyDown)
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        Keys key = (Keys)Marshal.ReadInt32(lParam);

        if (key >= Keys.NumPad0 && key <= Keys.NumPad9 && Control.IsKeyLocked(Keys.NumLock))
        {
            HandleNumpadBind(key);
            return (IntPtr)1;
        }

        if (key == Keys.Multiply)
        {
            StartClipboardSyncThread(0);
            return (IntPtr)1;
        }

        if (IsClipboardCopyShortcut(key))
        {
            StartClipboardSyncThread(180);
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        bool swallowKey = ProcessCodeInput(key);
        if (swallowKey)
        {
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void HandleNumpadBind(Keys key)
    {
        int slotNumber = key - Keys.NumPad0;
        if (slotNumber == 0)
        {
            RefreshScripts();
            Speak("Список файлов обновлен");
            return;
        }

        if (slotNumber == 9)
        {
            SetOverlayEnabled(!_overlayEnabled);
            Speak(_overlayEnabled ? "Визуальная подсказка включена" : "Визуальная подсказка выключена");
            return;
        }

        LoadScriptSlot(slotNumber);
    }

    private bool ProcessCodeInput(Keys key)
    {
        if (TryReplayCurrentLine(key))
        {
            return true;
        }

        if (TryHandleOverlayNavigation(key))
        {
            return false;
        }

        if (key == Keys.Back)
        {
            lock (_syncRoot)
            {
                if (_typedText.Length == 0)
                {
                    ResetReplaySequence(key);
                    return false;
                }

                _typedText = _typedText[..^1];
                ApplyTypedTextCore(_typedText, $"Удаление: {BuildShortStatus()} ");
            }

            TrackReplaySequence(key);
            NotifyStateChanged();
            return false;
        }

        if (!TryTranslateKey(key, out string? text))
        {
            ResetReplaySequence(key);
            return false;
        }

        lock (_syncRoot)
        {
            _typedText += text;
            ApplyTypedTextCore(_typedText, $"Ввод: {BuildShortStatus()} ");
        }

        TrackReplaySequence(key);

        if (IsNarrationTrigger(text))
        {
            NarrateCurrentLine();
        }

        NotifyStateChanged();
        return false;
    }

    private static bool IsNarrationTrigger(string? text)
    {
        return text is ";" or "\n";
    }

    private bool TryHandleOverlayNavigation(Keys key)
    {
        if (key is not (Keys.Right or Keys.Down or Keys.End))
        {
            return false;
        }

        bool advanced;
        lock (_syncRoot)
        {
            advanced = AdvancePastAutoClosedTailCore();
            if (advanced)
            {
                _status = "Позиция обновлена по навигации";
                ResetReplaySequenceCore();
            }
        }

        if (!advanced)
        {
            return false;
        }

        NotifyStateChanged();
        return true;
    }

    private bool AdvancePastAutoClosedTailCore()
    {
        if (string.IsNullOrEmpty(_currentCode) || _currentIndex >= _currentCode.Length)
        {
            return false;
        }

        int index = _currentIndex;
        bool skippedClosable = false;

        while (index < _currentCode.Length)
        {
            char current = _currentCode[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (IsSkippableAutoCompletedChar(current))
            {
                skippedClosable = true;
                index++;
                continue;
            }

            break;
        }

        if (!skippedClosable || index == _currentIndex)
        {
            return false;
        }

        _currentIndex = index;
        _typedText = BuildTrackedTypedText(_currentIndex);
        _isWildcardMode = false;
        return true;
    }

    private bool TryReplayCurrentLine(Keys key)
    {
        lock (_syncRoot)
        {
            if (_replaySequenceStage == 2 && key == Keys.Space)
            {
                ApplyTypedTextCore(_replaySequenceSnapshot, "Повтор текущей строки");
                _typedText = _replaySequenceSnapshot;
                _status = "Повтор текущей строки";
                _replaySequenceStage = 0;
                _replaySequenceSnapshot = string.Empty;
            }
            else
            {
                return false;
            }
        }

        NarrateCurrentLine();
        NotifyStateChanged();
        return true;
    }

    private void TrackReplaySequence(Keys key)
    {
        lock (_syncRoot)
        {
            switch (_replaySequenceStage)
            {
                case 0 when key == Keys.Space:
                    _replaySequenceSnapshot = _typedText.Length > 0 ? _typedText[..^1] : string.Empty;
                    _replaySequenceStage = 1;
                    break;
                case 1 when key == Keys.Back:
                    _replaySequenceStage = 2;
                    break;
                case 1 when key == Keys.Space:
                    _replaySequenceSnapshot = _typedText.Length > 0 ? _typedText[..^1] : string.Empty;
                    _replaySequenceStage = 1;
                    break;
                default:
                    ResetReplaySequenceCore();
                    break;
            }
        }
    }

    private void ResetReplaySequence(Keys key)
    {
        lock (_syncRoot)
        {
            if (key == Keys.Space)
            {
                _replaySequenceSnapshot = _typedText;
                _replaySequenceStage = 1;
                return;
            }

            ResetReplaySequenceCore();
        }
    }

    private void ResetReplaySequenceCore()
    {
        _replaySequenceStage = 0;
        _replaySequenceSnapshot = string.Empty;
    }

    private Alignment ApplyTypedTextCore(string typedText, string status)
    {
        if (string.IsNullOrEmpty(_currentCode))
        {
            _typedText = string.Empty;
            _currentIndex = 0;
            _isWildcardMode = false;
            _status = "Нет активного TXT-файла";
            return new Alignment(true, false, 0, false);
        }

        string normalizedInput = NormalizeLineEndings(typedText);
        _typedText = normalizedInput;

        Alignment alignment = Align(normalizedInput);
        _currentIndex = alignment.TemplateIndex;
        _isWildcardMode = alignment.InsidePlaceholder;

        if (alignment.IsMismatch)
        {
            _status = $"Обнаружено расхождение. Совпадение до {alignment.TemplateIndex} символа";
        }
        else if (_currentIndex >= _currentCode.Length)
        {
            _status = "Код введен полностью"; 
        }
        else
        {
            _status = status.Trim();
        }

        return alignment;
    }

    private Alignment Align(string typedText)
    {
        Dictionary<(int SegmentIndex, int TypedIndex), Alignment> cache = [];
        Alignment alignment = AlignSegment(0, 0);
        return alignment;

        Alignment AlignSegment(int segmentIndex, int typedIndex)
        {
            if (cache.TryGetValue((segmentIndex, typedIndex), out Alignment cached))
            {
                return cached;
            }

            Alignment result;

            if (segmentIndex >= _segments.Count)
            {
                result = typedIndex >= typedText.Length
                    ? new Alignment(true, false, _currentCode.Length, false)
                    : new Alignment(false, false, _currentCode.Length, true);
                cache[(segmentIndex, typedIndex)] = result;
                return result;
            }

            TemplateSegment segment = _segments[segmentIndex];
            if (!segment.IsPlaceholder)
            {
                LiteralMatchResult literalMatch = MatchLiteralSegment(segment.Text, typedText, typedIndex);

                if (literalMatch.ReachedTypedEnd)
                {
                    result = new Alignment(true, false, segment.StartIndex + literalMatch.TemplateCharsConsumed, false);
                    cache[(segmentIndex, typedIndex)] = result;
                    return result;
                }

                if (literalMatch.IsMismatch)
                {
                    result = new Alignment(false, false, segment.StartIndex + literalMatch.TemplateCharsConsumed, true);
                    cache[(segmentIndex, typedIndex)] = result;
                    return result;
                }

                result = AlignSegment(segmentIndex + 1, typedIndex + literalMatch.TypedCharsConsumed);
                cache[(segmentIndex, typedIndex)] = result;
                return result;
            }

            Alignment best = new(true, true, segment.EndIndex, false);
            for (int split = typedIndex; split <= typedText.Length; split++)
            {
                Alignment candidate = AlignSegment(segmentIndex + 1, split);
                if (IsBetter(candidate, best))
                {
                    best = candidate;
                }
            }

            cache[(segmentIndex, typedIndex)] = best;
            return best;
        }
    }

    private void StartClipboardSyncThread(int delayMilliseconds)
    {
        Thread thread = new(() =>
        {
            if (delayMilliseconds > 0)
            {
                Thread.Sleep(delayMilliseconds);
            }

            try
            {
                string clipboardText = string.Empty;
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        clipboardText = NormalizeLineEndings(System.Windows.Clipboard.GetText());
                    }
                });

                if (string.IsNullOrWhiteSpace(clipboardText))
                {
                    lock (_syncRoot)
                    {
                        _status = "Буфер обмена пуст";
                    }

                    NotifyStateChanged();
                    return;
                }

                lock (_syncRoot)
                {
                    Alignment alignment = ApplyTypedTextCore(clipboardText, "Синхронизировано из буфера обмена");
                    _typedText = BuildTrackedTypedText(alignment.TemplateIndex);
                    ResetReplaySequenceCore();
                }

                Speak("Синхронизация выполнена");
                NarrateCurrentLine();
                NotifyStateChanged();
            }
            catch (Exception)
            {
                lock (_syncRoot)
                {
                    _status = "Не удалось прочитать буфер обмена";
                }

                NotifyStateChanged();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private void NarrateCurrentLine()
    {
        string speechText;
        lock (_syncRoot)
        {
            speechText = BuildSpeechText();
        }

        if (!string.IsNullOrWhiteSpace(speechText))
        {
            Speak(speechText);
        }
    }

    private string BuildSpeechText()
    {
        if (string.IsNullOrWhiteSpace(_currentCode))
        {
            return "Нет загруженного текста";
        }

        if (_currentIndex >= _currentCode.Length)
        {
            return "Код завершен";
        }

        if (_isWildcardMode)
        {
            string nextLiteral = BuildCurrentLinePreview();
            return string.IsNullOrWhiteSpace(nextLiteral)
                ? "Введи свое название"
                : $"Введи свое название, дальше {NormalizeSpeechText(nextLiteral)}";
        }

        string currentLine = BuildCurrentLinePreview();
        if (string.IsNullOrWhiteSpace(currentLine))
        {
            return "Новая строка";
        }

        return $"Строка: {NormalizeSpeechText(currentLine)}";
    }

    private string BuildOverlayText()
    {
        if (_currentIndex >= _currentCode.Length)
        {
            return "Код завершен";
        }

        if (_isWildcardMode)
        {
            return "Введи свое название";
        }

        string currentLine = BuildCurrentLinePreview();
        return string.IsNullOrWhiteSpace(currentLine) ? "Новая строка" : currentLine;
    }

    private string BuildOverlayWord()
    {
        if (string.IsNullOrWhiteSpace(_currentCode))
        {
            return string.Empty;
        }

        if (_currentIndex >= _currentCode.Length)
        {
            return "Готово";
        }

        if (_isWildcardMode)
        {
            return "название";
        }

        int anchor = GetNarrationAnchorIndex();
        while (anchor < _currentCode.Length && char.IsWhiteSpace(_currentCode[anchor]))
        {
            anchor++;
        }

        if (anchor >= _currentCode.Length)
        {
            return string.Empty;
        }

        Match placeholderMatch = PlaceholderRegex.Match(_currentCode, anchor);
        if (placeholderMatch.Success && placeholderMatch.Index == anchor)
        {
            return "название";
        }

        int tokenEnd = anchor;
        while (tokenEnd < _currentCode.Length && !char.IsWhiteSpace(_currentCode[tokenEnd]))
        {
            tokenEnd++;
        }

        string token = _currentCode[anchor..tokenEnd].Trim();
        return string.IsNullOrWhiteSpace(token) ? string.Empty : token;
    }

    private string BuildCurrentLinePreview()
    {
        if (string.IsNullOrWhiteSpace(_currentCode) || _currentIndex >= _currentCode.Length)
        {
            return string.Empty;
        }

        int anchor = GetNarrationAnchorIndex();
        while (anchor < _currentCode.Length && _currentCode[anchor] == '\n')
        {
            anchor++;
        }

        if (anchor >= _currentCode.Length)
        {
            return string.Empty;
        }

        int start = anchor;
        while (start > 0 && _currentCode[start - 1] != '\n')
        {
            start--;
        }

        int end = _currentCode.IndexOf('\n', anchor);
        if (end < 0)
        {
            end = _currentCode.Length;
        }

        string line = _currentCode[start..end].Trim();
        if (line.Length == 0)
        {
            return string.Empty;
        }

        return PlaceholderRegex.Replace(line, "произвольное название");
    }

    private int GetNarrationAnchorIndex()
    {
        if (_currentIndex >= _currentCode.Length)
        {
            return _currentCode.Length;
        }

        int currentLineEnd = _currentCode.IndexOf('\n', _currentIndex);
        if (currentLineEnd < 0)
        {
            currentLineEnd = _currentCode.Length;
        }

        bool lineTailIsWhitespace = true;
        for (int index = _currentIndex; index < currentLineEnd; index++)
        {
            if (!char.IsWhiteSpace(_currentCode[index]))
            {
                lineTailIsWhitespace = false;
                break;
            }
        }

        return lineTailIsWhitespace && currentLineEnd < _currentCode.Length
            ? currentLineEnd + 1
            : _currentIndex;
    }

    private string BuildShortStatus()
    {
        return string.IsNullOrWhiteSpace(_activeScriptName) ? "нет файла" : _activeScriptName;
    }

    private static List<TemplateSegment> BuildSegments(string code)
    {
        List<TemplateSegment> segments = [];
        int currentIndex = 0;

        foreach (Match match in PlaceholderRegex.Matches(code))
        {
            if (match.Index > currentIndex)
            {
                segments.Add(new TemplateSegment(false, currentIndex, match.Index, code[currentIndex..match.Index]));
            }

            segments.Add(new TemplateSegment(true, match.Index, match.Index + match.Length, match.Value));
            currentIndex = match.Index + match.Length;
        }

        if (currentIndex < code.Length)
        {
            segments.Add(new TemplateSegment(false, currentIndex, code.Length, code[currentIndex..]));
        }

        if (segments.Count == 0)
        {
            segments.Add(new TemplateSegment(false, 0, 0, string.Empty));
        }

        return segments;
    }

    private static bool IsBetter(Alignment candidate, Alignment current)
    {
        if (candidate.TemplateIndex != current.TemplateIndex)
        {
            return candidate.TemplateIndex > current.TemplateIndex;
        }

        if (candidate.IsPrefixMatch != current.IsPrefixMatch)
        {
            return candidate.IsPrefixMatch;
        }

        if (candidate.IsMismatch != current.IsMismatch)
        {
            return !candidate.IsMismatch;
        }

        return !candidate.InsidePlaceholder && current.InsidePlaceholder;
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
    }

    private string BuildTrackedTypedText(int templateIndex)
    {
        if (templateIndex <= 0)
        {
            return string.Empty;
        }

        int safeIndex = Math.Min(templateIndex, _currentCode.Length);
        return _currentCode[..safeIndex];
    }

    private static bool CharsEqualIgnoreCase(char left, char right)
    {
        return char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
    }

    private static bool IsIgnorableAutoInsertedChar(string templateText, int templateIndex, char typedChar)
    {
        if (!IsSkippableAutoCompletedChar(typedChar))
        {
            return false;
        }

        for (int index = templateIndex + 1; index < templateText.Length; index++)
        {
            if (CharsEqualIgnoreCase(templateText[index], typedChar))
            {
                return true;
            }

            if (!char.IsWhiteSpace(templateText[index]))
            {
                break;
            }
        }

        return false;
    }

    private static bool CanSkipAutoInsertedTemplateChar(string templateText, int templateIndex, char typedChar)
    {
        if (!IsSkippableAutoCompletedChar(templateText[templateIndex]))
        {
            return false;
        }

        for (int index = templateIndex + 1; index < templateText.Length; index++)
        {
            if (CharsEqualIgnoreCase(templateText[index], typedChar))
            {
                return true;
            }

            if (!char.IsWhiteSpace(templateText[index]) && !IsSkippableAutoCompletedChar(templateText[index]))
            {
                break;
            }
        }

        return false;
    }

    private static bool IsSkippableAutoCompletedChar(char value)
    {
        return value is ')' or ']' or '}' or '"' or '\'' or '`' or ';' or ',' or ':' or '>';
    }

    private static int SkipOptionalTrailingTemplateChars(string templateText, int templateIndex)
    {
        int index = templateIndex;
        while (index < templateText.Length)
        {
            char current = templateText[index];
            if (char.IsWhiteSpace(current) || IsSkippableAutoCompletedChar(current))
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    private static LiteralMatchResult MatchLiteralSegment(string templateText, string typedText, int typedStartIndex)
    {
        int templateIndex = 0;
        int typedIndex = typedStartIndex;

        while (templateIndex < templateText.Length)
        {
            if (char.IsWhiteSpace(templateText[templateIndex]))
            {
                while (templateIndex < templateText.Length && char.IsWhiteSpace(templateText[templateIndex]))
                {
                    templateIndex++;
                }

                while (typedIndex < typedText.Length && char.IsWhiteSpace(typedText[typedIndex]))
                {
                    typedIndex++;
                }

                continue;
            }

            while (typedIndex < typedText.Length && char.IsWhiteSpace(typedText[typedIndex]))
            {
                typedIndex++;
            }

            if (typedIndex >= typedText.Length)
            {
                templateIndex = SkipOptionalTrailingTemplateChars(templateText, templateIndex);
                return new LiteralMatchResult(templateIndex, typedIndex - typedStartIndex, true, false);
            }

            if (IsIgnorableAutoInsertedChar(templateText, templateIndex, typedText[typedIndex]))
            {
                typedIndex++;
                continue;
            }

            if (CanSkipAutoInsertedTemplateChar(templateText, templateIndex, typedText[typedIndex]))
            {
                templateIndex++;
                continue;
            }

            if (!CharsEqualIgnoreCase(typedText[typedIndex], templateText[templateIndex]))
            {
                return new LiteralMatchResult(templateIndex, typedIndex - typedStartIndex, false, true);
            }

            templateIndex++;
            typedIndex++;
        }

        while (typedIndex < typedText.Length && char.IsWhiteSpace(typedText[typedIndex]))
        {
            typedIndex++;
        }

        if (typedIndex >= typedText.Length)
        {
            templateIndex = SkipOptionalTrailingTemplateChars(templateText, templateIndex);
        }

        return new LiteralMatchResult(templateText.Length, typedIndex - typedStartIndex, typedIndex >= typedText.Length, false);
    }

    private string NormalizeSpeechText(string text)
    {
        string normalized = PlaceholderRegex.Replace(text, "произвольное название");
        normalized = Regex.Replace(normalized, @"([a-z])([A-Z])", "$1 $2");
        normalized = Regex.Replace(normalized, @"([A-Za-z])(\d)", "$1 $2");
        normalized = Regex.Replace(normalized, @"(\d)([A-Za-z])", "$1 $2");

        MatchCollection tokens = Regex.Matches(normalized, @"[A-Za-z_][A-Za-z0-9_]*|\d+|===|!==|&&|\|\||==|!=|>=|<=|=>|->|\+\+|--|\+=|-=|\*=|/=|%=|\?\?|\?\.|::|\S");
        List<string> spokenTokens = [];

        foreach (Match tokenMatch in tokens)
        {
            string token = tokenMatch.Value;
            string spoken = GetSpokenToken(token);
            if (!string.IsNullOrWhiteSpace(spoken))
            {
                spokenTokens.Add(spoken);
            }
        }

        return string.Join(", ", spokenTokens);
    }

    private string GetSpokenToken(string token)
    {
        return _useRussianSpeech ? GetRussianSpokenToken(token) : GetEnglishSpokenToken(token);
    }

    private static string GetRussianSpokenToken(string token)
    {
        return token switch
        {
            "public" => "паблик",
            "private" => "прайвет",
            "protected" => "протектед",
            "internal" => "интернал",
            "static" => "статик",
            "void" => "воид",
            "class" => "класс",
            "namespace" => "неймспейс",
            "using" => "юзинг",
            "import" => "импорт",
            "package" => "пакедж",
            "return" => "ретерн",
            "string" => "стринг",
            "bool" => "бул",
            "int" => "инт",
            "new" => "нью",
            "null" => "налл",
            "true" => "тру",
            "false" => "фолс",
            "if" => "иф",
            "else" => "элс",
            "for" => "фор",
            "while" => "вайл",
            "switch" => "свич",
            "case" => "кейс",
            "break" => "брейк",
            "extends" => "экстендс",
            "implements" => "имплементс",
            "override" => "оверрайд",
            "main" => "мэйн",
            "&&" => "логическое и",
            "||" => "логическое или",
            "===" => "строго равно",
            "!==" => "строго не равно",
            "==" => "равно равно",
            "!=" => "не равно",
            ">=" => "больше или равно",
            "<=" => "меньше или равно",
            "=>" => "стрелка",
            "->" => "стрелка",
            "++" => "плюс плюс",
            "--" => "минус минус",
            "+=" => "плюс равно",
            "-=" => "минус равно",
            "*=" => "умножить равно",
            "/=" => "разделить равно",
            "%=" => "остаток равно",
            "??" => "если налл то",
            "?." => "безопасная точка",
            "::" => "двойное двоеточие",
            "{" => "открывающая фигурная скобка",
            "}" => "закрывающая фигурная скобка",
            "(" => "открывающая скобка",
            ")" => "закрывающая скобка",
            "[" => "открывающая квадратная скобка",
            "]" => "закрывающая квадратная скобка",
            ";" => "точка с запятой",
            ":" => "двоеточие",
            "," => "запятая",
            "." => "точка",
            "=" => "равно",
            ">" => "больше",
            "<" => "меньше",
            "+" => "плюс",
            "-" => "минус",
            "*" => "умножить",
            "/" => "слэш",
            "&" => "и",
            "|" => "или",
            "!" => "не",
            "#" => "решетка",
            "@" => "собака",
            "_" => "подчеркивание",
            "\"" => "кавычка",
            "'" => "апостроф",
            _ when Regex.IsMatch(token, @"^\d+$") => string.Join(" ", token.ToCharArray()),
            _ when Regex.IsMatch(token, @"^[A-Za-z_][A-Za-z0-9_]*$") => TransliterateIdentifier(token),
            _ => token
        };
    }

    private static string GetEnglishSpokenToken(string token)
    {
        return token switch
        {
            "public" => "public",
            "private" => "private",
            "protected" => "protected",
            "internal" => "internal",
            "static" => "static",
            "void" => "void",
            "class" => "class",
            "namespace" => "namespace",
            "using" => "using",
            "import" => "import",
            "package" => "package",
            "return" => "return",
            "string" => "string",
            "bool" => "bool",
            "int" => "int",
            "new" => "new",
            "null" => "null",
            "true" => "true",
            "false" => "false",
            "if" => "if",
            "else" => "else",
            "for" => "for",
            "while" => "while",
            "switch" => "switch",
            "case" => "case",
            "break" => "break",
            "extends" => "extends",
            "implements" => "implements",
            "override" => "override",
            "main" => "main",
            "&&" => "logical and",
            "||" => "logical or",
            "===" => "strict equals",
            "!==" => "strict not equals",
            "==" => "equals equals",
            "!=" => "not equals",
            ">=" => "greater or equal",
            "<=" => "less or equal",
            "=>" => "arrow",
            "->" => "arrow",
            "++" => "plus plus",
            "--" => "minus minus",
            "+=" => "plus equals",
            "-=" => "minus equals",
            "*=" => "times equals",
            "/=" => "divide equals",
            "%=" => "mod equals",
            "??" => "null coalescing",
            "?." => "safe dot",
            "::" => "double colon",
            "{" => "open brace",
            "}" => "close brace",
            "(" => "open paren",
            ")" => "close paren",
            "[" => "open bracket",
            "]" => "close bracket",
            ";" => "semicolon",
            ":" => "colon",
            "," => "comma",
            "." => "dot",
            "=" => "equals",
            ">" => "greater",
            "<" => "less",
            "+" => "plus",
            "-" => "minus",
            "*" => "star",
            "/" => "slash",
            "&" => "and",
            "|" => "or",
            "!" => "not",
            "#" => "hash",
            "@" => "at",
            "_" => "underscore",
            "\"" => "quote",
            "'" => "apostrophe",
            _ when Regex.IsMatch(token, @"^\d+$") => string.Join(" ", token.ToCharArray()),
            _ when Regex.IsMatch(token, @"^[A-Za-z_][A-Za-z0-9_]*$") => SplitIdentifierForEnglish(token),
            _ => token
        };
    }

    private static string SplitIdentifierForEnglish(string token)
    {
        string spaced = Regex.Replace(token, @"([a-z])([A-Z])", "$1 $2");
        string[] parts = spaced.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? token : string.Join(" underscore ", parts);
    }

    private static string TransliterateIdentifier(string token)
    {
        List<string> parts = [];
        foreach (string part in Regex.Split(token, @"_+") )
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            parts.Add(TransliterateLatinWord(part));
        }

        return parts.Count == 0 ? token : string.Join(" подчеркивание ", parts);
    }

    private static string TransliterateLatinWord(string word)
    {
        string lower = word.ToLowerInvariant();
        (string Source, string Target)[] replacements =
        [
            ("tion", "шн"),
            ("sion", "жн"),
            ("ight", "айт"),
            ("ph", "ф"),
            ("sh", "ш"),
            ("ch", "ч"),
            ("th", "з"),
            ("ck", "к"),
            ("oo", "у"),
            ("ee", "и"),
            ("ou", "ау"),
            ("ow", "оу"),
            ("qu", "кв")
        ];

        foreach ((string source, string target) in replacements)
        {
            lower = lower.Replace(source, target, StringComparison.Ordinal);
        }

        Dictionary<char, string> map = new()
        {
            ['a'] = "а", ['b'] = "б", ['c'] = "к", ['d'] = "д", ['e'] = "е", ['f'] = "ф",
            ['g'] = "г", ['h'] = "х", ['i'] = "и", ['j'] = "дж", ['k'] = "к", ['l'] = "л",
            ['m'] = "м", ['n'] = "н", ['o'] = "о", ['p'] = "п", ['q'] = "к", ['r'] = "р",
            ['s'] = "с", ['t'] = "т", ['u'] = "у", ['v'] = "в", ['w'] = "в", ['x'] = "кс",
            ['y'] = "й", ['z'] = "з",
            ['0'] = "0", ['1'] = "1", ['2'] = "2", ['3'] = "3", ['4'] = "4",
            ['5'] = "5", ['6'] = "6", ['7'] = "7", ['8'] = "8", ['9'] = "9"
        };

        List<string> result = [];
        foreach (char ch in lower)
        {
            result.Add(map.TryGetValue(ch, out string? value) ? value : ch.ToString());
        }

        return string.Concat(result);
    }

    private void ConfigureSpeechVoice()
    {
        try
        {
            foreach (InstalledVoice voice in _synth.GetInstalledVoices())
            {
                VoiceInfo info = voice.VoiceInfo;
                if (voice.Enabled && string.Equals(info.Culture.Name, "ru-RU", StringComparison.OrdinalIgnoreCase))
                {
                    _synth.SelectVoice(info.Name);
                    _synth.Rate = -1;
                    _synth.Volume = 100;
                    _useRussianSpeech = true;
                    return;
                }
            }

            foreach (InstalledVoice voice in _synth.GetInstalledVoices())
            {
                VoiceInfo info = voice.VoiceInfo;
                if (voice.Enabled && info.Culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase))
                {
                    _synth.SelectVoice(info.Name);
                    _synth.Rate = -1;
                    _synth.Volume = 100;
                    _useRussianSpeech = true;
                    return;
                }
            }

            _synth.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.Adult, 0, CultureInfo.GetCultureInfo("en-US"));
            _synth.Rate = -1;
            _synth.Volume = 100;
            _useRussianSpeech = false;
        }
        catch
        {
            _synth.Rate = -1;
            _synth.Volume = 100;
            _useRussianSpeech = false;
        }
    }

    private void UpdateOutputModeCore()
    {
        _useVisualOverlay = _overlayEnabled && (!HasAudioOutputDevice() || !HasConnectedBluetoothDevice());
    }

    private static bool HasConnectedBluetoothDevice()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth' AND Status = 'OK'");

            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementObject _ in results)
            {
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool HasAudioOutputDevice()
    {
        try
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice? device);
            return device is not null;
        }
        catch
        {
            return false;
        }
    }

    private bool TryTranslateKey(Keys key, out string? text)
    {
        text = key switch
        {
            Keys.Space => " ",
            Keys.Enter => "\n",
            Keys.Tab => "\t",
            _ => null
        };

        if (text is not null)
        {
            return true;
        }

        bool shift = (GetKeyState(VkShift) & 0x8000) != 0;
        bool capsLock = (GetKeyState(VkCapital) & 0x0001) != 0;

        if (key >= Keys.A && key <= Keys.Z)
        {
            char letter = (char)('a' + (key - Keys.A));
            text = (shift ^ capsLock) ? char.ToUpperInvariant(letter).ToString() : letter.ToString();
            return true;
        }

        text = key switch
        {
            Keys.D1 => shift ? "!" : "1",
            Keys.D2 => shift ? "@" : "2",
            Keys.D3 => shift ? "#" : "3",
            Keys.D4 => shift ? "$" : "4",
            Keys.D5 => shift ? "%" : "5",
            Keys.D6 => shift ? "^" : "6",
            Keys.D7 => shift ? "&" : "7",
            Keys.D8 => shift ? "*" : "8",
            Keys.D9 => shift ? "(" : "9",
            Keys.D0 => shift ? ")" : "0",
            Keys.OemMinus => shift ? "_" : "-",
            Keys.Oemplus => shift ? "+" : "=",
            Keys.OemOpenBrackets => shift ? "{" : "[",
            Keys.Oem6 => shift ? "}" : "]",
            Keys.Oem5 => shift ? "|" : "\\",
            Keys.Oem1 => shift ? ":" : ";",
            Keys.Oem7 => shift ? "\"" : "'",
            Keys.Oemcomma => shift ? "<" : ",",
            Keys.OemPeriod => shift ? ">" : ".",
            Keys.OemQuestion => shift ? "?" : "/",
            Keys.Oemtilde => shift ? "~" : "`",
            _ => null
        };

        return text is not null;
    }

    private bool IsClipboardCopyShortcut(Keys key)
    {
        if (key != Keys.C && key != Keys.Insert)
        {
            return false;
        }

        return (GetKeyState(VkControl) & 0x8000) != 0;
    }

    private void Speak(string text)
    {
        _synth.SpeakAsyncCancelAll();
        _synth.SpeakAsync(text);
    }

    private void NotifyStateChanged()
    {
        PrompterState state;
        lock (_syncRoot)
        {
            state = new PrompterState(
                _hookId != IntPtr.Zero,
                _isWildcardMode,
                _overlayEnabled,
                _useVisualOverlay,
                _currentIndex,
                _currentCode.Length,
                _status,
                BuildPreview(),
                BuildOverlayWord(),
                _activeScriptName,
                BuildCurrentLinePreview(),
                BuildScriptSummary(),
                _currentCode);
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => StateChanged?.Invoke(state));
    }

    private string BuildPreview()
    {
        if (string.IsNullOrEmpty(_currentCode) || _currentIndex >= _currentCode.Length)
        {
            return string.Empty;
        }

        int length = Math.Min(60, _currentCode.Length - _currentIndex);
        return _currentCode.Substring(_currentIndex, length)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " | ", StringComparison.Ordinal);
    }

    private string BuildScriptSummary()
    {
        if (_scriptSlots.Count == 0)
        {
            return "Нет TXT-файлов";
        }

        StringBuilder builder = new();
        foreach (ScriptSlot slot in _scriptSlots)
        {
            string marker = string.Equals(slot.FileName, _activeScriptName, StringComparison.OrdinalIgnoreCase) ? ">" : " ";
            builder.Append(marker)
                .Append(" NumPad")
                .Append(slot.SlotNumber)
                .Append(" - ")
                .AppendLine(slot.FileName);
        }

        return builder.ToString().TrimEnd();
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly record struct TemplateSegment(bool IsPlaceholder, int StartIndex, int EndIndex, string Text);

    private readonly record struct Alignment(bool IsPrefixMatch, bool InsidePlaceholder, int TemplateIndex, bool IsMismatch);

    private readonly record struct LiteralMatchResult(int TemplateCharsConsumed, int TypedCharsConsumed, bool ReachedTypedEnd, bool IsMismatch);

    private enum EDataFlow
    {
        eRender,
        eCapture,
        eAll,
        EDataFlowEnumCount
    }

    private enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications,
        ERoleEnumCount
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
    }
}
