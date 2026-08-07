using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiCopy.Infrastructure;
using MultiCopy.Native;
using MultiCopy.Services;
using MultiCopy.State;

namespace MultiCopy.Views;

/// <summary>
/// 设置对话框：自定义全局快捷键。
/// 用按键捕获（PreviewKeyDown）记录组合，比 ComboBox 更直觉。
/// 应用/确定时调用 HotkeyService.UpdateSettings 即时注册；
/// 取消时若已应用过改动，还原到对话框打开时的原始配置。
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly HotkeyService _hotkey;
    private readonly SettingsStorageService.SettingsDto _original; // 打开时生效的配置（取消还原用）

    // 当前捕获区显示的（尚未应用的）组合
    private HotkeyModifierKeys _capturedMods;
    private int _capturedKey;
    private bool _hasValidCapture;

    // 已应用到 HotkeyService 的组合（取消时若与 original 不同需还原）
    private bool _appliedEnabled;
    private HotkeyModifierKeys _appliedMods;
    private int _appliedKey;

    // 当前编辑中的前后缀内容
    private string _currentPrefix = string.Empty;
    private string _currentSuffix = string.Empty;

    private SettingsDialog(HotkeyService hotkey)
    {
        _hotkey = hotkey;
        _original = hotkey.CurrentSettings;
        InitializeComponent();

        // 初始化 UI 为当前生效配置
        EnableCheckBox.IsChecked = _original.HotkeyEnabled;
        _appliedEnabled = _original.HotkeyEnabled;
        _appliedMods = (HotkeyModifierKeys)_original.Modifiers;
        _appliedKey = _original.Key;
        _capturedMods = _appliedMods;
        _capturedKey = _appliedKey;
        _hasValidCapture = _original.HotkeyEnabled;
        if (_original.HotkeyEnabled)
            CaptureBox.Text = HotkeyFormatter.Format(_capturedMods, _capturedKey);

        // 初始化前后缀内容和启用状态
        var state = MultiCopy.State.AppState.Instance;
        _currentPrefix = state.PastePrefix ?? string.Empty;
        _currentSuffix = state.PasteSuffix ?? string.Empty;
        EnablePrefixCheckBox.IsChecked = state.IsPastePrefixEnabled;
        EnableSuffixCheckBox.IsChecked = state.IsPasteSuffixEnabled;
        UpdatePrefixSummary();
        UpdateSuffixSummary();

        // 初始化序号自增配置
        EnableSequenceCheckBox.IsChecked = state.IsPrefixSequenceEnabled;
        SequenceStartTextBox.Text = state.PrefixSequenceStart.ToString();
        SequenceFormatCombo.SelectedIndex = (int)state.PrefixSequenceFormat;
        SequencePositionCombo.SelectedIndex = (int)state.PrefixSequencePosition;

        UpdateCaptureEnabled();
        Loaded += (_, _) => { if (EnableCheckBox.IsChecked == true) CaptureBox.Focus(); };
    }

    /// <summary>更新前缀摘要框显示：空时显示提示，非空时把换行显示为 ↵。</summary>
    private void UpdatePrefixSummary()
    {
        string display = string.IsNullOrEmpty(_currentPrefix)
            ? "点击此处编辑前缀内容…"
            : _currentPrefix.Replace("\r\n", "↵ ").Replace("\n", "↵ ");
        PrefixSummaryTextBox.Text = display;
    }

    /// <summary>更新后缀摘要框显示：空时显示提示，非空时把换行显示为 ↵。</summary>
    private void UpdateSuffixSummary()
    {
        string display = string.IsNullOrEmpty(_currentSuffix)
            ? "点击此处编辑后缀内容…"
            : _currentSuffix.Replace("\r\n", "↵ ").Replace("\n", "↵ ");
        SuffixSummaryTextBox.Text = display;
    }

    /// <summary>点击前缀摘要框：弹出多行编辑对话框。</summary>
    private void PrefixSummaryBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        string? result = EditPasteSuffixDialog.Show(this, "编辑前缀内容", _currentPrefix);
        if (result != null)
        {
            _currentPrefix = result;
            UpdatePrefixSummary();
        }
    }

    /// <summary>点击后缀摘要框：弹出多行编辑对话框。</summary>
    private void SuffixSummaryBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        string? result = EditPasteSuffixDialog.Show(this, "编辑后缀内容", _currentSuffix);
        if (result != null)
        {
            _currentSuffix = result;
            UpdateSuffixSummary();
        }
    }

    /// <summary>弹出设置对话框。返回 true=有改动并已应用。</summary>
    public static bool Show(Window? owner, HotkeyService hotkey)
    {
        var dlg = new SettingsDialog(hotkey);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        else
        {
            // owner 隐藏时 CenterOwner 会定位异常，退化为 CenterScreen
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dlg.ShowDialog() == true;
    }

    // ---------- 启用开关 ----------
    private void Enable_Changed(object sender, RoutedEventArgs e)
    {
        UpdateCaptureEnabled();
    }

    private void UpdateCaptureEnabled()
    {
        bool en = EnableCheckBox.IsChecked == true;
        CaptureBox.IsEnabled = en;
        OkButton.IsEnabled = en;
    }

    // ---------- 序号输入限制 ----------
    /// <summary>限制起始序号只能输入正整数。</summary>
    private void SequenceStartTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out int v) || v < 0;
    }

    /// <summary>限制粘贴操作只能粘贴数字。</summary>
    private void SequenceStartTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            string text = (string)e.DataObject.GetData(DataFormats.Text);
            if (!int.TryParse(text, out _))
                e.CancelCommand();
        }
        else
        {
            e.CancelCommand();
        }
    }

    // ---------- 按键捕获 ----------
    private void CaptureBox_GotFocus(object sender, RoutedEventArgs e)
    {
        HintBlock.Text = "按下想要的组合键（需至少一个修饰键）";
    }

    private void CaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true; // 阻止文本输入

        // Alt 按下时 e.Key=System，真正按键在 SystemKey
        Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;

        // 纯修饰键：等待主键，不记录
        if (IsModifierKey(key)) return;

        // 修饰键状态（Keyboard.Modifiers 反映当前实际按下的修饰键）
        var mods = WpfToWin32(Keyboard.Modifiers);
        if (mods == HotkeyModifierKeys.None)
        {
            HintBlock.Text = "请配合修饰键（Alt/Ctrl/Shift/Win）一起按下";
            _hasValidCapture = false;
            return;
        }

        _capturedMods = mods;
        _capturedKey = KeyInterop.VirtualKeyFromKey(key);
        _hasValidCapture = true;
        CaptureBox.Text = HotkeyFormatter.Format(_capturedMods, _capturedKey);
        HintBlock.Text = "点击「应用」立即生效";
    }

    private static bool IsModifierKey(Key k) =>
        k == Key.LeftCtrl || k == Key.RightCtrl ||
        k == Key.LeftAlt || k == Key.RightAlt ||
        k == Key.LeftShift || k == Key.RightShift ||
        k == Key.LWin || k == Key.RWin;

    private static HotkeyModifierKeys WpfToWin32(ModifierKeys m)
    {
        HotkeyModifierKeys r = HotkeyModifierKeys.None;
        if (m.HasFlag(ModifierKeys.Alt)) r |= HotkeyModifierKeys.Alt;
        if (m.HasFlag(ModifierKeys.Control)) r |= HotkeyModifierKeys.Control;
        if (m.HasFlag(ModifierKeys.Shift)) r |= HotkeyModifierKeys.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) r |= HotkeyModifierKeys.Win;
        return r;
    }

    // ---------- 按钮 ----------
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DoApply()) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 若已应用过与原始不同的配置，还原到原始
        if (_appliedEnabled != _original.HotkeyEnabled ||
            _appliedMods != (HotkeyModifierKeys)_original.Modifiers ||
            _appliedKey != _original.Key)
        {
            _hotkey.UpdateSettings(_original.HotkeyEnabled, _original.Modifiers, _original.Key);
        }
        DialogResult = false;
    }

    /// <summary>应用当前捕获。返回是否成功（含禁用态：禁用视为成功）。</summary>
    private bool DoApply()
    {
        bool enabled = EnableCheckBox.IsChecked == true;
        if (enabled && !_hasValidCapture)
        {
            HintBlock.Text = "请先捕获一个有效的快捷键组合";
            return false;
        }

        uint mods = enabled ? (uint)_capturedMods : 0;
        int key = enabled ? _capturedKey : 0;

        var (ok, error) = _hotkey.UpdateSettings(enabled, mods, key);
        if (!ok)
        {
            MessageBox.Show(this, error, "快捷键注册失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            HintBlock.Text = error;
            return false;
        }

        _appliedEnabled = enabled;
        _appliedMods = _capturedMods;
        _appliedKey = _capturedKey;
        HintBlock.Text = "已生效" + (enabled ? $"（{HotkeyFormatter.Format(_capturedMods, _capturedKey)}）" : "（已禁用）");


        // 保存粘贴前后缀设置（独立于快捷键保存逻辑，立即生效并持久化）
        string prefix = _currentPrefix;
        string suffix = _currentSuffix;
        bool prefixEnabled = EnablePrefixCheckBox.IsChecked == true;
        bool suffixEnabled = EnableSuffixCheckBox.IsChecked == true;
        bool sequenceEnabled = EnableSequenceCheckBox.IsChecked == true;
        // 起始序号校验：解析失败或 <1 时回退到 1
        int sequenceStart = int.TryParse(SequenceStartTextBox.Text, out int parsed) && parsed >= 1 ? parsed : 1;
        var seqFormat = (PrefixSequenceFormat)SequenceFormatCombo.SelectedIndex;
        var seqPosition = (PrefixSequencePosition)SequencePositionCombo.SelectedIndex;

        var currentSettings = SettingsStorageService.Load();
        bool settingsChanged = false;
        if (currentSettings.PastePrefix != prefix)
        {
            currentSettings.PastePrefix = prefix;
            settingsChanged = true;
        }
        if (currentSettings.PasteSuffix != suffix)
        {
            currentSettings.PasteSuffix = suffix;
            settingsChanged = true;
        }
        if (currentSettings.IsPastePrefixEnabled != prefixEnabled)
        {
            currentSettings.IsPastePrefixEnabled = prefixEnabled;
            settingsChanged = true;
        }
        if (currentSettings.IsPasteSuffixEnabled != suffixEnabled)
        {
            currentSettings.IsPasteSuffixEnabled = suffixEnabled;
            settingsChanged = true;
        }
        if (currentSettings.IsPrefixSequenceEnabled != sequenceEnabled)
        {
            currentSettings.IsPrefixSequenceEnabled = sequenceEnabled;
            settingsChanged = true;
        }
        if (currentSettings.PrefixSequenceStart != sequenceStart)
        {
            currentSettings.PrefixSequenceStart = sequenceStart;
            // 起始序号变更时，当前序号也重置到新起始值
            currentSettings.PrefixSequenceCurrent = sequenceStart;
            settingsChanged = true;
        }
        if (currentSettings.PrefixSequenceFormat != seqFormat)
        {
            currentSettings.PrefixSequenceFormat = seqFormat;
            settingsChanged = true;
        }
        if (currentSettings.PrefixSequencePosition != seqPosition)
        {
            currentSettings.PrefixSequencePosition = seqPosition;
            settingsChanged = true;
        }
        // 当前序号值可能已因粘贴递增，始终写回最新值
        if (currentSettings.PrefixSequenceCurrent != MultiCopy.State.AppState.Instance.PrefixSequenceCurrent)
        {
            currentSettings.PrefixSequenceCurrent = MultiCopy.State.AppState.Instance.PrefixSequenceCurrent;
            settingsChanged = true;
        }
        if (settingsChanged)
        {
            SettingsStorageService.Save(currentSettings);
        }

        var state = MultiCopy.State.AppState.Instance;
        state.PastePrefix = prefix;
        state.PasteSuffix = suffix;
        state.IsPastePrefixEnabled = prefixEnabled;
        state.IsPasteSuffixEnabled = suffixEnabled;
        state.IsPrefixSequenceEnabled = sequenceEnabled;
        state.PrefixSequenceStart = sequenceStart;
        state.PrefixSequenceFormat = seqFormat;
        state.PrefixSequencePosition = seqPosition;
        // 起始序号变更时同步重置当前序号
        if (state.PrefixSequenceCurrent < sequenceStart)
            state.PrefixSequenceCurrent = sequenceStart;
        return true;
    }
}
