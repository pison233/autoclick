# autoclick 自动化助手

一个基于 .NET 8 WPF、OpenCvSharp 和 OnnxRuntime 构建的 Windows 桌面自动化工具（上游项目 fork，见文末署名）。

## 简介

可视化的 Windows 自动化方案：窗口句柄绑定、OCR 文字识别（PaddleOCR/ONNX，支持 DirectML GPU 加速）、可视化步骤编排（条件/循环/跳转/表达式）。另内置**人类化「快捷操作」**模式：检测到信号 → 像人一样反应 → 在目标上连点一组，操作节奏在统计上接近真人。

## 核心功能

*   **窗口探测 (WindowSpy)**：绑定目标窗口句柄（A/B 双窗口），自动获取标题与位置。
*   **可视化步骤编辑器**：鼠标点击/键盘/OCR 识别 + Loop/If/表达式/Goto 逻辑，支持队列保存/加载、定时任务。
*   **人类化「快捷操作」页**：
    *   **检测窗口 + 操作窗口分离**——检测信号（弹窗/数字）与操作目标可不在同一窗口。
    *   时序模型：对数正态分布、AR(1) 自相关、会话级漂移、偶发落点抖动、走神扫描、核查停顿；全部语义参数可在「高级设置」折叠面板调整。
    *   停止条件：目标次数 / 最长运行分钟 / 防卡死右键×10 + F12 急停。
*   **安全机制**：5 秒内右键 10 次强制停止（系统钩子）、F12 全局热键、UI 与执行线程分离。

## 文档（换机开发必读）

> **[`DESIGN.md`](DESIGN.md)** — 设计思路（9 条原则）、实现原理、参数表、后续扩展、关键决策。
> **[`MANUAL.md`](MANUAL.md)** — 所有可操作元素的完整说明（操作方式/显示/作用/原理/必要性/是否原功能）。
> **[`USAGE.md`](USAGE.md)** — 原用法（步骤编辑器）与新用法（快捷操作）两套完整实战教程。
> 新环境拉代码后先读这三份，再读代码即可对齐全部上下文。

## 环境要求

*   **操作系统**：Windows 10 / 11 (64-bit)
*   **运行环境**：[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（编译需 SDK）/ Runtime；[Python 3.10+](https://www.python.org/)（OCR 后端；已打包 OCR exe 则不需要）。
*   **Python 依赖**（未打包 OCR exe 时）：`pip install -r requirements.txt`

## 快速开始（换机开发）

```bash
git clone git@github.com:pison233/autoclick.git
cd autoclick
dotnet build -c Release          # 需 .NET 8 SDK
# 运行 WindowSpy/bin/Release/net8.0-windows/autoclick.exe（务必管理员运行）
```

OCR 后端两种方式：已打包 `Scripts/dist/onnx_ocr_cli/onnx_ocr_cli.exe`（自动使用，免 Python）；或系统 Python + `pip install -r requirements.txt`。
模型：`Scripts/onnxocr/models/ppocrv5/`（det.onnx + rec.onnx + ppocrv5_dict.txt），缺则 OCR 无法初始化。

## 快捷操作使用（快速上手）

1. **绑定窗口**：点左上圆形图标（绑定A/B），在要绑定的窗口上点一下。若检测与操作是两个窗口，就 A、B 各绑一个。
2. 切到 **「快捷操作」** Tab：
   - **检测窗口** 下拉选 A/B → **选择检测区域**框出信号出现的固定位置。
   - **操作窗口** 下拉选 A/B → **选择操作按钮**在要点击的目标上点一下。
   - 填参数：一组操作次数（默认 3~5）、目标次数（0=不限）、最长运行分钟（0=不限）。
   - 点开 **「高级设置」** 可微调全部人类化时序参数（不点则用调优默认）。
3. 点 **「开始执行」**。循环：扫描检测区 → 信号出现 → 反应(~0.2-0.5s) → 操作窗口目标连点一组 → 信号消失重新武装 → 每 2~3 组停顿核查 → 达目标/超时/急停停止。
4. 停止：界面「停止」/ **F12** / **5 秒内右键 10 次**。

原用法（步骤编辑器）与完整实战见 [`USAGE.md`](USAGE.md)。

## 当前状态

**已实现**：HumanClicker 全参数配置化（`HumanTimingParams` + `Configure`）；「快捷操作」页双窗口检测/操作分离 + 高级设置折叠面板；步骤编辑器原功能；`.NET 8` 编译通过（本机 8.0.424）。

**后续扩展（详见 DESIGN.md）**：交易记录核查的真实鼠标移动；鼠标轨迹模拟（轻量/中等/完整三档）；预设方案（专注/正常/放松）；多账号窗口隔离。

## 上游与许可

本项目为 [MoliGod 自动化助手 (DF-AutomatedTool)](https://github.com/moligod/DF-AutomatedTool)（作者 moligod，GPL）的 fork：保留原步骤编辑器全部功能，新增人类化快捷操作与品牌改名。原作者赞助：https://ifdian.net/a/moligod

本软件开源免费仅供学习交流，**请勿用于非法用途以及商业用途！** 作者不对使用本软件产生的任何后果负责。

**License**: GPL License
