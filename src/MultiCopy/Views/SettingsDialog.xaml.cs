using System.Windows;
using System.Windows.Input;
using MultiCopy.Infrastructure;
using MultiCopy.Native;
using MultiCopy.Services;

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

        UpdateCaptureEnabled();
        Loaded += (_, _) => { if (EnableCheckBox.IsChecked == true) CaptureBox.Focus(); };
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
        ApplyButton.IsEnabled = en;
        OkButton.IsEnabled = en;
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
    private void Apply_Click(object sender, RoutedEventArgs e) => DoApply();

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
        return true;
    }
}
