using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiCopy.Infrastructure;
using MultiCopy.Native;
using MultiCopy.Services;
using MultiCopy.State;

namespace MultiCopy.Views;

/// <summary>
/// 设置对话框：自定义全局快捷键与粘贴前后缀/序号。
/// 用按键捕获（PreviewKeyDown）记录组合，比 ComboBox 更直觉。
/// 所有改动仅在点「确定」时经 HotkeyService.UpdateSettings / SettingsStorageService 应用，
/// 取消即丢弃未保存的修改。
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly HotkeyService _hotkey;
    private readonly SettingsStorageService.SettingsDto _original; // 打开时生效的配置（取消还原用）

    // 主热键与片段速取热键的捕获器（捕获逻辑相同，实例化两次）
    private readonly HotkeyCapture _mainCapture;
    private readonly HotkeyCapture _snippetCapture;

    // 当前编辑中的前后缀内容
    private string _currentPrefix = string.Empty;
    private string _currentSuffix = string.Empty;

    // 打开时生效的队列容量（取消对比与解析失败回退用）
    private readonly int _originalCapacity;

    private SettingsDialog(HotkeyService hotkey)
    {
        _hotkey = hotkey;
        _original = hotkey.CurrentSettings;
        InitializeComponent();
        _mainCapture = new HotkeyCapture(CaptureBox, HintBlock);
        _snippetCapture = new HotkeyCapture(SnippetCaptureBox, SnippetHintBlock);

        // 初始化 UI 为当前生效配置
        EnableCheckBox.IsChecked = _original.HotkeyEnabled;
        _mainCapture.Reset((HotkeyModifierKeys)_original.Modifiers, _original.Key, _original.HotkeyEnabled);
        SnippetEnableCheckBox.IsChecked = _original.SnippetHotkeyEnabled;
        _snippetCapture.Reset((HotkeyModifierKeys)_original.SnippetModifiers, _original.SnippetKey, _original.SnippetHotkeyEnabled);

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

        // 初始化队列容量
        _originalCapacity = state.MaxQueueItems;
        QueueCapacityTextBox.Text = _originalCapacity.ToString();

        // 序号自增依赖前缀：按初始状态同步序号编辑区可用性
        UpdateSequenceEditorEnabled();

        UpdateCaptureEnabled();
        UpdatePastePreview();
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

    // ---------- 粘贴效果实时预览 ----------
    /// <summary>预览联动入口：前后缀/序号任一配置变化时触发（XAML 事件挂接）。</summary>
    private void PreviewSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (PreviewTextBlock == null) return; // XAML 解析期间预览控件尚未就绪
        UpdatePastePreview();
    }

    /// <summary>前缀/序号开关变化：同步序号编辑区可用状态，并刷新预览。
    /// 序号自增仅在前缀启用时实际生效（见 AppState.BuildPasteText），UI 保持一致。</summary>
    private void SequenceDependency_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSequenceEditorEnabled();
        if (PreviewTextBlock != null) UpdatePastePreview();
    }

    /// <summary>按「启用前缀」「启用序号自增」状态启用/禁用序号配置行（禁用时 WPF 自动置灰）。</summary>
    private void UpdateSequenceEditorEnabled()
    {
        bool prefixOn = EnablePrefixCheckBox.IsChecked == true;
        EnableSequenceCheckBox.IsEnabled = prefixOn;
        SequenceEditorGrid.IsEnabled = prefixOn && EnableSequenceCheckBox.IsChecked == true;
    }

    /// <summary>
    /// 刷新粘贴效果预览：按启用状态拼接 前缀+序号+正文+后缀（示例正文，不递增序号）。
    /// 拼接规则与 AppState.BuildPasteText 一致，起始序号校验与 DoApply 相同。
    /// </summary>
    private void UpdatePastePreview()
    {
        const string body = "这里是复制的内容";
        bool prefixEnabled = EnablePrefixCheckBox.IsChecked == true;
        bool suffixEnabled = EnableSuffixCheckBox.IsChecked == true;

        if (!prefixEnabled && !suffixEnabled)
        {
            PreviewTextBlock.Text = "未启用前/后缀，粘贴原文";
            return;
        }

        string prefix = prefixEnabled ? _currentPrefix : string.Empty;
        string suffix = suffixEnabled ? _currentSuffix : string.Empty;
        bool sequenceEnabled = EnableSequenceCheckBox.IsChecked == true;

        string result;
        if (!prefixEnabled || !sequenceEnabled)
        {
            result = prefix + body + suffix;
        }
        else
        {
            int start = int.TryParse(SequenceStartTextBox.Text, out int parsed) && parsed >= 1 ? parsed : 1;
            var format = (PrefixSequenceFormat)SequenceFormatCombo.SelectedIndex;
            var position = (PrefixSequencePosition)SequencePositionCombo.SelectedIndex;
            string seq = SequenceFormatter.Format(start, format);
            string fullPrefix = position == PrefixSequencePosition.BeforePrefix ? seq + prefix : prefix + seq;
            result = fullPrefix + body + suffix;
        }

        PreviewTextBlock.Text = result.Length == 0
            ? "（前/后缀内容为空，粘贴原文）"
            : result;
    }

    /// <summary>点击前缀摘要框：弹出多行编辑对话框。</summary>
    private void PrefixSummaryBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        string? result = EditPasteSuffixDialog.Show(this, "编辑前缀内容", _currentPrefix);
        if (result != null)
        {
            _currentPrefix = result;
            UpdatePrefixSummary();
            UpdatePastePreview();
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
            UpdatePastePreview();
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
        // 仅禁用捕获区；「确定」始终可用，否则禁用热键后将无法保存任何设置
        CaptureBox.IsEnabled = EnableCheckBox.IsChecked == true;
        SnippetCaptureBox.IsEnabled = SnippetEnableCheckBox.IsChecked == true;
    }

    // ---------- 序号输入限制 ----------
    /// <summary>限制起始序号只能输入正整数（按拼入后的完整文本校验，拦下 0 与前导零）。</summary>
    private void SequenceStartTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IsValidPositiveIntInput(SequenceStartTextBox, e.Text);
    }

    /// <summary>限制粘贴操作只能粘贴 ≥1 的整数。</summary>
    private void SequenceStartTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            string text = (string)e.DataObject.GetData(DataFormats.Text);
            if (!int.TryParse(text, out int v) || v < 1)
                e.CancelCommand();
        }
        else
        {
            e.CancelCommand();
        }
    }

    // ---------- 队列容量输入限制 ----------
    /// <summary>限制容量输入只能输入正整数（按拼入后的完整文本校验，拦下 0 与前导零）。</summary>
    private void QueueCapacityTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IsValidPositiveIntInput(QueueCapacityTextBox, e.Text);
    }

    /// <summary>限制粘贴操作只能粘贴 ≥1 的整数。</summary>
    private void QueueCapacityTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            string text = (string)e.DataObject.GetData(DataFormats.Text);
            if (!int.TryParse(text, out int v) || v < 1)
                e.CancelCommand();
        }
        else
        {
            e.CancelCommand();
        }
    }

    /// <summary>校验按键拼入后的完整文本是否为 ≥1 的整数（拦下 0 与前导零）。</summary>
    private static bool IsValidPositiveIntInput(TextBox box, string input)
    {
        // 拼入位置按选区处理：有选区=替换选区，无选区=插入光标处
        string text = box.Text.Remove(box.SelectionStart, box.SelectionLength);
        text = text.Insert(box.SelectionStart, input);
        return int.TryParse(text, out int v) && v >= 1;
    }

    // ---------- 按键捕获 ----------
    /// <summary>
    /// 按键捕获器：管理一个只读 TextBox 的组合键捕获（PreviewKeyDown 记录修饰键+主键）。
    /// 主热键与片段速取热键共用此类，各自持有独立的捕获状态与提示区。
    /// </summary>
    private sealed class HotkeyCapture
    {
        private readonly TextBox _box;
        private readonly Action<string> _setHint;

        public HotkeyModifierKeys Mods { get; private set; }

        /// <summary>主键的虚拟键码。命名避开 Input.Key 枚举，防止类内引用被遮蔽。</summary>
        public int VirtualKey { get; private set; }

        public bool HasValid { get; private set; }

        public HotkeyCapture(TextBox box, TextBlock hint)
        {
            _box = box;
            _setHint = text => hint.Text = text;
            box.PreviewKeyDown += OnPreviewKeyDown;
            box.GotFocus += (_, _) => _setHint("按下想要的组合键（需至少一个修饰键）");
        }

        /// <summary>初始化为给定组合（对话框打开时同步当前生效值；hasValid=false 表示未启用/未捕获）。</summary>
        public void Reset(HotkeyModifierKeys mods, int key, bool hasValid)
        {
            Mods = mods;
            VirtualKey = key;
            HasValid = hasValid;
            _box.Text = hasValid ? HotkeyFormatter.Format(mods, key) : string.Empty;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
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
                _setHint("请配合修饰键（Alt/Ctrl/Shift/Win）一起按下");
                HasValid = false;
                return;
            }

            Mods = mods;
            VirtualKey = KeyInterop.VirtualKeyFromKey(key);
            HasValid = true;
            _box.Text = HotkeyFormatter.Format(Mods, VirtualKey);
            _setHint("点击「确定」立即生效");
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
    }

    // ---------- 按钮 ----------
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DoApply()) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 所有改动仅在「确定」时应用；取消即丢弃，无需还原
        DialogResult = false;
    }

    /// <summary>应用当前捕获。返回是否成功（含禁用态：禁用视为成功）。</summary>
    private bool DoApply()
    {
        bool enabled = EnableCheckBox.IsChecked == true;
        if (enabled && !_mainCapture.HasValid)
        {
            HintBlock.Text = "请先捕获一个有效的快捷键组合";
            return false;
        }
        bool snippetEnabled = SnippetEnableCheckBox.IsChecked == true;
        if (snippetEnabled && !_snippetCapture.HasValid)
        {
            SnippetHintBlock.Text = "请先捕获一个有效的快捷键组合";
            return false;
        }

        uint mods = enabled ? (uint)_mainCapture.Mods : 0;
        int key = enabled ? _mainCapture.VirtualKey : 0;

        var (ok, error) = _hotkey.UpdateSettings(enabled, mods, key);
        if (!ok)
        {
            MessageBox.Show(this, error, "快捷键注册失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            HintBlock.Text = error;
            return false;
        }

        HintBlock.Text = "已生效" + (enabled ? $"（{HotkeyFormatter.Format(_mainCapture.Mods, _mainCapture.VirtualKey)}）" : "（已禁用）");

        // 应用片段速取热键（独立持久化；失败提示并保持对话框打开，主热键已生效不受影响）
        var (snipOk, snipError) = _hotkey.UpdateSnippetHotkey(snippetEnabled,
            snippetEnabled ? (uint)_snippetCapture.Mods : 0,
            snippetEnabled ? _snippetCapture.VirtualKey : 0);
        if (!snipOk)
        {
            MessageBox.Show(this, snipError, "快捷键注册失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SnippetHintBlock.Text = snipError;
            return false;
        }
        SnippetHintBlock.Text = "已生效" + (snippetEnabled
            ? $"（{HotkeyFormatter.Format(_snippetCapture.Mods, _snippetCapture.VirtualKey)}）"
            : "（已禁用）");


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
        // 队列容量校验：解析失败或越界（5–200）时提示并保持对话框打开，避免静默改值
        if (!int.TryParse(QueueCapacityTextBox.Text, out int queueCapacity)
            || queueCapacity < MultiCopy.State.AppState.MinQueueItems
            || queueCapacity > MultiCopy.State.AppState.MaxQueueItemsLimit)
        {
            MessageBox.Show(this,
                $"队列容量需在 {MultiCopy.State.AppState.MinQueueItems}–{MultiCopy.State.AppState.MaxQueueItemsLimit} 之间。",
                "队列容量无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            QueueCapacityTextBox.Focus();
            return false;
        }

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
        if (currentSettings.MaxQueueItems != queueCapacity)
        {
            currentSettings.MaxQueueItems = queueCapacity;
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
        state.MaxQueueItems = queueCapacity; // Setter 内夹取范围，变化时经事件刷新主界面 n/N
        // 起始序号变更时同步重置当前序号
        if (state.PrefixSequenceCurrent < sequenceStart)
            state.PrefixSequenceCurrent = sequenceStart;
        return true;
    }
}
