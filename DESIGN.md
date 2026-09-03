# autoclick 自动化助手 — 人类化自动化设计文档

> 本设计文档与代码一同维护，作为换机继续开发的设计依据。配套实现见 `WindowSpy/HumanClicker.cs`（人类化时序工具类）与 `MainWindow` 的"快捷操作"页。

---

## 一、项目定位

Windows 桌面自动化工具（.NET 8 WPF + OpenCvSharp + PaddleOCR/ONNX Runtime）。核心能力：

- 窗口绑定（A/B 双窗口）
- OCR 文字识别（PaddleOCR，支持 DirectML GPU 加速，Python 子进程通信）
- 可视化步骤编排（点击 / OCR / 条件 / 循环 / 跳转 / 表达式）
- 人类化自动化时序（"快捷操作"页）

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
    public static int ClickBurst(IntPtr hwnd, Point center);            // 一组爆发点击(默认 3~5), 返回点击数
    public static int ClickBurst(IntPtr hwnd, Point center, int count); // 按指定次数点击(快捷操作点击节点使用)
    public static void MaybeCheckRecords(int groupCount);    // 每 2~3 组触发, 停顿 3~6s, TODO移动

    // 配置
    public static void Configure(HumanTimingParams? p);      // 整体替换时序参数(null=默认, 见下)
    public static void SetBurstRange(int min, int max);      // 便捷设置组数
}
```

所有时序参数收敛到 `HumanTimingParams`（均值/σ/下限/概率/幅度/组数/间隔），`HumanClicker` 内部持有一份并随 `Configure` 替换。默认值见下。

参数默认值汇总：

| 参数 | 默认 | 说明 |
|---|---|---|
| ReactionDelayMs | mean 200, σ 0.35, 下限 120 | 识别到 → 手指开始动；AR(1) α=0.3 |
| DwellMs | mean 50, σ 0.3, 下限 30 | 按下+抬起，瞬间点击 |
| InterClickMs | mean 200, σ 0.25, 下限 110 | 组内连点间隔，无疲劳 |
| BurstCount | 3~5 | `ClickBurst()` 无参重载的组数；快捷操作每个点击节点改由自带的 min/max（默认 1） |
| ClickJitter | 12% 概率, ±1px(70%)/±2px(30%) | 偶发小抖动，高频时几乎不抖 |
| NextScanWaitMs | 6% 概率 3~8s | 走神间隔；其余 0（靠 OCR 耗时撑起） |
| MaybeCheckRecords | 每 2~3 组 | 停顿 3~6s（对数正态 mean 4500ms σ0.35） |
| tempo | 每 60~120s ×(0.85~1.15) | 会话级速度漂移 |

注意：`ClickAtScreen(x, y, dwell)` 内部把 dwell 用了两次（按前、按后各 Sleep 一次），传 50ms 实际点按周期约 100ms，符合"瞬间但有细微差距"。

> 上表均值/下限/概率/幅度等均可通过「快捷操作」页的**高级设置**（`MainWindow.ApplyTimingConfig`）调整；σ（ln 域波动宽度）属分布形状，为调优默认、未暴露到 UI。组数在快捷操作里是**每个点击节点自己设的 min/max**（默认 1），不再有全局组数。其余参数收在高级设置折叠面板。

### 4.2 "快捷操作"页 = 可视化流程构建器（QuickFlow）

「快捷操作」页从"单个检测区→单点连点"重构为**节点式流程构建器**，可随意编排"看什么 / 什么条件 / 点什么"，支持 if/else 分支与多停止条件。模型见 `WindowSpy/QuickFlow.cs`，执行见 `MainWindow.StartFlow_Click` / `RunFlowEngine`。

- **节点类型（一排从上到下执行 = 一轮，跑完自动从头循环）**
  - **点击**：窗口 A/B 上一个点，每轮连点 min~max 下（同落点多用 3~5，普通路径点 1）。
  - **检测(if)**：窗口 A/B 一块区域 + **多条子条件**（同区域，按 且/或 顺序组合）+ 真分支；可配「否则」。
  - **否则 / 结束**：结构标记，缩进自动推导（观感同旧版步骤队列的 If/Else）。
- **子条件**：出现文字 / 区域为空 / 数字比较(≥≤=><+阈值) / 文本匹配(相等|包含+目标)，各条前带 且/或 连接词（首条固定"当"）。
- **触发方式（每个检测，单选）**
  - **每轮判断（默认）**：每轮按当前状态走 真 / 否则（标准 if/else）。
  - **仅响应变化**：出现瞬间→真分支一次、消失瞬间→否则一次、稳定不重复（防同一弹窗反复买入）。
- **测试识别**：框好区域点「测试识别」→ 后台 OCR 一次并显示结果，方便调条件。
- **停止条件（勾选多选，任一达到即停）**：点击数 / 命中数（每轮每个检测满足并走真分支计 1）/ 轮数 / 运行分钟 / 任意检测勾「满足即停」。
- **保存 / 加载流程**：JSON 存到 `SavedQueues\QuickFlows\`（子目录，避免被旧版队列下拉误列）。
- **窗口可自由拉宽**（默认 1360×820，MinWidth 1100，无 MaxWidth；右列队列宽 400）。
- **界面结构（2026-09 重构）**：顶部 3 个主 Tab = 快捷操作（构建器）/ 复杂操作（原版整套，单页分组）/ 高级设置（时序模式 + 人类化参数 + 精细默认 + GPU）。右侧队列随主 Tab 切换：快捷操作→快捷流程列表；复杂操作/高级设置→原步骤队列。
- **流程节点**：点击 / 检测(if) / 否则 / 结束 / 循环(开始·次数) / 循环结束 / 跳转(目标行)。点击节点自带精细延迟字段（高级设置选"精细延迟"模式时用固定 延迟±随机/偏移/停留；默认人类化模式走 HumanClicker）。
- 高级设置（`_AutoBuyAdvExpander`，默认折叠）：反应/连点/停留等 → `ApplyTimingConfig` 读入 `HumanTimingParams` 并 `Configure`。

### 4.3 快捷操作执行引擎（if/else 解释器）

- 每轮从第 1 个节点顺序执行。遇 **检测(if)**：OCR 区域一次 → `QuickFlowEval.EvaluateConditions` 按 且/或 求值 → 按触发方式选分支：
  - 每轮判断：满足→走真分支；不满足→走否则分支（无否则则跳过）。
  - 仅响应变化：`prevIf[i]` 记上一轮状态，仅 假→真 走真、真→假 走否则；稳定状态跳过整个 if。
- **BuildBranchMaps** 预计算每个 If/Else 配对的 结束 点；真分支落到「否则」节点 = 直接跳到其「结束」，从而天然跳过否则块。
- 真分支/否则分支的**首个点击前** `Sleep(ReactionDelayMs())`（人类反应）；其余点击间隔 `InterClickMs()`；组内连点由 `ClickBurst(hwnd, center, count)` 自带抖动/停留/间隔。
- 每轮结束 `NextScanWaitMs()` 定节奏（多数 0，靠 OCR 耗时撑起）；有点击的轮调 `MaybeCheckRecords(1)`。
- 停止：`_stopAll`（F12 / 右键×10 / 停止按钮）每轮轮询；点击数 / 命中数 / 轮数 / 分钟达到即停；检测勾「满足即停」当场停。
- **循环/跳转**：LoopStart 压栈(次数)，LoopEnd 递减回跳（到达次数出栈继续）；Jump 直接置 `i=目标行`，带每轮 20 万条指令的死循环保护。
- **时序模式全局二选一**（高级设置）：人类化 → `HumanClicker`（反应/间隔/抖动/停留）；精细延迟 → 用各点击节点自己的 `DelayMs±RandomDelay / RandomX/Y / DwellMs±RandomDwell`，等效原版固定延迟步。

### 4.4 与现有代码的对接点

| 现有代码 | 位置 | 复用方式 |
|---|---|---|
| `_boundAHwnd`/`_boundBHwnd` + `TargetType{A,B}` | MainWindow.xaml.cs / ScriptStep.cs | 每个节点所在窗口 |
| `NativeMethods.CaptureWindow`/`GetRect`/`ClickAtScreen` | NativeMethods.cs | 截图、坐标换算、底层点击 |
| `_ocr.OcrAsync` + `CaptureAndOcrRegion` | MainWindow.xaml.cs | 区域 OCR（全文本；数字在客户端提取） |
| `OverlaySelectWindow`/`OverlayPickWindow` | Overlay*.xaml.cs | 框区域 / 取点 |
| `HumanClicker.*`（Configure/Reaction/Inter/ClickBurst(count)/…） | HumanClicker.cs | 人类化时序与点击 |
| `DoRegisterHotkey`/`InstallHook`/`_stopAll`/`StopScript` | MainWindow.xaml.cs | 双通道急停 |
| `AppendLog` | MainWindow.xaml.cs | 日志 |
| 原版 `_steps`/`RunSteps`/`EvaluateExpression` | MainWindow.xaml.cs | **不改**，快捷操作完全独立 |

## 五、后续扩展（在案记录，本轮未做）

1. **交易记录核查的真实鼠标移动**：当前 `MaybeCheckRecords` 只有停顿占位（`TODO` 已留）。后续：把目标窗口**窗口化调小**缩短移动距离 → 从购买按钮移动到"交易记录"按钮 → 点击 → 停顿查看 → 返回购买界面。涉及真实鼠标轨迹（下条）。
2. **鼠标轨迹模拟**（三档，均未实现，当前仍是 `SetCursorPos` 瞬移）：
   - 轻量版（~150 行）：直线 + 钟形速度轮廓 + 终点校正。够用。
   - 中等版（+100 行）：贝塞尔曲线路径 + 速度轮廓。
   - 完整版（再 +100 行）：过冲修正 + 2~5px 手抖噪声 + 距离越长轨迹越弯。
   - 注：轨迹模拟只对"事件流/回放观察"有效，对低级注入检测无效（后者认定为结构性无解）。
3. **预设方案**（专注 / 正常 / 放松）：把 `HumanTimingParams` 的整组参数做成几套可一键切换的预设（本轮已实现手动逐项配置，预设仅差一个"读取预设包"的壳）。
4. **手动输入选项**：✅ 已实现——HumanClicker 改为 `HumanTimingParams` 配置化，快捷操作页「高级设置」可调整全部语义参数（σ 形状除外）。
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
2. 手动测试快捷操作页：绑 A/B 窗口 → 用 ＋点击 / ＋检测(if) / ＋否则 / ＋结束 搭一段分支流程（如"当 数字<66 或 文字含X → 点购买；否则 → 点另一点"）→ 给检测框区域并点「测试识别」核对 OCR → 触发方式按需选「每轮判断」或「仅响应变化」→ 勾停止条件（命中 / 轮数 / 分钟）→ 开始执行。
   - 观察日志：每轮"满足/不满足/命中/点击"清晰；真分支首个点击前有 ~0.2-0.5s 人类反应（含 OCR 延迟）；点击节奏对数正态、偶发 ±1~2px 抖动；每 2~3 个有点击的轮停顿 3~6s（核查占位）。
   - 急停：运行中 F12 / 5 秒内右键 10 次 / 停止按钮均立即停。
   - 停止条件：命中数（Buy N 次）填 N；轮数 / 分钟达到自动停。
3. 回归测试：原有步骤编辑器（识别 + 点击 + 循环 + 表达式）与定时任务行为不变。
