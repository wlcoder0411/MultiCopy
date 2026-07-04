using System.Runtime.InteropServices;
using MultiCopy.Models;
using MultiCopy.Native;
using MultiCopy.State;

namespace MultiCopy.Services;

/// <summary>
/// 点选粘贴执行器：把指定项写入剪贴板，再用 SendInput 模拟 Ctrl+V 粘贴到当前焦点窗口。
/// 因主窗口 WS_EX_NOACTIVATE 不抢焦点，目标应用始终保有焦点，模拟按键直达。
/// keepAfterPaste=true（置顶项）粘贴后保留；false（普通项）从队列移除。
/// </summary>
public sealed class PasteExecutor
{
    private readonly ClipboardService _clipboard;
    private readonly AppState _state = AppState.Instance;

    public PasteExecutor(ClipboardService clipboard)
    {
        _clipboard = clipboard;
    }

    public void Execute(ClipboardItem item, bool keepAfterPaste, ClipboardQueue queue)
    {
        bool writeOk;
        switch (item)
        {
            case TextClipboardItem textItem:
                // 文本为空直接返回（目标应用会粘贴当前剪贴板，降级为普通粘贴）
                if (string.IsNullOrEmpty(textItem.Text)) return;
                writeOk = _clipboard.SetClipboardTextSync(textItem.Text);
                // 文本写失败：直接返回（目标应用粘贴当前剪贴板，降级为普通粘贴）
                if (!writeOk) return;
                break;

            case ImageClipboardItem imgItem:
                writeOk = _clipboard.SetClipboardImageSync(imgItem.Image);
                // 图片写失败：清空剪贴板再返回（避免粘出上一条文本，明确告知失败）
                if (!writeOk)
                {
                    _clipboard.ClearClipboard();
                    return;
                }
                break;

            default:
                return;
        }

        _state.IsSimulatingPaste = true;
        try
        {
            SendCtrlV();
        }
        finally
        {
            // SendInput 同步触发低级钩子（钩子在注入时被调用），返回后即可清位
            _state.IsSimulatingPaste = false;
        }

        if (!keepAfterPaste)
        {
            queue.RemoveNormal(item);
        }
    }

    /// <summary>
    /// 预构建的 Ctrl+V 输入序列（Ctrl down → V down → V up → Ctrl up）。
    /// 内容固定不变，静态化避免每次点选粘贴重复分配 INPUT[4]（160B）。
    /// SendInput 只读消费此数组，不会修改。
    /// </summary>
    private static readonly INPUT[] _ctrlVInputs = BuildCtrlVInputs();

    private static INPUT[] BuildCtrlVInputs()
    {
        var inputs = new INPUT[4];
        // Ctrl 按下
        inputs[0].Type = Constants.INPUT_KEYBOARD;
        inputs[0].U.Ki.WVk = (ushort)Constants.VK_CONTROL;
        // V 按下
        inputs[1].Type = Constants.INPUT_KEYBOARD;
        inputs[1].U.Ki.WVk = (ushort)Constants.VK_V;
        // V 抬起
        inputs[2].Type = Constants.INPUT_KEYBOARD;
        inputs[2].U.Ki.WVk = (ushort)Constants.VK_V;
        inputs[2].U.Ki.DwFlags = Constants.KEYEVENTF_KEYUP;
        // Ctrl 抬起
        inputs[3].Type = Constants.INPUT_KEYBOARD;
        inputs[3].U.Ki.WVk = (ushort)Constants.VK_CONTROL;
        inputs[3].U.Ki.DwFlags = Constants.KEYEVENTF_KEYUP;
        return inputs;
    }

    private static void SendCtrlV()
        => Win32.SendInput((uint)_ctrlVInputs.Length, _ctrlVInputs, INPUT.Size);
}
