using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using TarkovHelper.Pages;
using TarkovHelper.Services;
using QuestNormalizedName = TarkovDBEditor.Services.QuestNormalizedName;

namespace TarkovHelper.Tests;

/// <summary>
/// Shared harness for end-to-end tests that drive the real app (extracted from
/// MainWindowBoundsE2ETests for reuse; see also MapStateE2ETests): launches
/// <c>dotnet TarkovHelper.dll</c> (running the DLL bypasses the requireAdministrator
/// apphost manifest), points it at a throwaway Config folder via the
/// TARKOVHELPER_CONFIG_PATH environment variable, tracks the main window, and exposes
/// Win32 window control plus UI Automation for in-window controls (WPF surfaces
/// x:Name as the UIA AutomationId).
///
/// E2E tests need an interactive desktop and take a few seconds per launch; exclude
/// them from quick runs with <c>dotnet test --filter Category!=E2E</c>. They skip
/// automatically when the app build output is missing.
///
/// Coordinates: the test host's DPI awareness is forced to per-monitor-v2 up front
/// (see <see cref="TestHostDpiAwareness"/>) so GetWindowRect deterministically returns
/// physical pixels, and <see cref="GetWindowRect"/> normalizes them by the window's DPI
/// back to WPF device-independent units (verified on a 200% display). Without the
/// forcing, awareness silently depended on whether UI Automation had been touched
/// first, flipping the coordinate space between runs.
/// </summary>
internal sealed class AppDriver : IDisposable
{
    private readonly Process _process;
    private readonly IntPtr _hwnd;
    // Root UIA element for the main window, resolved once, so every TryFindElement poll
    // reuses it instead of re-entering COM via FromHandle each 250ms tick.
    private readonly AutomationElement _uiaRoot;

    private AppDriver(Process process, IntPtr hwnd)
    {
        _process = process;
        _hwnd = hwnd;
        _uiaRoot = AutomationElement.FromHandle(hwnd);
    }

    public static AppDriver Launch(string configDir) => Launch(AppUnderTest.DllPath!, configDir);

    /// <summary>
    /// Launches a specific build rather than the one this test run produced.
    /// <para>
    /// Used by the legacy smoke, which drives the release already installed in the field
    /// against a candidate database. That build honours TARKOVHELPER_CONFIG_PATH and no other
    /// harness variable; the rest are set anyway because they are inert to a build that does
    /// not read them.
    /// </para>
    /// <para>
    /// Inert specifically includes TARKOVHELPER_DISABLE_DB_UPDATE: the commit that taught
    /// DatabaseUpdateService to read it postdates tag v2026.7.0, so setting it here does not
    /// stop an older build from downloading the published database over the one the caller
    /// staged. A caller launching a pre-tag build has to neutralise that check itself and
    /// prove afterwards that it held (see LegacySmokeE2ETests).
    /// </para>
    /// </summary>
    public static AppDriver Launch(string dllPath, string configDir)
    {
        var dll = dllPath;
        var appDir = Path.GetDirectoryName(dll)!;
        RemoveLegacyLanguageOverride(appDir);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = appDir,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.Environment["TARKOVHELPER_CONFIG_PATH"] = configDir;
        // Without this the app's immediate background check can download a newer
        // tarkov_data.db over the build-output Assets copy mid-test, silently
        // diverging from the static copy the tests derive their expectations from.
        psi.Environment["TARKOVHELPER_DISABLE_DB_UPDATE"] = "1";
        // Without this a Debug-build app opens the Topmost Debug Toolbox at the OS
        // cascade position (upper-left, drifting per launch), which steals focus on
        // Show() and intermittently obscures the quest list / recommendations area.
        // GetClickablePoint then throws on the obscured rows and synthetic clicks
        // land on the toolbox instead of the intended element.
        psi.Environment["TARKOVHELPER_DISABLE_DEBUG_TOOLBOX"] = "1";
        // Without this the app fetches update.xml over the real network on launch; a
        // published version newer than the built one flips the header's version chip
        // (BtnVersionChip/ChipVersion) mid-test, making HeaderE2ETests' chip-state
        // assertions depend on the release cadence instead of the build under test.
        psi.Environment["TARKOVHELPER_DISABLE_UPDATE_CHECK"] = "1";

        var process = Process.Start(psi)!;
        try
        {
            return new AppDriver(process, WaitForMainWindow(process));
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Waits for the process's "Tarkov Helper" top-level window. Matched by exact
    /// title because the app opens other top-level windows that make
    /// Process.MainWindowHandle ambiguous: owned dialogs such as
    /// QuestCompleteConfirmDialog, and outside the harness the Debug Toolbox
    /// (which Launch disables via TARKOVHELPER_DISABLE_DEBUG_TOOLBOX).
    /// </summary>
    private static IntPtr WaitForMainWindow(Process process)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"app exited during startup (exit code {process.ExitCode})");

            var hwnd = Win32.FindTopLevelWindow(process.Id, "Tarkov Helper");
            if (hwnd != IntPtr.Zero) return hwnd;

            Thread.Sleep(250);
        }
        throw new TimeoutException("main window did not appear within 60s");
    }

    /// <summary>
    /// Deletes a leftover legacy Data\settings.json next to the app under test.
    /// TARKOVHELPER_CONFIG_PATH isolates user_data.db but NOT that file: it sits under the
    /// app base directory, and LocalizationService.LoadSettings runs MigrateFromJsonIfNeeded
    /// (which writes app.language into the isolated db) BEFORE reading the language back,
    /// so seeding the db cannot pin it. A stale file from a pre-DB build would flip the app
    /// under test to KO/JA and break every assertion on rendered text (quest names, the
    /// cascade dialog's window title and headers). The app deletes it on first launch
    /// anyway, so removing it here loses nothing.
    /// </summary>
    internal static void RemoveLegacyLanguageOverride(string appDir)
    {
        var legacy = Path.Combine(appDir, "Data", "settings.json");
        if (File.Exists(legacy)) File.Delete(legacy);
    }

    #region Win32 window control

    /// <summary>
    /// The window rect in WPF device-independent units: raw physical pixels scaled by
    /// the window's own DPI (the same DPI WPF used to place it), so assertions hold on
    /// scaled displays.
    /// </summary>
    public Win32.WindowRect GetWindowRect()
    {
        Win32.GetWindowRect(_hwnd, out var rect);
        return new Win32.WindowRect(rect, 96.0 / Win32.GetDpiForWindow(_hwnd));
    }

    /// <summary>Off-screen check in raw physical units (same space as GetSystemMetrics).</summary>
    public bool IsWithinVirtualScreen()
    {
        Win32.GetWindowRect(_hwnd, out var rect);
        return Win32.IsWithinVirtualScreen(new Win32.WindowRect(rect, scale: 1.0));
    }

    /// <summary>SW_SHOWNORMAL / SW_SHOWMINIMIZED / SW_SHOWMAXIMIZED of the live window.</summary>
    public int GetShowCmd()
    {
        var placement = Win32.WINDOWPLACEMENT.Create();
        Win32.GetWindowPlacement(_hwnd, ref placement);
        return placement.showCmd;
    }

    public void ShowWindow(int cmd)
    {
        Win32.ShowWindow(_hwnd, cmd);
        Thread.Sleep(300); // let WPF finish the state change before we act on it
    }

    /// <summary>Resizes the normal window using WPF device-independent units.</summary>
    public void ResizeWindow(double width, double height)
    {
        Win32.ShowWindow(_hwnd, Win32.SW_RESTORE);
        Win32.GetWindowRect(_hwnd, out var rect);
        var scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        Assert.True(Win32.MoveWindow(
            _hwnd,
            rect.Left,
            rect.Top,
            (int)Math.Round(width * scale),
            (int)Math.Round(height * scale),
            repaint: true));
        Thread.Sleep(300);
    }

    /// <summary>Graceful close (WM_CLOSE, so the Closing event saves) and wait for exit.</summary>
    public void CloseAndWaitForExit()
    {
        Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Assert.True(_process.WaitForExit(20_000), "app did not exit within 20s of WM_CLOSE");
    }

    #endregion

    #region UI Automation

    /// <summary>
    /// Selects a main-window tab (a named RadioButton, e.g. "TabMap") and waits until
    /// <paramref name="readyElementAutomationId"/>, an element unique to the switched-in
    /// page, appears. A click that lands during the app's startup loading window is
    /// swallowed by MainWindow's _isLoading guard while still checking the radio button
    /// (so re-selecting it would no-op); the retry bounces through another tab to
    /// re-fire the Checked event once loading has finished.
    /// </summary>
    public void SelectTab(string tabAutomationId, string readyElementAutomationId,
        string bounceTabAutomationId = "TabItems")
    {
        if (string.Equals(tabAutomationId, bounceTabAutomationId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"bounce tab must differ from the target tab '{tabAutomationId}': bouncing to itself " +
                "just re-selects the checked radio button (a no-op) and would spin to the timeout",
                nameof(bounceTabAutomationId));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        Select(WaitForElement(tabAutomationId, deadline));

        while (TryFindElement(readyElementAutomationId) == null)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"page for tab '{tabAutomationId}' did not appear within 60s");

            Thread.Sleep(250);
            Select(WaitForElement(bounceTabAutomationId, deadline));
            Thread.Sleep(250);
            Select(WaitForElement(tabAutomationId, deadline));
        }
    }

    /// <summary>
    /// Polls the combo box until its UIA selection is non-empty and returns the selected
    /// item's Name (for explicit ComboBoxItem items, their Content text).
    /// </summary>
    public string WaitForComboSelection(string comboAutomationId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var combo = WaitForElement(comboAutomationId, deadline);

        string? name = null;
        PollUntil(() =>
        {
            var selection = ((SelectionPattern)combo.GetCurrentPattern(SelectionPattern.Pattern))
                .Current.GetSelection();
            if (selection.Length > 0)
            {
                var candidate = selection[0].Current.Name;
                if (!string.IsNullOrEmpty(candidate))
                {
                    name = candidate;
                    return true;
                }
            }
            return false;
        }, deadline, () => $"combo '{comboAutomationId}' did not report a selection within 30s");
        return name!;
    }

    /// <summary>Waits until the element exists in the UIA tree and returns it.</summary>
    public AutomationElement WaitForElement(string automationId, int timeoutSeconds = 30)
        => WaitForElement(automationId, DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds));

    /// <summary>
    /// Whether the element is currently in the UIA tree and on screen. Collapsed WPF
    /// elements either drop out of the automation tree or report IsOffscreen depending
    /// on the framework's peer behavior, so this covers both.
    /// </summary>
    public bool IsElementVisible(string automationId)
    {
        var element = TryFindElement(automationId);
        return element != null && !element.Current.IsOffscreen;
    }

    /// <summary>Polls until <see cref="IsElementVisible"/> reports the expected state.</summary>
    public void WaitForElementVisibility(string automationId, bool visible, int timeoutSeconds = 30)
        => PollUntil(
            () => IsElementVisible(automationId) == visible,
            DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds),
            () => $"element '{automationId}' did not become {(visible ? "visible" : "hidden")} within {timeoutSeconds}s");

    /// <summary>Invokes a button-like element by AutomationId (InvokePattern).</summary>
    public void InvokeElement(string automationId)
    {
        var element = WaitForElement(automationId);
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    /// <summary>The element's UIA Name (for a TextBlock, its rendered text).</summary>
    public string GetElementText(string automationId)
        => WaitForElement(automationId).Current.Name;

    /// <summary>
    /// The element's UIA ItemStatus (what the app publishes via
    /// AutomationProperties.SetItemStatus, e.g. a status chip's
    /// "Selected"/"Unselected"). Empty until the app first sets it.
    /// </summary>
    public string GetItemStatus(string automationId)
        => WaitForElement(automationId).Current.ItemStatus;

    /// <summary>
    /// <see cref="GetItemStatus"/> for use INSIDE a poll predicate: returns null instead
    /// of throwing when the element is absent or is torn out of the UIA tree between the
    /// find and the property read. PollUntil calls its condition bare, so an
    /// ElementNotAvailableException from a transient teardown (a tab switch reassigning
    /// MainWindow.Content mid-poll) would fail the test outright instead of being
    /// retried on the next tick, which is the opposite of what a wait helper is for.
    /// </summary>
    public string? TryGetItemStatus(string automationId)
    {
        try
        {
            return TryFindElement(automationId)?.Current.ItemStatus;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>Reads a TextBox's text via ValuePattern.</summary>
    public string GetTextBoxValue(string automationId)
        => ((ValuePattern)WaitForElement(automationId).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    /// <summary>Sets a TextBox's text via ValuePattern (fires TextChanged like typing).</summary>
    public void SetTextBoxValue(string automationId, string text)
        => ((ValuePattern)WaitForElement(automationId).GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

    /// <summary>Expands an Expander-like element (ExpandCollapsePattern).</summary>
    public void ExpandElement(string automationId)
        => ((ExpandCollapsePattern)WaitForElement(automationId).GetCurrentPattern(ExpandCollapsePattern.Pattern))
            .Expand();

    /// <summary>Toggles a CheckBox-like element (TogglePattern).</summary>
    public void ToggleElement(string automationId)
        => ((TogglePattern)WaitForElement(automationId).GetCurrentPattern(TogglePattern.Pattern)).Toggle();

    /// <summary>Selects one item in an exact-one control such as a RadioButton.</summary>
    public void SelectElement(string automationId)
        => ((SelectionItemPattern)WaitForElement(automationId)
            .GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    /// <summary>Whether a CheckBox-like element is currently checked (TogglePattern).</summary>
    public bool GetToggleState(string automationId)
        => ((TogglePattern)WaitForElement(automationId).GetCurrentPattern(TogglePattern.Pattern))
            .Current.ToggleState == ToggleState.On;

    /// <summary>
    /// The number of ListItem children the list exposes right now. Virtualizing lists
    /// only expose realized containers, so treat the count as exact only for small
    /// expected values (0 or 1 after a narrowing filter), which is what callers use
    /// it for: telling "filtered down to N" apart from "still showing the old list".
    /// </summary>
    public int GetListItemCount(string listAutomationId)
        => WaitForElement(listAutomationId).FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;

    /// <summary>Whether the list currently has a selected item (SelectionPattern).</summary>
    public bool ListHasSelection(string listAutomationId)
        => ((SelectionPattern)WaitForElement(listAutomationId).GetCurrentPattern(SelectionPattern.Pattern))
            .Current.GetSelection().Length > 0;

    /// <summary>Polls until the list's selection state matches.</summary>
    public void WaitForListSelection(string listAutomationId, bool hasSelection, int timeoutSeconds = 30)
        => PollUntil(
            () => ListHasSelection(listAutomationId) == hasSelection,
            DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds),
            () => $"list '{listAutomationId}' selection did not become {(hasSelection ? "non-empty" : "empty")} " +
                  $"within {timeoutSeconds}s");

    /// <summary>
    /// The Nth (0-based) ListItem child of a list, waiting for the list to have that
    /// many items first.
    /// </summary>
    public AutomationElement GetListItemAt(string listAutomationId, int index)
    {
        var list = WaitForElement(listAutomationId);
        AutomationElement? item = null;
        PollUntil(() =>
        {
            var items = list.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            if (items.Count <= index) return false;
            item = items[index];
            return true;
        }, DateTime.UtcNow + TimeSpan.FromSeconds(30),
            () => $"list '{listAutomationId}' never had {index + 1} item(s)");
        return item!;
    }

    /// <summary>Selects the Nth (0-based) ListItem child of a list.</summary>
    public void SelectListItemAt(string listAutomationId, int index)
        => Select(GetListItemAt(listAutomationId, index));

    /// <summary>
    /// Finds a Text element (TextBlock) by its rendered text, optionally scoped under
    /// another element, and returns it. TextBlock link "buttons" in this app are wired
    /// via MouseLeftButtonDown, which UIA cannot invoke, so pair with ClickElement.
    /// </summary>
    public AutomationElement WaitForTextElement(string text, string? scopeAutomationId = null,
        int timeoutSeconds = 30)
    {
        var scope = scopeAutomationId == null ? _uiaRoot : WaitForElement(scopeAutomationId);
        AutomationElement? element = null;
        PollUntil(() =>
        {
            element = scope.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, text),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
            return element != null;
        }, DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds),
            () => $"text element '{text}' did not appear" +
                  (scopeAutomationId == null ? "" : $" under '{scopeAutomationId}'"));
        return element!;
    }

    /// <summary>
    /// Clicks an element with the real mouse (foregrounds the window first). Needed for
    /// elements without InvokePattern: the app's TextBlock links handle
    /// MouseLeftButtonDown directly. Requires the element to be on screen; scroll it
    /// into view first (see ClickTextElementWithScroll).
    /// </summary>
    public void ClickElement(AutomationElement element)
    {
        EnsureForeground();
        var point = element.GetClickablePoint(); // physical screen px (host is per-monitor-v2)
        Win32.ClickAt((int)point.X, (int)point.Y);
        Thread.Sleep(150);
    }

    /// <summary>
    /// Raises the app window and CONFIRMS it actually became foreground before any
    /// synthetic click is injected.
    ///
    /// SetForegroundWindow returns without effect whenever another process owns the
    /// foreground lock (a different app was just activated, an elevated window is on
    /// top, some tray/monitor utilities hold it). Clicking anyway sends the click to
    /// whatever is really under those screen coordinates (another application), which
    /// both fails the test with a misleading "the UI never updated" timeout 30s later
    /// AND injects a stray click into someone else's window. On a shared desktop that
    /// can be a real, data-modifying click in another copy of this very app, so this
    /// refuses to click rather than clicking blind.
    /// </summary>
    private void EnsureForeground(int timeoutSeconds = 10)
    {
        PollUntil(
            () =>
            {
                if (Win32.GetForegroundWindow() == _hwnd) return true;
                Win32.SetForegroundWindow(_hwnd);
                return Win32.GetForegroundWindow() == _hwnd;
            },
            DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds),
            () => "the app window could not be brought to the foreground, so no click was "
                + "injected (another window owns the foreground lock: check for a topmost "
                + "or elevated window on this desktop)");
        Thread.Sleep(150);
    }

    /// <summary>
    /// Like <see cref="ClickElement"/> but with Ctrl held: the WPF single-select
    /// ListBox gesture that toggles the clicked row's selection OFF, which UIA's
    /// SelectionItemPattern cannot express for single-selection containers. Clicks
    /// near the element's LEFT edge rather than its centre: list-row templates put
    /// action buttons mid/right, and the toggle must land on the row itself.
    /// </summary>
    public void CtrlClickElement(AutomationElement element)
    {
        EnsureForeground();
        var rect = element.Current.BoundingRectangle; // physical screen px
        Win32.CtrlClickAt((int)(rect.Left + 15), (int)(rect.Top + rect.Height / 2));
        Thread.Sleep(150);
    }

    /// <summary>
    /// Clicks a TextBlock link that may sit outside its ScrollViewer's viewport: tries
    /// the current scroll position first, then walks the viewer's scroll positions until
    /// the element reports a clickable point.
    /// </summary>
    public void ClickTextElementWithScroll(string text, string scopeAutomationId,
        string scrollViewerAutomationId)
    {
        var viewer = WaitForElement(scrollViewerAutomationId);
        var scroll = (ScrollPattern)viewer.GetCurrentPattern(ScrollPattern.Pattern);
        var element = WaitForTextElement(text, scopeAutomationId);

        // The current scroll position first, since most links are already in view.
        if (TryClickElement(element)) return;

        if (scroll.Current.VerticallyScrollable) // when the content fits, there is nothing to walk
        {
            foreach (var percent in new double[] { 0, 25, 50, 75, 100 })
            {
                scroll.SetScrollPercent(ScrollPattern.NoScroll, percent);
                Thread.Sleep(100); // let the layout settle before GetClickablePoint
                if (TryClickElement(element)) return;
            }
        }
        throw new InvalidOperationException(
            $"could not bring text element '{text}' into view inside '{scrollViewerAutomationId}'");
    }

    /// <summary>ClickElement, but false instead of throwing when the element is off screen.</summary>
    private bool TryClickElement(AutomationElement element)
    {
        try
        {
            ClickElement(element);
            return true;
        }
        catch (NoClickablePointException)
        {
            return false;
        }
    }

    /// <summary>
    /// All Text (TextBlock) descendants under an element, for callers that need to
    /// inspect rendered names (e.g. picking a known quest link out of a template list).
    /// Non-waiting: returns an empty list when the scope element is not in the UIA tree
    /// right now (WPF drops collapsed sections from the tree entirely).
    /// </summary>
    public IReadOnlyList<AutomationElement> TryGetTextElements(string scopeAutomationId)
    {
        var scope = TryFindElement(scopeAutomationId);
        return scope == null ? Array.Empty<AutomationElement>() : TextElementsUnder(scope);
    }

    private static IReadOnlyList<AutomationElement> TextElementsUnder(AutomationElement scope)
    {
        var found = scope.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        var result = new List<AutomationElement>(found.Count);
        foreach (AutomationElement element in found) result.Add(element);
        return result;
    }

    private static void Select(AutomationElement element)
        => ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    private AutomationElement WaitForElement(string automationId, DateTime deadline)
    {
        AutomationElement? element = null;
        PollUntil(() => (element = TryFindElement(automationId)) != null, deadline,
            () => $"element '{automationId}' did not appear in the main window");
        return element!;
    }

    /// <summary>
    /// Shared poll loop (250ms cadence) behind every wait helper, so the retry/timeout
    /// mechanics live in one place instead of drifting across near-identical copies.
    /// Internal so test classes reuse it for their own conditions too.
    /// </summary>
    internal static void PollUntil(Func<bool> condition, DateTime deadline, Func<string> timeoutMessage)
    {
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(250);
        }
        throw new TimeoutException(timeoutMessage());
    }

    /// <summary>Convenience overload for test-local conditions: timeout in seconds from now.</summary>
    internal static void PollUntil(Func<bool> condition, string what, int timeoutSeconds = 30)
        => PollUntil(condition, DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds),
            () => $"timed out waiting for: {what}");

    private AutomationElement? TryFindElement(string automationId)
        => _uiaRoot.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    #endregion

    #region Owned windows (dialogs)

    /// <summary>
    /// Waits for a visible top-level window of the app process with the exact title
    /// (e.g. a modal dialog owned by the main window; owned windows are not reliably
    /// UIA descendants of their owner, so they are located via Win32 instead) and
    /// returns its root automation element for scoped searches.
    /// </summary>
    public AutomationElement WaitForAppWindow(string title, int timeoutSeconds = 30)
    {
        AutomationElement? window = null;
        PollUntil(() =>
        {
            var hwnd = Win32.FindTopLevelWindow(_process.Id, title);
            if (hwnd == IntPtr.Zero) return false;
            window = AutomationElement.FromHandle(hwnd);
            return true;
        }, DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds),
            () => $"window titled '{title}' did not appear within {timeoutSeconds}s");
        return window!;
    }

    /// <summary>Whether a visible top-level window with the exact title exists right now.</summary>
    public bool HasAppWindow(string title)
        => Win32.FindTopLevelWindow(_process.Id, title) != IntPtr.Zero;

    /// <summary>Waits until no visible top-level window with the title remains (dialog closed).</summary>
    public void WaitForAppWindowClosed(string title, int timeoutSeconds = 30)
        => PollUntil(() => !HasAppWindow(title),
            DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds),
            () => $"window titled '{title}' did not close within {timeoutSeconds}s");

    /// <summary>
    /// Waits for an element by AutomationId among a scope element's descendants,
    /// the dialog-window counterpart of <see cref="WaitForElement(string, int)"/>.
    /// </summary>
    public static AutomationElement WaitForElementUnder(AutomationElement scope, string automationId,
        int timeoutSeconds = 30)
    {
        AutomationElement? element = null;
        PollUntil(() =>
        {
            element = scope.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
            return element != null;
        }, DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds),
            () => $"element '{automationId}' did not appear under the scope element");
        return element!;
    }

    /// <summary>
    /// The rows of a repeating list, read by AutomationId rather than by rendered wording: for
    /// the list named <paramref name="listAutomationId"/>, the text of each cell named by
    /// <paramref name="cellAutomationIds"/>, one string array per row in display order.
    /// <para>
    /// Cell ids repeat across rows by design, so the cells are collected per column (UIA returns
    /// descendants in tree order) and zipped. A column of a different length means the template
    /// changed shape, and the pairing would be meaningless, so it fails loudly instead.
    /// </para>
    /// </summary>
    public static List<string[]> RowsUnder(
        AutomationElement scope, string listAutomationId, params string[] cellAutomationIds)
    {
        var list = WaitForElementUnder(scope, listAutomationId);

        var columns = cellAutomationIds
            .Select(id => list
                .FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, id))
                .Cast<AutomationElement>()
                .Select(cell => cell.Current.Name)
                .ToList())
            .ToList();

        var rowCount = columns[0].Count;
        Assert.All(columns, column => Assert.Equal(rowCount, column.Count));

        return Enumerable.Range(0, rowCount)
            .Select(row => columns.Select(column => column[row]).ToArray())
            .ToList();
    }

    /// <summary>Whether a Text (TextBlock) descendant with the exact rendered text exists under the scope element.</summary>
    public static bool HasTextElementUnder(AutomationElement scope, string text)
        => scope.FindFirst(TreeScope.Descendants, new AndCondition(
               new PropertyCondition(AutomationElement.NameProperty, text),
               new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))) != null;

    /// <summary>Invokes an already-resolved button-like element (InvokePattern), e.g. inside a dialog window.</summary>
    public static void Invoke(AutomationElement element)
        => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    #endregion

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.Dispose();
        }
        catch { /* best effort */ }
    }
}

/// <summary>Skips e2e tests when the app build output is not present.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (AppUnderTest.DllPath == null)
            Skip = "TarkovHelper build output not found - build TarkovHelper.csproj first";
    }
}

/// <summary>Theory twin of <see cref="E2EFactAttribute"/>: skips when the app build output is missing.</summary>
public sealed class E2ETheoryAttribute : TheoryAttribute
{
    public E2ETheoryAttribute()
    {
        if (AppUnderTest.DllPath == null)
            Skip = "TarkovHelper build output not found - build TarkovHelper.csproj first";
    }
}

/// <summary>
/// All e2e test classes join this single xUnit collection so they run SERIALLY.
/// Without it, xUnit runs different classes in parallel by default, which would launch
/// two real app instances at once: they fight over window focus and the global
/// keyboard hook, and one class's Dispose calls the process-global
/// SqliteConnection.ClearAllPools under the other's in-flight DB access.
/// </summary>
[CollectionDefinition("E2E")]
public sealed class E2ETestCollection { }

/// <summary>
/// Shared per-class scaffolding for e2e tests: an isolated temp root for throwaway
/// Config folders, and cleanup that clears the process-wide SQLite pools first so the
/// user_data.db files are unlocked before the directory delete.
/// </summary>
public abstract class E2ETestBase : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "TarkovHelperE2E", Guid.NewGuid().ToString("N"));

    protected string NewConfigDir()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Test-local waits reuse the harness's shared poll loop (AppDriver.PollUntil).</summary>
    private protected static void WaitUntil(Func<bool> condition, string what, int timeoutSeconds = 30)
        => AppDriver.PollUntil(condition, what, timeoutSeconds);

    /// <summary>
    /// Launches the app against a fresh throwaway Config dir and maximizes its window.
    /// private protected because the AppDriver return type is internal.
    /// </summary>
    private protected AppDriver LaunchMaximized() => LaunchMaximized(NewConfigDir());

    /// <summary>
    /// Same, against a caller-held Config dir, for tests that also read the
    /// user_data.db in that dir (persistence assertions, relaunch flows).
    /// </summary>
    private protected static AppDriver LaunchMaximized(string configDir)
    {
        var app = AppDriver.Launch(configDir);
        try
        {
            app.ShowWindow(Win32.SW_MAXIMIZE);
            return app;
        }
        catch
        {
            // Preserve the disposal the inline `using var app = ...` form provided
            // when ShowWindow throws after a successful launch.
            app.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Locates the app DLL matching this test build's configuration.</summary>
internal static class AppUnderTest
{
    public static readonly string? DllPath = Locate();

    private static string? Locate()
    {
        // ...\TarkovHelper.Tests\bin\<Configuration>\net8.0-windows\ up to the repo root
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = tfmDir.Parent?.Name;
        var repoRoot = tfmDir.Parent?.Parent?.Parent?.Parent;
        if (configuration == null || repoRoot == null) return null;

        var dll = Path.Combine(repoRoot.FullName, "TarkovHelper", "bin", configuration, tfmDir.Name, "TarkovHelper.dll");
        return File.Exists(dll) ? dll : null;
    }
}

/// <summary>Minimal user32 interop for driving the app window from the tests.</summary>
internal static class Win32
{
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_MAXIMIZE = 3;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT minPosition, maxPosition;
        public RECT normalPosition;

        public static WINDOWPLACEMENT Create()
            => new() { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
    }

    public readonly struct WindowRect
    {
        public WindowRect(RECT r, double scale)
        {
            Left = r.Left * scale;
            Top = r.Top * scale;
            Width = (r.Right - r.Left) * scale;
            Height = (r.Bottom - r.Top) * scale;
        }

        public double Left { get; }
        public double Top { get; }
        public double Width { get; }
        public double Height { get; }
    }

    public static bool IsWithinVirtualScreen(WindowRect rect)
    {
        double left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        double top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        return rect.Left >= left && rect.Top >= top &&
               rect.Left + rect.Width <= left + GetSystemMetrics(SM_CXVIRTUALSCREEN) &&
               rect.Top + rect.Height <= top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
    }

    /// <summary>Finds a visible top-level window of the process with the exact title.</summary>
    public static IntPtr FindTopLevelWindow(int processId, string title)
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != processId || !IsWindowVisible(hwnd)) return true;

            var text = new StringBuilder(256);
            GetWindowText(hwnd, text, text.Capacity);
            if (text.ToString() != title) return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    /// <summary>Left-clicks at a physical screen coordinate with the real cursor.</summary>
    public static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(50); // give WPF hit-testing the cursor position before the press
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Left-clicks with Ctrl held (the toggle-selection gesture).</summary>
    public static void CtrlClickAt(int x, int y)
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        try
        {
            ClickAt(x, y);
        }
        finally
        {
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }

    /// <summary>
    /// Presses and releases Escape as real keyboard input to the foreground window,
    /// for exercising a dialog's IsCancel keyboard path (InvokePattern cannot).
    /// </summary>
    public static void PressEscape()
    {
        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private const byte VK_CONTROL = 0x11;
    private const byte VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(
        IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}

/// <summary>
/// Pins the test host's DPI awareness before any test code runs. Loading UI Automation
/// (or other UI stacks) can flip an unset process to DPI-aware mid-run, which would
/// silently switch GetWindowRect between virtualized and physical coordinates depending
/// on test ordering. Forcing per-monitor-v2 here makes the coordinate space
/// deterministic (AppDriver.GetWindowRect then converts physical px to WPF units).
/// </summary>
internal static class TestHostDpiAwareness
{
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Ensure()
    {
        // Fails harmlessly when awareness is already set: by then it is aware anyway.
        Win32.SetProcessDpiAwarenessContext(PerMonitorAwareV2);
    }
}

/// <summary>
/// Direct user_data.db access for seeding rows before a launch and asserting the persisted
/// values after a close. <see cref="CreateUserDataDb"/> builds the file through the app's own
/// store, so the pre-launch schema is the production schema; the seeders below just INSERT
/// into it and callers must run CreateUserDataDb first.
/// </summary>
internal static class E2EDb
{
    /// <summary>
    /// Creates user_data.db with the production schema so tests can seed rows without a first
    /// app launch. Built by the app's own store rather than by hand-written DDL: the app's
    /// schema creation is CREATE TABLE IF NOT EXISTS, so it silently adopts whatever table a
    /// pre-created file already holds. A hand-copied CREATE TABLE that drifted from production
    /// (a column added on the app side, as AppProfileId was added to RaidHistory) would be
    /// adopted as-is and the app would then fail at runtime on the missing column.
    ///
    /// The blocking wait lives here rather than in the calling test method on purpose: xUnit's
    /// xUnit1031 analyzer flags blocking calls in test methods only.
    /// </summary>
    public static void CreateUserDataDb(string configDir)
        => new UserDataDbService(Path.Combine(configDir, "user_data.db"))
            .InitializeAsync().GetAwaiter().GetResult();

    public static void SeedSetting(string configDir, string key, string value)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(configDir, "user_data.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO UserSettings (Key, Value) VALUES ($key, $value) " +
            "ON CONFLICT(Key) DO UPDATE SET Value = $value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Seeds one QuestProgress row before a launch, so a reset test starts from a database that
    /// already holds per-profile progress. Requires <see cref="CreateUserDataDb"/> to have run
    /// on this config dir first (same precondition as <see cref="SeedSetting"/>).
    /// </summary>
    public static void SeedQuestProgress(
        string configDir, string profileId, string id, string? normalizedName, string status)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(configDir, "user_data.db")}");
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = @"
            INSERT OR REPLACE INTO QuestProgress (ProfileId, Id, NormalizedName, Status, UpdatedAt)
            VALUES ($profile, $id, $name, $status, $updatedAt)";
        insert.Parameters.AddWithValue("$profile", profileId);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$name", (object?)normalizedName ?? DBNull.Value);
        insert.Parameters.AddWithValue("$status", status);
        insert.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o"));
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// Seeds one per-profile setting row pre-launch. Requires <see cref="CreateUserDataDb"/> to
    /// have run on this config dir first.
    /// </summary>
    public static void SeedProfileSetting(string configDir, string profileId, string key, string value)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(configDir, "user_data.db")}");
        connection.Open();
        using var insert = connection.CreateCommand();
        insert.CommandText = @"
            INSERT INTO ProfileSettings (ProfileId, Key, Value) VALUES ($profile, $key, $value)
            ON CONFLICT(ProfileId, Key) DO UPDATE SET Value = $value";
        insert.Parameters.AddWithValue("$profile", profileId);
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$value", value);
        insert.ExecuteNonQuery();
    }

    /// <summary>Reads one per-profile setting value, or null when the row/table/db is missing.</summary>
    public static string? ReadProfileSetting(string configDir, string profileId, string key)
        => ReadSingleValue(
            configDir,
            "SELECT Value FROM ProfileSettings WHERE ProfileId = $profile AND Key = $key",
            command =>
            {
                command.Parameters.AddWithValue("$profile", profileId);
                command.Parameters.AddWithValue("$key", key);
            });

    /// <summary>Reads a persisted global setting value, or null when the row/table/db is missing.</summary>
    public static string? ReadSetting(string configDir, string key)
        => ReadSingleValue(
            configDir,
            "SELECT Value FROM UserSettings WHERE Key = $key",
            command => command.Parameters.AddWithValue("$key", key));

    /// <summary>
    /// Reads a quest's persisted progress status (the QuestProgress row keyed by
    /// quest Id or NormalizedName), or null when no row / no db exists yet. Lets
    /// tests assert a completion actually reached user_data.db, since the app's batch
    /// save is fire-and-forget with a swallowing catch, so in-memory UI state alone
    /// proves nothing about persistence.
    /// </summary>
    public static string? ReadQuestProgress(string configDir, string questKey)
        => ReadQuestStatus(
            configDir,
            "Id = $key OR NormalizedName = $key",
            command => command.Parameters.AddWithValue("$key", questKey));

    /// <summary>
    /// Reads a quest's persisted status within ONE profile's partition, or null when that
    /// profile has no row for it. The profile-blind overload above cannot express the assertion
    /// attribution needs ("present here and absent there") because it stops at the first
    /// matching row whichever partition it is in.
    /// </summary>
    public static string? ReadQuestProgress(string configDir, string profileId, string questKey)
        => ReadQuestStatus(
            configDir,
            "ProfileId = $profile AND (Id = $key OR NormalizedName = $key)",
            command =>
            {
                command.Parameters.AddWithValue("$profile", profileId);
                command.Parameters.AddWithValue("$key", questKey);
            });

    /// <summary>
    /// The clause both ReadQuestProgress overloads vary: only the WHERE differs, and it is
    /// always a literal from this file (values arrive as parameters through
    /// <paramref name="bindParameters"/>, never interpolated).
    /// </summary>
    private static string? ReadQuestStatus(
        string configDir, string where, Action<SqliteCommand> bindParameters)
        => ReadSingleValue(
            configDir, $"SELECT Status FROM QuestProgress WHERE {where} LIMIT 1", bindParameters);

    /// <summary>
    /// The scaffolding every reader above shares: open user_data.db if it exists, run one
    /// single-value query, and report "no value" rather than throwing for a table the app has
    /// not created yet (a launch that never reached its first save, or a db seeded with only
    /// the tables one test needs). <paramref name="sql"/> is always a literal from this file;
    /// test values reach the command as parameters through <paramref name="bindParameters"/>.
    /// </summary>
    private static string? ReadSingleValue(
        string configDir, string sql, Action<SqliteCommand> bindParameters)
    {
        var dbPath = Path.Combine(configDir, "user_data.db");
        if (!File.Exists(dbPath)) return null;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bindParameters(command);
        try
        {
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            // Table not created yet.
            return null;
        }
    }
}

/// <summary>
/// Reads the quests a tarkov_data.db offers as e2e fixtures: the shipped seed for
/// <c>ProgressCarryOverE2ETests</c>, the regenerated candidate for <c>LegacySmokeE2ETests</c>.
/// <para>
/// Both need the same two judgements, so they are made once here. Which quests are usable at
/// all: the quest tab's search is a substring match, so a name that is a substring of another
/// quest's name never filters the list down to one row and every wait on it would time out.
/// And which of those are <em>carried renames</em>: a quest whose stored
/// <c>Quests.NormalizedName</c> no longer matches what its current title would produce, which
/// is exactly the identity carry-over the 1.1 refresh exists to guarantee.
/// </para>
/// <para>
/// The title-to-key rule comes from <see cref="QuestNormalizedName.SqlForm"/>, the pipeline's
/// own pinned reproduction of the app's SQL expression, rather than a copy spelled out here.
/// A copy would not detect drift, it would BE drift: <c>ToLowerInvariant</c> lowers 1,146 BMP
/// code points that the bundled ICU-less SQLite <c>LOWER</c> leaves alone, so a title carrying
/// a cased non-ASCII letter would be misread as a carried rename and preferred over the real
/// ones, quietly retiring the very case these tests exist for. The pinned rule is the thing
/// with a drift guard: <c>QuestNormalizedNameTests</c> evaluates the app's actual SQL over
/// every published name and compares.
/// </para>
/// </summary>
internal static class E2EQuests
{
    /// <param name="IsCarriedRename">
    /// True when the stored key no longer matches the current title, i.e. a rename whose row key
    /// and progress key were carried across a refresh.
    /// </param>
    internal sealed record Quest(string Id, string Name, string NormalizedName, bool IsCarriedRename);

    /// <param name="UniquelySearchable">
    /// Quests whose name is a unique search substring across the whole table, in title order.
    /// </param>
    /// <param name="HasNormalizedNameColumn">
    /// False for a database published before the column existed. There are no carried renames in
    /// such a database by definition, because the app derives the key from the title itself.
    /// </param>
    internal sealed record Catalogue(IReadOnlyList<Quest> UniquelySearchable, bool HasNormalizedNameColumn);

    public static Catalogue Read(string databasePath)
    {
        Assert.True(File.Exists(databasePath), $"quest database not found at {databasePath}");

        var quests = new List<Quest>();
        // Every title in the table, including rows rejected as fixtures below: the quest tab
        // lists them too, so they are what a search has to stay unambiguous against.
        var allNames = new List<string>();
        bool hasNormalizedName;

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();

            hasNormalizedName = ColumnExists(connection, "Quests", "NormalizedName");
            // Without the column both app builds compute the key from the title with this very
            // expression, so the fallback reads back exactly what they would use, and reports no
            // carried renames, which is the truth for data published before the refresh.
            var expression = hasNormalizedName
                ? "NormalizedName"
                : "LOWER(REPLACE(REPLACE(REPLACE(Name, ' ', '-'), '''', ''), '.', ''))";

            using var cmd = new SqliteCommand(
                $"SELECT Id, Name, {expression} FROM Quests " +
                "WHERE Id IS NOT NULL AND Id <> '' AND Name IS NOT NULL AND Name <> '' ORDER BY Name",
                connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                allNames.Add(name);

                var normalizedName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                if (normalizedName.Length == 0)
                    continue;

                quests.Add(new Quest(
                    reader.GetString(0),
                    name,
                    normalizedName,
                    IsCarriedRename: normalizedName != QuestNormalizedName.SqlForm(name)));
            }
        }

        SqliteConnection.ClearAllPools();

        var unique = quests
            .Where(q => allNames.Count(n => n.Contains(q.Name, StringComparison.OrdinalIgnoreCase)) == 1)
            .ToList();

        return new Catalogue(unique, hasNormalizedName);
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var cmd = new SqliteCommand($"PRAGMA table_info({table})", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Proves that a database a test staged into a build's Assets folder is still the one that build
/// read, for builds that predate TARKOVHELPER_DISABLE_DB_UPDATE and therefore run their data
/// check no matter what the harness sets.
/// <para>
/// Nothing in such a build reports which database it loaded, so this reads the evidence its
/// DatabaseUpdateService leaves on disk instead. A download stages <c>tarkov_data.db.tmp</c>,
/// moves the live file aside as <c>tarkov_data.db.bak</c>, moves the download into place and
/// rewrites <c>db_version.txt</c>; it deliberately clears the SQLite connection pools and retries
/// the move so it CAN overwrite the file while the app has it open. So an unchanged hash, no
/// leftover .tmp/.bak from a download that failed halfway or is still running, and the pinned
/// version token still in place together say the check stayed neutralised for the whole run.
/// </para>
/// <para>
/// Extracted from LegacySmokeE2ETests so the failure paths can be exercised by ordinary unit
/// tests: this guard only ever runs on a machine holding a real extracted release, and a guard
/// nobody can run is exactly the kind that silently stops guarding.
/// </para>
/// </summary>
internal static class StagedDatabase
{
    /// <summary>The file name a build's data check keeps its version token in, beside the database.</summary>
    public const string VersionFileName = "db_version.txt";

    /// <summary>Hex SHA-256 of a file, opened share-read so it also works while the app has it open.</summary>
    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <param name="databasePath">The staged tarkov_data.db inside the build's Assets folder.</param>
    /// <param name="expectedHash">Its <see cref="Sha256"/> as staged.</param>
    /// <param name="expectedVersionToken">
    /// The token the data check was pinned to, or null when the build's folder holds no version
    /// file at all (which is itself a state a completed download would end).
    /// </param>
    public static void AssertStillStaged(string databasePath, string expectedHash, string? expectedVersionToken)
    {
        foreach (var artefact in new[] { databasePath + ".tmp", databasePath + ".bak" })
        {
            Assert.False(File.Exists(artefact),
                $"{artefact} exists, so the build ran its data check and downloaded the published " +
                "database over the staged one. This run proved nothing about the staged database.");
        }

        var versionFile = Path.Combine(Path.GetDirectoryName(databasePath)!, VersionFileName);
        var token = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;
        Assert.True(token == expectedVersionToken,
            $"{versionFile} now reads '{token}' but was pinned to '{expectedVersionToken}'. Only a " +
            "completed database download rewrites it, so the build replaced the staged database.");

        var hash = Sha256(databasePath);
        Assert.True(hash == expectedHash,
            $"{databasePath} now hashes to {hash} but was staged as {expectedHash}. The build replaced " +
            "it mid-run, so what it rendered was not the staged database.");
    }
}
