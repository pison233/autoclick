# MoliGod 自动化助手 — 人类化自动化设计文档

> 本设计文档与代码一同维护，作为换机继续开发的设计依据。配套实现见 `WindowSpy/HumanClicker.cs`（人类化时序工具类）与 `MainWindow` 的"自动购买"页。

---

## 一、项目定位

Windows 桌面自动化工具（.NET 8 WPF + OpenCvSharp + PaddleOCR/ONNX Runtime）。核心能力：

- 窗口绑定（A/B 双窗口）
- OCR 文字识别（PaddleOCR，支持 DirectML GPU 加速，Python 子进程通信）
- 可视化步骤编排（点击 / OCR / 条件 / 循环 / 跳转 / 表达式）
- 人类化自动化时序（"自动购买"页）

## 二、设计哲学（核心思路）

根本目标：**让自动化的操作节奏在统计上与真人无法显著区分**。以下原则直接决定实现参数。

**原则 1：高熵 ≠ 人性**
机器最明显的特征不是"不够随机"，而是"过于随机"——均匀分布、相互独立、无关联。人类行为变异是**低熵、有结构**的：有峰、有自相关、有漂移、有状态依赖。实现上所有时间采样用**对数正态分布**，禁止均匀分布。

**原则 2：分布形状 —— 对数正态**
人类反应时间/动作间隔近似对数正态：`x = exp(ln(mean) + σ·Gaussian())`。有峰、有重尾、有生理下限。

**原则 3：自相关（AR(1)）**
人类相邻两次动作间隔相关（连着快几下会缓一缓）。纯 iid 白噪声是机器指纹。实现：`d(t) = 0.3·d(t-1) + 0.7·raw`。

**原则 4：会话级漂移**
参数本身随时间慢漂移（均值每 60~120s 随机乘 0.85~1.15），模拟"今天状态不同"。

**原则 5：硬下限**
人类感知→决策→动作有不可压缩的生理延迟：反应时间下限 120ms、点击间隔下限 110ms、dwell 下限 30ms。

**原则 6：外部驱动的时间不要建模**
组与组之间的等待由市场/补货驱动（外部世界产生，天然不规则、非平稳），**不要用随机数生成器模拟**——这是自动化的天然优势。

**原则 7：OCR 延迟是盟友，不是 bug**
推理耗时不稳定（数百 ms 波动）本身就是人类反应波动的一部分。把反应时间模型建在它之上，而不是消除它。

**原则 8：状态条件化的边界**
人类动作是游戏状态/屏幕内容的函数。本地随机生成器无法做真状态条件化——但若自动化的信息源对服务器不可见（如外部网站数据），则服务器也无法做完整条件检测。承认边界，不硬做。

**原则 9：结构性无解项 —— 无限逼近**
以下无法在本地解决，只能"无限逼近、接受残余风险"：
- **检测器黑盒**：不知道对方算什么特征、什么阈值。
- **群体参照系**：速率/落点放在全服百分位里，仍可能离群。
- **零失误经济**：真人交易有失败/亏损/次优决策，自动化恒为最优——无法伪造"真实失误"。
- **蜜罐**：服务器主动埋的诱饵，无从预知。
- **人格一致性**：跨会话变化过多反而暴露"无稳定个体特征"（白噪声人格）。

目标定位是"在统计维度上做到最强，并明确残余风险"，不是"保证不被检测"。

## 三、安全机制决策（保留，不删）

- **防卡死（5 秒内右键 10 次）**：系统级鼠标钩子 `WH_MOUSE_LL`，纯被动监听，**自己不产生点击、不保活**，是"鼠标通道"的急停开关。只在脚本运行时安装、结束即卸载。
- **F12 全局热键（RegisterHotKey）**：标准系统热键，仅运行时注册，"键盘通道"的急停开关。
- 两者是**双通道冗余急停**：键盘一个通道、鼠标一个通道，哪个够得着用哪个。互不替代。

---

## 四、当前实现

### 4.1 `WindowSpy/HumanClicker.cs`（人类化时序工具类）

```csharp
public static class HumanClicker
{
    // 基础采样（私有）
    private static double Gaussian();            // Box-Muller 标准正态
    private static double LogNormalMs(double meanMs, double sigma);
    private static void UpdateTempo();           // 60~120s 随机乘 0.85~1.15，clamp [0.8, 1.2]

    // 人类化时序（公开）
    public static int ReactionDelayMs();         // 均值~200ms, 下限120, AR(1)自相关
    public static int DwellMs();                 // 均值~50ms, 下限30（瞬间点击）
    public static int InterClickMs();            // 均值~200ms, 下限110, 无疲劳
    public static int BurstCount();              // 默认 3~5, 可配置
    public static (int dx, int dy) ClickJitter();// 默认 ~12% 概率, ±1~2px
    public static int NextScanWaitMs();          // 6% 走神 3~8s, 否则 0(靠 OCR 耗时)

    // 动作
    public static int ClickBurst(IntPtr hwnd, Point center); // 执行一组爆发点击, 返回点击数
    public static void MaybeCheckRecords(int groupCount);    // 每 2~3 组触发, 停顿 3~6s, TODO移动

    // 配置
    public static void SetBurstRange(int min, int max);
    public static void SetJitterChance(double chance);
}
```

参数默认值汇总：

| 参数 | 默认 | 说明 |
|---|---|---|
| ReactionDelayMs | mean 200, σ 0.35, 下限 120 | 识别到 → 手指开始动；AR(1) α=0.3 |
| DwellMs | mean 50, σ 0.3, 下限 30 | 按下+抬起，瞬间点击 |
| InterClickMs | mean 200, σ 0.25, 下限 110 | 组内连点间隔，无疲劳 |
| BurstCount | 3~5 | 一组点击次数，UI 可配置 min/max |
| ClickJitter | 12% 概率, ±1px(70%)/±2px(30%) | 偶发小抖动，高频时几乎不抖 |
| NextScanWaitMs | 6% 概率 3~8s | 走神间隔；其余 0（靠 OCR 耗时撑起） |
| MaybeCheckRecords | 每 2~3 组 | 停顿 3~6s（对数正态 mean 4500ms σ0.35） |
| tempo | 每 60~120s ×(0.85~1.15) | 会话级速度漂移 |

注意：`ClickAtScreen(x, y, dwell)` 内部把 dwell 用了两次（按前、按后各 Sleep 一次），传 50ms 实际点按周期约 100ms，符合"瞬间但有细微差距"。

### 4.2 "自动购买"页（MainWindow）

- **目标窗口**：A / B 下拉，复用现有绑定。
- **弹窗识别区域**：`OverlaySelectWindow` 框选 → 存窗口相对坐标 `_autoBuyRect`。
- **购买按钮位置**：`OverlayPickWindow` 点选 → 存窗口相对坐标 `_autoBuyPoint`。
- **一组点击次数** min/max（默认 3 / 5）。
- **目标点击次数**（0 = 不限，累计点击达目标自动停）。
- **最长运行(分钟)**（0 = 不限，无人值守保险丝）。
- **开始 / 停止** 按钮 + 状态文本。

### 4.3 自动购买主循环（状态机）

```csharp
while (!_stopAll)
{
    text = OCR(弹窗区域);                    // 数字模式
    hasPopup = !string.IsNullOrEmpty(text);

    if (armed && hasPopup)                    // 弹窗出现且已武装 → 触发购买
    {
        armed = false;
        Sleep(HumanClicker.ReactionDelayMs());          // 反应延迟
        ClickBurst(hwnd, 购买按钮屏幕坐标);              // 爆发 3~5 下
        MaybeCheckRecords(1);                            // 每 2~3 组停顿核查
    }
    else if (!hasPopup) armed = true;         // 弹窗消失 → 重新武装（一次弹窗只触发一次）

    // 停止条件：目标点击次数 / 最长运行分钟 / _stopAll（防卡死、F12、停止按钮）
    Sleep(HumanClicker.NextScanWaitMs());     // 扫描节奏，多数 0（靠 OCR 耗时）
}
```

要点：
- **一次弹窗只触发一次**：`armed` 状态保证"弹窗出现 → 爆发一次 → 等弹窗消失 → 重新武装"，不会对同一弹窗重复购买。
- **组与组之间的等待**由市场/补货驱动（外部），不模拟。
- 停止条件三类：目标点击次数、最长运行时长、`_stopAll`（防卡死 / F12 / 停止按钮）。

### 4.4 与现有代码的对接点

| 现有代码 | 位置 | 复用方式 |
|---|---|---|
| `_boundAHwnd` / `_boundBHwnd` | MainWindow.xaml.cs | 自动购买目标窗口 |
| `NativeMethods.CaptureWindow` / `GetRect` | NativeMethods.cs | 截图与坐标换算 |
| `_ocr.OcrAsync` / 数字提取 | MainWindow.xaml.cs | 弹窗检测 |
| `OverlaySelectWindow` / `OverlayPickWindow` | Overlay*.xaml.cs | 选区域 / 选按钮 |
| `DoRegisterHotkey` / `InstallHook` / `_stopAll` | MainWindow.xaml.cs | 急停体系 |
| `AppendLog` | MainWindow.xaml.cs | 日志 |
| `GetRandomVal` | MainWindow.xaml.cs | **不改**（原有步骤编辑器保持） |

---

## 五、后续扩展（在案记录，本轮未做）

1. **交易记录核查的真实鼠标移动**：当前 `MaybeCheckRecords` 只有停顿占位（`TODO` 已留）。后续：把目标窗口**窗口化调小**缩短移动距离 → 从购买按钮移动到"交易记录"按钮 → 点击 → 停顿查看 → 返回购买界面。涉及真实鼠标轨迹（下条）。
2. **鼠标轨迹模拟**（三档，均未实现，当前仍是 `SetCursorPos` 瞬移）：
   - 轻量版（~150 行）：直线 + 钟形速度轮廓 + 终点校正。够用。
   - 中等版（+100 行）：贝塞尔曲线路径 + 速度轮廓。
   - 完整版（再 +100 行）：过冲修正 + 2~5px 手抖噪声 + 距离越长轨迹越弯。
   - 注：轨迹模拟只对"事件流/回放观察"有效，对低级注入检测无效（后者认定为结构性无解）。
3. **预设方案**（专注 / 正常 / 放松）：把 `HumanClicker` 的参数集（burst、reaction、走神概率、jitter）做成几套可切换的预设。
4. **手动输入选项**：burst min/max、jitter 概率等已预留 `Set*` 接口，后续接到 UI。
5. **多账号/窗口扩展**：当前单窗口 A/B；后续多开需把 `HumanClicker` 状态（tempo、AR 记忆、组计数）改为按窗口/账号隔离实例。

---

## 六、关键决策记录

| 决策 | 结论 | 理由 |
|---|---|---|
| burst 次数 | 默认 3~5，可配置 | 用户实测一组约 3~5 下 |
| 抖动 | 12% 概率、±1~2px、偶发 | 高频连点几乎不抖，难得才小抖 |
| dwell | 瞬间（mean 50ms，下限 30） | 手动购买是瞬间点击 |
| 反应时间 | mean 200ms，下限 120，AR(1) | 弹窗出现→点击约 0.5s，含 OCR 延迟 |
| 走神 | 扫描节奏 6% 概率 3~8s | 对应偶尔挪开视线；其余靠 OCR 耗时撑起 |
| 核查 | 每 2~3 组，停顿 3~6s | 十几二十次购买后看一次交易记录 |
| 防卡死/F12 | 保留 | 双通道冗余急停，是安全设计不是多余 |
| 管理员 | 保留（manifest 强制） | 为 UIPI：目标程序若是提权窗口需同权限读写 |
| 买满停止单位 | 点击次数 | 用户选定 |
| 分布 | 对数正态，禁均匀 | 原则 1、2：高熵≠人性 |

---

## 七、验证方法

1. `dotnet build -c Release` 编译通过。
2. 手动测试自动购买：
   - 绑定窗口 A → 选择弹窗区域 → 选择购买按钮 → 填一组次数(3/5) → 目标点击次数(如 30) → 开始。
   - 观察：弹窗出现后约 0.5s 内开始爆发点击（3~5 下，偶发 ±1px 抖动）；弹窗消失后重新武装；每 2~3 组停顿 3~6s（核查占位）；累计点击达目标后自动停。
   - 急停测试：运行中按 F12 / 5 秒内右键 10 次 → 脚本立即停止。
   - 最长运行时长：填 1 分钟，跑满 1 分钟自动停。
3. 回归测试：原有步骤编辑器（识别 + 点击 + 循环 + 表达式）行为不变。
