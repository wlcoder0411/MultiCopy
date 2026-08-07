using MultiCopy.State;

namespace MultiCopy.Infrastructure;

/// <summary>
/// 序号格式化工具：将数字按所选格式转为字符串。
/// </summary>
public static class SequenceFormatter
{
    /// <summary>按格式将序号转为字符串。</summary>
    public static string Format(int number, PrefixSequenceFormat format) => format switch
    {
        PrefixSequenceFormat.Plain => number.ToString(),
        PrefixSequenceFormat.ZeroPadded => number.ToString("D2"),  // 01, 02...
        PrefixSequenceFormat.Parenthesized => $"({number})",
        PrefixSequenceFormat.Dotted => $"{number}.",
        PrefixSequenceFormat.Dunhao => $"{number}、",
        _ => number.ToString(),
    };
}
