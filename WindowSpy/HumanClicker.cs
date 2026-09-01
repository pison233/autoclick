using System;
using System.Drawing;
using System.Threading;

namespace WindowSpy
{
    /// <summary>
    /// 人类化点击时序：生成接近真人的反应时间、点击节奏与落点抖动。
    /// 设计依据见仓库根目录 DESIGN.md。
    /// </summary>
    public static class HumanClicker
    {
        private static readonly Random Rng = new Random();

        private static int _burstMin = 3;
        private static int _burstMax = 5;
        private static double _jitterChance = 0.12;

        private static double _tempo = 1.0;                 // 会话级速度漂移
        private static DateTime _nextTempoShift = DateTime.MinValue;
        private static double _reactionMem = 0;             // AR(1) 记忆

        // ---------- 基础采样 ----------

        private static double Gaussian()
        {
            // Box-Muller
            double u1 = 1.0 - Rng.NextDouble();
            double u2 = Rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        // 对数正态采样，返回毫秒。meanMs 为 ln 域的均值中心。
        private static double LogNormalMs(double meanMs, double sigma)
        {
            return Math.Exp(Math.Log(meanMs) + sigma * Gaussian());
        }

        // 均值每 60~120s 随机乘 0.85~1.15，clamp [0.8, 1.2]，模拟"今天状态不同"
        private static void UpdateTempo()
        {
            if (DateTime.Now < _nextTempoShift) return;
            _tempo = Math.Clamp(_tempo * (0.85 + Rng.NextDouble() * 0.30), 0.8, 1.2);
            _nextTempoShift = DateTime.Now.AddSeconds(60 + Rng.NextDouble() * 60);
        }

        // ---------- 人类化时序 ----------

        /// <summary>识别到目标 → 手指开始动。均值~200ms，下限 120ms，AR(1) 自相关避免白噪声。</summary>
        public static int ReactionDelayMs()
        {
            UpdateTempo();
            double raw = LogNormalMs(200, 0.35) * _tempo;
            _reactionMem = 0.3 * _reactionMem + 0.7 * raw;
            return (int)Math.Max(120, _reactionMem);
        }

        /// <summary>单次按下+抬起。瞬间点击，均值 50ms，下限 30ms。</summary>
        public static int DwellMs()
        {
            return (int)Math.Max(30, LogNormalMs(50, 0.3));
        }

        /// <summary>组内连点间隔：无疲劳，仅细微抖动。均值~200ms，下限 110ms。</summary>
        public static int InterClickMs()
        {
            UpdateTempo();
            return (int)Math.Max(110, LogNormalMs(200, 0.25) * _tempo);
        }

        /// <summary>一组点击次数（默认 3~5，可用 SetBurstRange 配置）。</summary>
        public static int BurstCount()
        {
            return Rng.Next(_burstMin, _burstMax + 1);
        }

        /// <summary>
        /// 点击落点抖动：默认 ~12% 概率才抖，且只 ±1px（70%）/ ±2px（30%）。
        /// 大多数点击完全不动——对应高频连点几乎不抖、难得才小抖一下。
        /// </summary>
        public static (int dx, int dy) ClickJitter()
        {
            if (Rng.NextDouble() >= _jitterChance) return (0, 0);
            int maxPx = Rng.NextDouble() < 0.7 ? 1 : 2;
            return (Rng.Next(-maxPx, maxPx + 1), Rng.Next(-maxPx, maxPx + 1));
        }

        /// <summary>
        /// 扫描间隔：多数立即进入下一轮（由 OCR 推理耗时自然撑起），
        /// 6% 概率走神 3~8s（对应偶尔挪开视线）。
        /// </summary>
        public static int NextScanWaitMs()
        {
            return Rng.NextDouble() < 0.06 ? (int)LogNormalMs(4500, 0.4) : 0;
        }

        // ---------- 动作 ----------

        /// <summary>
        /// 执行一组爆发点击。center 必须是屏幕坐标（= 窗口左上角 + 按钮相对位置）。
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
        /// 每 2~3 组购买后触发一次。目前仅停顿模拟"查看交易记录"。
        /// TODO 预留：移到"交易记录"→ 点击 → 停顿查看 → 返回购买界面（见 DESIGN.md 后续扩展）。
        /// </summary>
        public static void MaybeCheckRecords(int groupCount)
        {
            _groupsSinceCheck += groupCount;
            if (_groupsSinceCheck < Rng.Next(2, 4)) return;
            _groupsSinceCheck = 0;
            Thread.Sleep((int)LogNormalMs(4500, 0.35)); // 3~6s
        }
        private static int _groupsSinceCheck = 0;

        // ---------- 配置 ----------

        /// <summary>配置一组点击次数范围。默认 3~5。</summary>
        public static void SetBurstRange(int min, int max)
        {
            if (min >= 1 && max >= min) { _burstMin = min; _burstMax = max; }
        }

        /// <summary>配置落点抖动概率（0~1）。默认 0.12。</summary>
        public static void SetJitterChance(double chance)
        {
            _jitterChance = Math.Clamp(chance, 0, 1);
        }
    }
}
