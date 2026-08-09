using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

public sealed class ProfileService
{
    private static readonly ILogger _log = Log.For<ProfileService>();
    private static readonly Lazy<ProfileService> _instance = new(() => new ProfileService());
    public static ProfileService Instance => _instance.Value;

    public const string PvpProfileId = "pvp";
    public const string PveProfileId = "pve";
    public const string SeasonProfileId = "season";
    private const string SettingKey = "app.activeGameMode";

    private AppProfile _activeProfile = AppProfile.PvpZone;
    private bool _isAutoDetected;

    public AppProfile ActiveProfile => _activeProfile;
    public GameMode ActiveGameMode => GetGameMode(_activeProfile);
    public string ActiveProfileId => GetProfileId(_activeProfile);
    public bool IsAutoDetected => _isAutoDetected;

    public event EventHandler<ProfileChangedEventArgs>? ActiveProfileChanged;

    private ProfileService()
    {
        EftRaidEventService.Instance.RaidEvent += OnRaidEvent;
    }

    public async Task InitializeAsync()
    {
        var saved = await UserDataDbService.Instance.GetSettingAsync(SettingKey);
        var profile = ParseStoredProfile(saved);
        _log.Info($"Initialized: {profile}");

        // SettingsService and other singletons may already be constructed (default PvP Zone)
        // before InitializeAsync runs. If the saved mode differs, fire the event so they
        // reload their profile-scoped state.
        if (profile != _activeProfile || _isAutoDetected)
        {
            _activeProfile = profile;
            _isAutoDetected = false;
            ActiveProfileChanged?.Invoke(this, new ProfileChangedEventArgs(profile, false));
        }
    }

    public void SetActiveProfile(AppProfile profile, bool isAuto = false)
    {
        if (!Enum.IsDefined(profile)) return;
        if (_activeProfile == profile && _isAutoDetected == isAuto) return;

        _activeProfile = profile;
        _isAutoDetected = isAuto;

        _ = UserDataDbService.Instance.SetSettingAsync(SettingKey, SerializeProfile(profile));
        _log.Info($"Switched to {profile} (auto={isAuto})");

        ActiveProfileChanged?.Invoke(this, new ProfileChangedEventArgs(profile, isAuto));
    }

    public void ApplyDetectedProfile(SessionProfileHint hint)
    {
        var resolution = ResolveDetectedProfile(_activeProfile, hint);
        if (resolution.DetectionApplied)
            SetActiveProfile(resolution.Profile, isAuto: true);
    }

    public static ProfileResolution ResolveDetectedProfile(AppProfile current, SessionProfileHint detected)
    {
        if (detected == SessionProfileHint.Unknown)
            return new ProfileResolution(current, false);

        var profile = detected switch
        {
            SessionProfileHint.PveZone => AppProfile.PveZone,
            SessionProfileHint.PvpSeason => AppProfile.PvpSeason,
            _ => AppProfile.PvpZone
        };
        return new ProfileResolution(profile, true);
    }

    public static AppProfile ParseStoredProfile(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "PVE" => AppProfile.PveZone,
        "SEASON" => AppProfile.PvpSeason,
        _ => AppProfile.PvpZone
    };

    public static string SerializeProfile(AppProfile profile) => profile switch
    {
        AppProfile.PveZone => "PVE",
        AppProfile.PvpSeason => "SEASON",
        _ => "PVP"
    };

    public static string GetProfileId(AppProfile profile) => profile switch
    {
        AppProfile.PveZone => PveProfileId,
        AppProfile.PvpSeason => SeasonProfileId,
        _ => PvpProfileId
    };

    public static string GetProfileId(GameMode mode) =>
        mode == GameMode.PVE ? PveProfileId : PvpProfileId;

    public static GameMode GetGameMode(AppProfile profile) =>
        profile == AppProfile.PveZone ? GameMode.PVE : GameMode.PVP;

    private void OnRaidEvent(object? sender, EftRaidEventArgs e)
    {
        if (e.EventType != EftRaidEventType.SessionModeDetected) return;
        ApplyDetectedProfile(e.SessionProfileHint);
    }
}

public class ProfileChangedEventArgs : EventArgs
{
    public AppProfile Profile { get; }
    public GameMode GameMode { get; }
    public bool IsAutoDetected { get; }

    public ProfileChangedEventArgs(AppProfile profile, bool isAuto)
    {
        Profile = profile;
        GameMode = ProfileService.GetGameMode(profile);
        IsAutoDetected = isAuto;
    }
}
