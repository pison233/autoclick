using System;
using System.Drawing;
using System.Threading;

namespace WindowSpy
{
    /// <summary>
    /// 人类化时序全部可配置参数。默认值为按真人操作调优的基准。
    /// sigma 为 ln 域对数正态的波动宽度，属"分布形状"，UI 不暴露、保持调优默认。
    /// </summary>
    public class HumanTimingParams
    {
        // 反应时间：识别到目标 → 手指开始动
        public double ReactionMeanMs = 200;
        public double ReactionSigma = 0.35;
        public int ReactionFloorMs = 120;

        // 单次按下+抬起（瞬间点击）
        public double DwellMeanMs = 50;
        public double DwellSigma = 0.3;
        public int DwellFloorMs = 30;

        // 组内连点间隔（无疲劳，仅细微抖动）
        public double InterClickMeanMs = 200;
        public double InterClickSigma = 0.25;
        public int InterClickFloorMs = 110;

        // 一组点击次数
        public int BurstMin = 3;
        public int BurstMax = 5;

        // 落点抖动：偶发 + 小幅度
        public double JitterChance = 0.12;
        public double JitterSmallProb = 0.7;   // 小幅度 vs 较大幅度的概率
        public int JitterSmallPx = 1;
        public int JitterLargePx = 2;

        // 走神扫描间隔
        public double DistractChance = 0.06;
        public double DistractMeanMs = 4500;
        public double DistractSigma = 0.4;

        // 核查停顿（查看交易记录）
        public int CheckMinGroups = 2;
        public int CheckMaxGroups = 3;
        public double CheckPauseMeanMs = 4500;
        public double CheckPauseSigma = 0.35;

        // 会话级速度漂移
        public double TempoMin = 0.8;
        public double TempoMax = 1.2;
        public int TempoMinIntervalSec = 60;
        public int TempoMaxIntervalSec = 120;

        public HumanTimingParams Clone() => (HumanTimingParams)MemberwiseClone();
    }

    /// <summary>
    /// 人类化点击时序：生成接近真人的反应时间、点击节奏与落点抖动。
    /// 设计依据见仓库根目录 DESIGN.md。所有参数可用 Configure(HumanTimingParams) 覆盖。
    /// </summary>
    public static class HumanClicker
    {
        private static readonly Random Rng = new Random();
        private static HumanTimingParams P = new HumanTimingParams();
        private static double _tempo = 1.0;
        private static DateTime _nextTempoShift = DateTime.MinValue;
        private static double _reactionMem = 0;
        private static int _groupsSinceCheck = 0;

        /// <summary>用给定参数整体替换时序配置。传 null 则恢复默认。</summary>
        public static void Configure(HumanTimingParams? p)
        {
            P = p?.Clone() ?? new HumanTimingParams();
        }

        // ---------- 基础采样 ----------

        private static double Gaussian()
        {
            double u1 = 1.0 - Rng.NextDouble();
            double u2 = Rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        private static double LogNormalMs(double meanMs, double sigma)
        {
            return Math.Exp(Math.Log(meanMs) + sigma * Gaussian());
        }

        private static void UpdateTempo()
        {
            if (DateTime.Now < _nextTempoShift) return;
            double range = P.TempoMax - P.TempoMin;
            _tempo = Math.Clamp(P.TempoMin + Rng.NextDouble() * range, P.TempoMin, P.TempoMax);
            _nextTempoShift = DateTime.Now.AddSeconds(
                P.TempoMinIntervalSec + Rng.NextDouble() * (P.TempoMaxIntervalSec - P.TempoMinIntervalSec));
        }

        // ---------- 人类化时序 ----------

        /// <summary>识别到目标 → 手指开始动。AR(1) 自相关避免白噪声。</summary>
        public static int ReactionDelayMs()
        {
            UpdateTempo();
            double raw = LogNormalMs(P.ReactionMeanMs, P.ReactionSigma) * _tempo;
            _reactionMem = 0.3 * _reactionMem + 0.7 * raw;
            return (int)Math.Max(P.ReactionFloorMs, _reactionMem);
        }

        /// <summary>单次按下+抬起。瞬间点击。</summary>
        public static int DwellMs()
        {
            return (int)Math.Max(P.DwellFloorMs, LogNormalMs(P.DwellMeanMs, P.DwellSigma));
        }

        /// <summary>组内连点间隔：无疲劳，仅细微抖动。</summary>
        public static int InterClickMs()
        {
            UpdateTempo();
            return (int)Math.Max(P.InterClickFloorMs, LogNormalMs(P.InterClickMeanMs, P.InterClickSigma) * _tempo);
        }

        /// <summary>一组点击次数（BurstMin ~ BurstMax）。</summary>
        public static int BurstCount()
        {
            return Rng.Next(P.BurstMin, P.BurstMax + 1);
        }

        /// <summary>
        /// 点击落点抖动：默认 ~12% 概率才抖，且小幅度为主。
        /// 大多数点击完全不动——对应高频连点几乎不抖、难得才小抖一下。
        /// </summary>
        public static (int dx, int dy) ClickJitter()
        {
            if (Rng.NextDouble() >= P.JitterChance) return (0, 0);
            int maxPx = Rng.NextDouble() < P.JitterSmallProb ? P.JitterSmallPx : P.JitterLargePx;
            return (Rng.Next(-maxPx, maxPx + 1), Rng.Next(-maxPx, maxPx + 1));
        }

        /// <summary>
        /// 扫描间隔：多数立即进入下一轮（由 OCR 推理耗时自然撑起），
        /// 小概率走神（对应偶尔挪开视线）。
        /// </summary>
        public static int NextScanWaitMs()
        {
            return Rng.NextDouble() < P.DistractChance ? (int)LogNormalMs(P.DistractMeanMs, P.DistractSigma) : 0;
        }

        // ---------- 动作 ----------

        /// <summary>
        /// 执行一组爆发点击。center 必须是操作窗口的屏幕坐标（= 窗口左上角 + 相对位置）。
        /// 返回实际点击次数（用于"买满停止"计数）。
        /// </summary>
        public static int ClickBurst(IntPtr hwnd, Point center)
        {
            int n = BurstCount();
            for (int i = 0; i < n; i++)
            {
                var (dx, dy) = ClickJitter();
                if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, 9);
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.ClickAtScreen(center.X + dx, center.Y + dy, DwellMs());
                if (i < n - 1) Thread.Sleep(InterClickMs());
            }
            return n;
        }

        /// <summary>
        /// 每 CheckMin~CheckMax 组触发一次核查停顿（模拟查看记录）。
        /// TODO 预留：移到"交易记录"→ 点击 → 停顿查看 → 返回（见 DESIGN.md 后续扩展）。
        /// </summary>
        public static void MaybeCheckRecords(int groupCount)
        {
            _groupsSinceCheck += groupCount;
            bool trigger;
            if (_groupsSinceCheck < P.CheckMinGroups)
                trigger = false;                      // 未到最小间隔
            else if (_groupsSinceCheck >= P.CheckMaxGroups)
                trigger = true;                       // 到最大间隔硬触发
            else
                trigger = Rng.NextDouble() > 0.5;     // 之间随机
            if (!trigger) return;
            _groupsSinceCheck = 0;
            Thread.Sleep((int)LogNormalMs(P.CheckPauseMeanMs, P.CheckPauseSigma));
        }

        /// <summary>配置一组点击次数范围。默认 3~5。</summary>
        public static void SetBurstRange(int min, int max)
        {
            if (min >= 1 && max >= min) { P.BurstMin = min; P.BurstMax = max; }
        }
    }
}
