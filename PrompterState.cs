namespace CodePrompter;

public sealed record PrompterState(
    bool IsRunning,
    bool IsWildcardMode,
    bool OverlayEnabled,
    bool UseVisualOverlay,
    int CurrentIndex,
    int CodeLength,
    string Status,
    string Preview,
    string OverlayWord,
    string ActiveScriptName,
    string CurrentLine,
    string AvailableScripts,
    string ActiveCode);
