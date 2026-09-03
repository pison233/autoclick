using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;

namespace WindowSpy
{
    // 流程节点类型：点击 / 检测(if) / 否则 / 结束 / 循环(开始) / 循环结束 / 跳转
    public enum QuickNodeType { Click, If, Else, End, LoopStart, LoopEnd, Jump }

    // 单条子条件种类（同一检测区域上判断）
    public enum CheckConditionKind { HasContent, IsEmpty, NumCompare, TextMatch }

    public enum NumCompareOp { GreaterOrEqual, LessOrEqual, Equal, Greater, Less }

    public enum TextMatchOp { Equal, Contains }

    // 连接词：首条固定为“当”，其后每条约“且/或”
    public enum ConjType { And, Or }

    // 检测(if)触发方式：每次状态变化触发一次（出现→执行真分支，消失→执行假分支）/ 每轮都按当前状态执行
    public enum CheckTriggerMode { OncePerAppearance, EveryRound }

    /// <summary>检测(If)节点内的一条子条件。</summary>
    public class FlowCondition
    {
        public ConjType Conj = ConjType.And;      // 仅非首条生效
        public CheckConditionKind Kind = CheckConditionKind.HasContent;
        public NumCompareOp NumOp;
        public int NumThreshold;
        public TextMatchOp TextOp = TextMatchOp.Contains;
        public string TextValue = "";

        public FlowCondition Clone()
        {
            return new FlowCondition
            {
                Conj = Conj, Kind = Kind, NumOp = NumOp, NumThreshold = NumThreshold,
                TextOp = TextOp, TextValue = TextValue,
            };
        }
    }

    /// <summary>流程节点。坐标均为窗口相对坐标。</summary>
    public class QuickFlowNode
    {
        public QuickNodeType Type;
        public TargetType Target = TargetType.A;   // 点击/检测 所在窗口

        // --- 点击节点 ---
        public Point Point;
        public int RepeatMin = 1;
        public int RepeatMax = 1;
        // 精细时序（高级设置选“精细延迟”时生效；人类化时忽略）
        public int DelayMs = 300;
        public int RandomDelay = 0;
        public int DwellMs = 100;
        public int RandomDwell = 0;
        public int RandomX = 0;
        public int RandomY = 0;

        // --- 检测(if)节点 ---
        public Rectangle Rect;                                    // 检测区域
        public List<FlowCondition> Conditions = new();            // 同区域的多条子条件
        public CheckTriggerMode TriggerMode = CheckTriggerMode.EveryRound;   // 默认每轮判断(标准 if/else)
        public bool StopWhenTrue;                                 // 满足即停（停止信号）

        // --- 循环(开始) / 跳转 ---
        public int LoopCount = 3;                                 // LoopStart 内循环次数
        public int JumpTarget = -1;                               // Jump 目标行（0 基下标，-1=未设）

        public QuickFlowNode Clone()
        {
            var c = new QuickFlowNode
            {
                Type = Type,
                Target = Target,
                Point = Point,
                RepeatMin = RepeatMin,
                RepeatMax = RepeatMax,
                DelayMs = DelayMs,
                RandomDelay = RandomDelay,
                DwellMs = DwellMs,
                RandomDwell = RandomDwell,
                RandomX = RandomX,
                RandomY = RandomY,
                Rect = Rect,
                TriggerMode = TriggerMode,
                StopWhenTrue = StopWhenTrue,
                LoopCount = LoopCount,
                JumpTarget = JumpTarget,
            };
            foreach (var f in Conditions) c.Conditions.Add(f.Clone());
            return c;
        }
    }

    /// <summary>条件求值（纯逻辑，可单测）。传入 OCR 全文本。</summary>
    public static class QuickFlowEval
    {
        public static bool HasContent(string s) => !string.IsNullOrWhiteSpace(s);
        public static bool IsEmpty(string s) => string.IsNullOrWhiteSpace(s);

        /// <summary>单条子条件是否满足。</summary>
        public static bool CondMatches(FlowCondition c, string text)
        {
            switch (c.Kind)
            {
                case CheckConditionKind.HasContent: return HasContent(text);
                case CheckConditionKind.IsEmpty: return IsEmpty(text);
                case CheckConditionKind.NumCompare: return NumCompare(text, c.NumOp, c.NumThreshold);
                case CheckConditionKind.TextMatch: return TextMatch(text, c.TextOp, c.TextValue);
                default: return false;
            }
        }

        /// <summary>按“且/或”连接词顺序求值整组子条件。</summary>
        public static bool EvaluateConditions(IList<FlowCondition> conds, string ocrText)
        {
            if (conds == null || conds.Count == 0) return false;
            bool r = CondMatches(conds[0], ocrText);
            for (int i = 1; i < conds.Count; i++)
            {
                bool m = CondMatches(conds[i], ocrText);
                r = conds[i].Conj == ConjType.Or ? (r || m) : (r && m);
            }
            return r;
        }

        /// <summary>从 OCR 文本取一个数字：挑含数字位最多的候选（忽略逗号分隔符）。无数字返回 null。</summary>
        public static long? ExtractFirstNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string best = "";
            int bestDigits = -1;
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsDigit(text[i]) || text[i] == ',')
                {
                    int j = i;
                    while (j < text.Length && (char.IsDigit(text[j]) || text[j] == ',')) j++;
                    string cand = new string(text.Substring(i, j - i).Where(ch => ch != ',').ToArray());
                    if (cand.Length > 0)
                    {
                        int digitCount = cand.Count(char.IsDigit);
                        if (digitCount > bestDigits) { best = cand; bestDigits = digitCount; }
                    }
                    i = j;
                }
                else i++;
            }
            if (best.Length == 0) return null;
            return long.TryParse(best, out long v) ? v : (long?)null;
        }

        public static bool NumCompare(string text, NumCompareOp op, int threshold)
        {
            long? n = ExtractFirstNumber(text);
            if (n == null) return false;
            return op switch
            {
                NumCompareOp.GreaterOrEqual => n >= threshold,
                NumCompareOp.LessOrEqual => n <= threshold,
                NumCompareOp.Equal => n == threshold,
                NumCompareOp.Greater => n > threshold,
                NumCompareOp.Less => n < threshold,
                _ => false,
            };
        }

        public static bool TextMatch(string text, TextMatchOp op, string target)
        {
            string t = target?.Trim() ?? "";
            if (t.Length == 0) return false;
            string src = text ?? "";
            return op switch
            {
                TextMatchOp.Equal => string.Equals(src.Trim(), t, StringComparison.OrdinalIgnoreCase),
                TextMatchOp.Contains => src.Contains(t, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        // ---- 显示辅助 ----

        public static string OpText(NumCompareOp op) => op switch
        {
            NumCompareOp.GreaterOrEqual => "≥",
            NumCompareOp.LessOrEqual => "≤",
            NumCompareOp.Equal => "=",
            NumCompareOp.Greater => ">",
            NumCompareOp.Less => "<",
            _ => "?",
        };

        public static string KindText(CheckConditionKind k) => k switch
        {
            CheckConditionKind.HasContent => "区域出现文字",
            CheckConditionKind.IsEmpty => "区域为空",
            CheckConditionKind.NumCompare => "数字比较",
            CheckConditionKind.TextMatch => "文本匹配",
            _ => "?",
        };

        /// <summary>单条子条件的中文（不含连接词）。</summary>
        public static string DescribeCond(FlowCondition c)
        {
            switch (c.Kind)
            {
                case CheckConditionKind.HasContent: return "有文字";
                case CheckConditionKind.IsEmpty: return "为空";
                case CheckConditionKind.NumCompare: return $"数字{OpText(c.NumOp)}{c.NumThreshold}";
                case CheckConditionKind.TextMatch:
                    return c.TextOp == TextMatchOp.Equal ? $"文字={c.TextValue}" : $"文字含“{c.TextValue}”";
                default: return "";
            }
        }

        /// <summary>整组条件中文，第一条前不加连接词，其后加“且/或”。</summary>
        public static string DescribeConditions(IList<FlowCondition> conds)
        {
            if (conds == null || conds.Count == 0) return "无条件";
            var sb = new StringBuilder();
            sb.Append(DescribeCond(conds[0]));
            for (int i = 1; i < conds.Count; i++)
            {
                sb.Append(conds[i].Conj == ConjType.Or ? " 或 " : " 且 ");
                sb.Append(DescribeCond(conds[i]));
            }
            return sb.ToString();
        }
    }

    // ---------- 序列化 DTO（System.Text.Json，public 属性） ----------

    public class QuickFlowCondDto
    {
        public string Conj { get; set; } = "And";
        public string Kind { get; set; } = "HasContent";
        public string CondOp { get; set; } = "";      // NumOp 或 TextOp 枚举名
        public string CondValue { get; set; } = "";   // 数字阈值或目标文字
    }

    public class QuickFlowNodeDto
    {
        public string Type { get; set; } = "Click";   // "Click"|"If"|"Else"|"End"|"LoopStart"|"LoopEnd"|"Jump"
        public string Target { get; set; } = "A";
        public int PointX { get; set; }
        public int PointY { get; set; }
        public int RepeatMin { get; set; } = 1;
        public int RepeatMax { get; set; } = 1;
        public int DelayMs { get; set; } = 300;
        public int RandomDelay { get; set; }
        public int DwellMs { get; set; } = 100;
        public int RandomDwell { get; set; }
        public int RandomX { get; set; }
        public int RandomY { get; set; }
        public int RectX { get; set; }
        public int RectY { get; set; }
        public int RectW { get; set; }
        public int RectH { get; set; }
        public string TriggerMode { get; set; } = "EveryRound";
        public bool StopWhenTrue { get; set; }
        public int LoopCount { get; set; } = 3;
        public int JumpTarget { get; set; } = -1;
        public List<QuickFlowCondDto> Conditions { get; set; } = new();
    }

    public class QuickFlowStopDto
    {
        public bool UseClicks { get; set; }
        public int Clicks { get; set; }
        public bool UseTriggers { get; set; }
        public int Triggers { get; set; }
        public bool UseRounds { get; set; }
        public int Rounds { get; set; }
        public bool UseMinutes { get; set; }
        public double Minutes { get; set; }
    }

    public class QuickFlowFileDto
    {
        public string Note { get; set; } = "";
        public List<QuickFlowNodeDto> Nodes { get; set; } = new();
        public QuickFlowStopDto Stop { get; set; } = new();
    }

    // DTO <-> 模型 转换（UI 与保存共用）
    public static class QuickFlowMapper
    {
        public static QuickFlowCondDto CondToDto(FlowCondition c)
        {
            return new QuickFlowCondDto
            {
                Conj = c.Conj == ConjType.Or ? "Or" : "And",
                Kind = c.Kind.ToString(),
                CondOp = c.Kind == CheckConditionKind.NumCompare ? c.NumOp.ToString()
                       : c.Kind == CheckConditionKind.TextMatch ? c.TextOp.ToString() : "",
                CondValue = c.Kind == CheckConditionKind.NumCompare ? c.NumThreshold.ToString()
                          : c.Kind == CheckConditionKind.TextMatch ? c.TextValue : "",
            };
        }

        public static FlowCondition CondFromDto(QuickFlowCondDto d)
        {
            var c = new FlowCondition();
            if (Enum.TryParse<ConjType>(d.Conj, true, out var cj)) c.Conj = cj;
            if (Enum.TryParse<CheckConditionKind>(d.Kind, true, out var k)) c.Kind = k;
            if (c.Kind == CheckConditionKind.NumCompare)
            {
                if (Enum.TryParse<NumCompareOp>(d.CondOp, true, out var op)) c.NumOp = op;
                int.TryParse(d.CondValue, out var th); c.NumThreshold = th;
            }
            else if (c.Kind == CheckConditionKind.TextMatch)
            {
                if (Enum.TryParse<TextMatchOp>(d.CondOp, true, out var top)) c.TextOp = top;
                c.TextValue = d.CondValue ?? "";
            }
            return c;
        }

        public static QuickFlowNodeDto ToDto(QuickFlowNode n)
        {
            var d = new QuickFlowNodeDto
            {
                Type = n.Type.ToString(),
                Target = n.Target == TargetType.A ? "A" : "B",
                PointX = n.Point.X,
                PointY = n.Point.Y,
                RepeatMin = n.RepeatMin,
                RepeatMax = n.RepeatMax,
                DelayMs = n.DelayMs,
                RandomDelay = n.RandomDelay,
                DwellMs = n.DwellMs,
                RandomDwell = n.RandomDwell,
                RandomX = n.RandomX,
                RandomY = n.RandomY,
                RectX = n.Rect.X,
                RectY = n.Rect.Y,
                RectW = n.Rect.Width,
                RectH = n.Rect.Height,
                TriggerMode = n.TriggerMode.ToString(),
                StopWhenTrue = n.StopWhenTrue,
                LoopCount = n.LoopCount,
                JumpTarget = n.JumpTarget,
            };
            foreach (var c in n.Conditions) d.Conditions.Add(CondToDto(c));
            return d;
        }

        public static QuickFlowNode FromDto(QuickFlowNodeDto d)
        {
            var n = new QuickFlowNode
            {
                Type = Enum.TryParse<QuickNodeType>(d.Type, true, out var t) ? t : QuickNodeType.Click,
                Target = string.Equals(d.Target, "B", StringComparison.OrdinalIgnoreCase) ? TargetType.B : TargetType.A,
                Point = new Point(d.PointX, d.PointY),
                Rect = new Rectangle(d.RectX, d.RectY, d.RectW, d.RectH),
            };
            n.RepeatMin = Math.Max(1, d.RepeatMin);
            n.RepeatMax = Math.Max(n.RepeatMin, d.RepeatMax);
            n.DelayMs = Math.Max(0, d.DelayMs);
            n.RandomDelay = Math.Max(0, d.RandomDelay);
            n.DwellMs = Math.Max(0, d.DwellMs);
            n.RandomDwell = Math.Max(0, d.RandomDwell);
            n.RandomX = Math.Max(0, d.RandomX);
            n.RandomY = Math.Max(0, d.RandomY);
            if (Enum.TryParse<CheckTriggerMode>(d.TriggerMode, true, out var tm)) n.TriggerMode = tm;
            n.StopWhenTrue = d.StopWhenTrue;
            n.LoopCount = Math.Max(1, d.LoopCount);
            n.JumpTarget = d.JumpTarget;
            if (d.Conditions != null)
                foreach (var c in d.Conditions) n.Conditions.Add(CondFromDto(c));
            return n;
        }
    }
}
