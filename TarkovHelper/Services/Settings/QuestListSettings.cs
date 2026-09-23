using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services.Settings;

/// <summary>
/// Quest-tab UI state persisted across app restarts (pattern: <see cref="MapSettings"/>):
/// the filter bar (Kappa/Item-Req checkboxes, trader, map, status) and the detail-panel
/// width. Search text is deliberately NOT persisted — it is a transient query, and
/// restoring it would surprise more than help (see feature-quest-overview-filters.md).
///
/// First access must happen after UserDataDbService is initialized (the page touches
/// this from Loaded, never from a constructor — see the init-order note on
/// QuestListPage.RestoreFilterSettings).
/// </summary>
public class QuestListSettings
{
    private static readonly ILogger _log = Log.For<QuestListSettings>();
    private static QuestListSettings? _instance;
    public static QuestListSettings Instance => _instance ??= new QuestListSettings();

    private readonly UserDataDbService _userDataDb = UserDataDbService.Instance;
    private bool _settingsLoaded;

    #region Setting Keys

    private const string KeyKappaOnly = "questList.kappaOnly";
    private const string KeyItemRequired = "questList.itemRequired";
    private const string KeyTrader = "questList.trader";
    private const string KeyMap = "questList.map";
    private const string KeyStatusTag = "questList.statusTag";
    private const string KeyDetailPanelWidth = "questList.detailPanelWidth";

    #endregion

    #region Constants

    /// <summary>Matches the XAML default of the detail column (QuestListPage.xaml, DetailColumn).</summary>
    public const double DefaultDetailPanelWidth = 350;
    public const double MinDetailPanelWidth = 250;
    public const double MaxDetailPanelWidth = 800;

    /// <summary>The page's fresh-install status filter ("Active" — QuestListPage._statusTag's initial value).</summary>
    public const string DefaultStatusTag = "Active";

    #endregion

    #region Cached Values

    private bool? _kappaOnly;
    private bool? _itemRequired;
    private string? _trader;
    private string? _map;
    private string? _statusTag;
    private double? _detailPanelWidth;

    #endregion

    private QuestListSettings()
    {
        LoadSettings();
    }

    public bool KappaOnly
    {
        get
        {
            EnsureLoaded();
            return _kappaOnly ?? false;
        }
        set
        {
            // EnsureLoaded before the change check: an unloaded cache is null, and
            // `null != value` is always true, so an unguarded setter would write the
            // session default over a stored value it never read. Same in every setter.
            EnsureLoaded();
            if (_kappaOnly != value)
            {
                _kappaOnly = value;
                SaveSetting(KeyKappaOnly, value.ToString());
            }
        }
    }

    public bool ItemRequired
    {
        get
        {
            EnsureLoaded();
            return _itemRequired ?? false;
        }
        set
        {
            EnsureLoaded();
            if (_itemRequired != value)
            {
                _itemRequired = value;
                SaveSetting(KeyItemRequired, value.ToString());
            }
        }
    }

    /// <summary>The trader combo's Tag value; empty string = "All Traders".</summary>
    public string Trader
    {
        get
        {
            EnsureLoaded();
            return _trader ?? "";
        }
        set
        {
            EnsureLoaded();
            if (_trader != value)
            {
                _trader = value;
                SaveSetting(KeyTrader, value ?? "");
            }
        }
    }

    /// <summary>The map combo's Tag value (normalized map name); empty string = "All Maps".</summary>
    public string Map
    {
        get
        {
            EnsureLoaded();
            return _map ?? "";
        }
        set
        {
            EnsureLoaded();
            if (_map != value)
            {
                _map = value;
                SaveSetting(KeyMap, value ?? "");
            }
        }
    }

    /// <summary>The status filter's chip tag ("All", "Active", "Locked", "Done", "Failed", "Unavailable").</summary>
    public string StatusTag
    {
        get
        {
            EnsureLoaded();
            return string.IsNullOrEmpty(_statusTag) ? DefaultStatusTag : _statusTag;
        }
        set
        {
            EnsureLoaded();
            if (_statusTag != value)
            {
                _statusTag = value;
                SaveSetting(KeyStatusTag, value ?? DefaultStatusTag);
            }
        }
    }

    public double DetailPanelWidth
    {
        get
        {
            EnsureLoaded();
            return _detailPanelWidth ?? DefaultDetailPanelWidth;
        }
        set
        {
            EnsureLoaded();
            var clampedValue = Math.Clamp(value, MinDetailPanelWidth, MaxDetailPanelWidth);
            if (Math.Abs((_detailPanelWidth ?? DefaultDetailPanelWidth) - clampedValue) > 1)
            {
                _detailPanelWidth = clampedValue;
                SaveSetting(KeyDetailPanelWidth, SettingsValue.FormatDouble(clampedValue));
            }
        }
    }

    /// <summary>
    /// Persists the whole filter-bar snapshot in ONE connection/transaction (the
    /// <see cref="UserDataDbService.SetSettings"/> batch path <c>MapSettings.SaveLastView</c>
    /// uses): these five values change together — a reset changes all of them — and a
    /// failure partway through five separate writes would restore a filter combination
    /// the user never had. Unchanged values are omitted, so the common case writes nothing.
    /// </summary>
    public void SaveFilterSnapshot(
        bool kappaOnly, bool itemRequired, string trader, string map, string statusTag)
    {
        EnsureLoaded();

        var changes = new List<KeyValuePair<string, string>>();

        if (_kappaOnly != kappaOnly)
            changes.Add(new(KeyKappaOnly, kappaOnly.ToString()));
        if (_itemRequired != itemRequired)
            changes.Add(new(KeyItemRequired, itemRequired.ToString()));
        if (_trader != trader)
            changes.Add(new(KeyTrader, trader ?? ""));
        if (_map != map)
            changes.Add(new(KeyMap, map ?? ""));
        // StatusTag's getter substitutes DefaultStatusTag for an empty cache, so compare
        // against the same substituted value or an empty stored tag would look "changed".
        if (StatusTag != statusTag)
            changes.Add(new(KeyStatusTag, statusTag ?? DefaultStatusTag));

        if (changes.Count == 0) return;

        try
        {
            _userDataDb.SetSettings(changes);
        }
        catch (Exception ex)
        {
            // Cache deliberately NOT updated: leaving it at the stored values means the
            // next ApplyFilters still sees a difference and retries the write, instead of
            // a cache that reports success for the rest of the session while the store
            // silently keeps the old filters.
            _log.Error($"Filter snapshot save failed ({changes.Count} keys): {ex.Message}");
            return;
        }

        _kappaOnly = kappaOnly;
        _itemRequired = itemRequired;
        _trader = trader;
        _map = map;
        _statusTag = statusTag;
    }

    #region Private Methods

    private void EnsureLoaded()
    {
        if (!_settingsLoaded) LoadSettings();
    }

    private void SaveSetting(string key, string value)
    {
        try
        {
            _userDataDb.SetSetting(key, value);
        }
        catch (Exception ex)
        {
            _log.Error($"Save failed: key={key}, error={ex.Message}");
        }
    }

    /// <summary>
    /// Reads every persisted value into the cache. The loaded flag is set only AFTER a
    /// successful read: a throwing read (locked user_data.db, DB not initialized yet)
    /// must leave the cache retryable, because a latched "loaded" flag over an empty
    /// cache makes every getter return defaults for the rest of the session — and the
    /// next save would then write those defaults over the user's stored values.
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            if (bool.TryParse(_userDataDb.GetSetting(KeyKappaOnly), out var kappaOnly))
                _kappaOnly = kappaOnly;

            if (bool.TryParse(_userDataDb.GetSetting(KeyItemRequired), out var itemRequired))
                _itemRequired = itemRequired;

            _trader = _userDataDb.GetSetting(KeyTrader);
            _map = _userDataDb.GetSetting(KeyMap);
            _statusTag = _userDataDb.GetSetting(KeyStatusTag);

            if (SettingsValue.TryParseDouble(_userDataDb.GetSetting(KeyDetailPanelWidth), out var width))
                _detailPanelWidth = Math.Clamp(width, MinDetailPanelWidth, MaxDetailPanelWidth);

            _settingsLoaded = true;
        }
        catch (Exception ex)
        {
            _log.Error($"Load failed: {ex.Message}");
        }
    }

    #endregion
}
