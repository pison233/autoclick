using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Diagnostics;
using Microsoft.Win32;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using WindowSpy.Ocr;
using System.Management;

namespace WindowSpy
{
    public partial class MainWindow : System.Windows.Window
    {
        private IntPtr _capturedHwnd = IntPtr.Zero;
        private bool _dragging = false;
        private IntPtr _boundAHwnd = IntPtr.Zero;
        private IntPtr _boundBHwnd = IntPtr.Zero;
        private System.Drawing.Rectangle? _rectA = null;
        private System.Drawing.Rectangle? _rectB = null;
        private System.Drawing.Point? _clickA = null;
        private System.Drawing.Point? _clickB = null;
        private readonly System.Collections.Generic.List<QuickFlowNode> _flowNodes = new();
        private bool _syncingFlowEditor = false;
        private bool _syncingCondEditor = false;
        private bool _condBusy = false;
        private bool _uiWritePending = false;   // 程序化写控件期间/延后事件到达时也一律忽略
        private bool _suppressUiEvents = false; // MarkUiWrite 置位，直到本次派发结束（含其间 layout）才清除
        private bool _flowSyncQueued = false;   // 已排队一次延迟刷新（合并连续触发）

        private void MarkUiWrite()
        {
            _uiWritePending = true;
            _suppressUiEvents = true;
            Dispatcher.BeginInvoke(new System.Action(() => { _uiWritePending = false; _suppressUiEvents = false; }),
                                   System.Windows.Threading.DispatcherPriority.Background);
        }

        // 同步写 trace.log（硬崩溃前也能落盘，用于排查栈溢出/递归）
        private static string TracePath =>
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jietu", "trace.log");
        private static bool _traceResetDone = false;
        private void TraceLog(string msg)
        {
            try
            {
                var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jietu");
                System.IO.Directory.CreateDirectory(dir);
                if (!_traceResetDone)
                {
                    _traceResetDone = true;
                    try { if (System.IO.File.Exists(TracePath)) System.IO.File.Delete(TracePath); } catch { }
                }
                System.IO.File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
            }
            catch { }
        }

        // 把“重编辑器/重列表面”的同步代码推迟到本次派发(含 layout)结束后再执行，
        // 避免在 SelectionChanged 派发中改 ListBox/ComboBox 的项/选中，触发 WPF 布局重入导致栈溢出。
        private void DeferUi(System.Action act)
        {
            if (_flowSyncQueued) return;
            _flowSyncQueued = true;
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                _flowSyncQueued = false;
                try { act(); }
                catch (Exception ex) { AppendLog("延迟刷新异常：" + ex.Message); TraceLog("DeferUi exception: " + ex); }
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // 编辑器子系统递归深度保险：若框架在派发过程中又同步回调我们的处理函数，
        // 深度超过阈值就截断，把“栈溢出闪退”变成可记录的返回。
        private int _editNest = 0;
        private bool EnterEdit(string where)
        {
            if (_editNest >= 40)
            {
                TraceLog($"!! 编辑器递归深度 {_editNest} 于 {where} —— 截断");
                return false;
            }
            _editNest++;
            return true;
        }
        private void ExitEdit()
        {
            if (_editNest > 0) _editNest--;
        }
        private readonly OnnxOcrHelper _ocr = new();
        private volatile bool _stopAll = false;
        private bool _bindingHotkey = false;
        private System.Windows.Input.Key _stopKey = System.Windows.Input.Key.F12;
        private bool _stopCtrl = false, _stopAlt = false, _stopShift = false;
        private IntPtr _hwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1001;
        private bool _singleModifierOnly = false;
        private System.Windows.Input.ModifierKeys _singleModifier = System.Windows.Input.ModifierKeys.None;
        private System.Windows.Input.ModifierKeys _pressedModsDuringBinding = System.Windows.Input.ModifierKeys.None;
        private bool _nonModifierPressedDuringBinding = false;
        private readonly System.Collections.Generic.List<ScriptStep> _steps = new();
        private System.Windows.Point? _lastMouseDown = null;
        private readonly System.Collections.Generic.Dictionary<string, string> _vars = new();
        private NativeMethods.LowLevelMouseProc _hookProc;
        private IntPtr _hookID = IntPtr.Zero;
        private readonly System.Collections.Generic.Queue<DateTime> _rightClickTimes = new();

        private readonly Random _rng = new Random();
        private readonly object _logLock = new object();
        private readonly System.Collections.Generic.Queue<(DateTime ts, string text)> _pendingLogs = new();
        private bool _logFlushScheduled = false;

        private readonly object _exprLogLock = new object();
        private readonly System.Collections.Generic.Queue<(DateTime ts, string text)> _pendingExprLogs = new();
        private bool _exprLogFlushScheduled = false;

        private readonly System.Collections.Generic.Dictionary<ScriptStep, OcrRoiCacheEntry> _ocrRoiCache = new();
        private int _firstInferenceHintLogged = 0;
        private readonly string _savedQueuesDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedQueues");

        public MainWindow()
        {
            InitializeComponent();
            this.Dispatcher.UnhandledException += (s, args) =>
            {
                try { AppendLog("UI 异常（已拦截）：" + args.Exception); }
                catch { }
                args.Handled = true;
            };
            this.Loaded += MainWindow_Loaded;
            _ocr.Logger = AppendLog;
            _ocr.UseGpu = UseGpuCheck?.IsChecked == true;
            _hookProc = HookCallback;
            
            if (UseGpuCheck != null)
            {
                UseGpuCheck.Checked += (s, e) => { if (_ocr != null) { _ocr.UseGpu = true; AppendLog("设置：已启用 GPU 加速请求"); } };
                UseGpuCheck.Unchecked += (s, e) => { if (_ocr != null) { _ocr.UseGpu = false; AppendLog("设置：已强制切换为 CPU 模式"); } };
            }
        }

        public List<string> GetNetworkAdapters()
        {
            var list = new List<string>();
            try
            {
                var query = new SelectQuery("Win32_NetworkAdapter", "PhysicalAdapter=True AND NetConnectionID IS NOT NULL");
                using var searcher = new ManagementObjectSearcher(query);
                foreach (ManagementObject mo in searcher.Get())
                {
                    string name = mo["NetConnectionID"]?.ToString() ?? mo["Name"]?.ToString() ?? "Unknown";
                    list.Add(name);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[网络] 加载网卡列表失败: {ex.Message}");
            }
            return list;
        }


        private volatile bool _isRunning = false;

        private readonly struct OcrRoiCacheEntry
        {
            public readonly ulong H1;
            public readonly ulong H2;
            public readonly bool NumbersOnly;
            public readonly string Text;

            public OcrRoiCacheEntry(ulong h1, ulong h2, bool numbersOnly, string text)
            {
                H1 = h1;
                H2 = h2;
                NumbersOnly = numbersOnly;
                Text = text;
            }
        }

        private static (ulong h1, ulong h2) ComputeRoiSignature(Mat roi)
        {
            using var gray = new Mat();
            if (roi.Channels() == 1) roi.CopyTo(gray);
            else Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);

            using var small = new Mat();
            Cv2.Resize(gray, small, new OpenCvSharp.Size(48, 48), 0, 0, InterpolationFlags.Area);

            using var u8 = new Mat();
            if (small.Type() == MatType.CV_8UC1) small.CopyTo(u8);
            else small.ConvertTo(u8, MatType.CV_8UC1);

            using var cont = u8.IsContinuous() ? u8 : u8.Clone();
            int byteLen = checked((int)(cont.Total() * cont.ElemSize()));
            var bytes = new byte[byteLen];
            Marshal.Copy(cont.Data, bytes, 0, byteLen);

            var digest = SHA256.HashData(bytes);
            return (BitConverter.ToUInt64(digest, 0), BitConverter.ToUInt64(digest, 8));
        }

        private (string text, bool reused) PerformOcrWithCache(ScriptStep step, Mat matRoi)
        {
            if (step.ReuseOcrOnRoiUnchanged)
            {
                var sig = ComputeRoiSignature(matRoi);
                if (_ocrRoiCache.TryGetValue(step, out var cached) &&
                    cached.H1 == sig.h1 && cached.H2 == sig.h2 &&
                    cached.NumbersOnly == step.OcrNumbersOnly)
                {
                    return (cached.Text ?? "", true);
                }

                string text = PerformOcrInternal(step, matRoi);
                _ocrRoiCache[step] = new OcrRoiCacheEntry(sig.h1, sig.h2, step.OcrNumbersOnly, text);
                return (text, false);
            }
            else
            {
                return (PerformOcrInternal(step, matRoi), false);
            }
        }

        private string PerformOcrInternal(ScriptStep step, Mat matRoi)
        {
            if (Interlocked.Exchange(ref _firstInferenceHintLogged, 1) == 0)
            {
                AppendLog("提示：首次推理识别会慢很多，这属于正常现象");
            }

            var regions = _ocr.OcrAsync(matRoi).GetAwaiter().GetResult();
            var texts = regions.Select(z => z.Text ?? "").ToList();
            if (step.OcrNumbersOnly)
            {
                var nums = texts.Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                .Where(s => s.Any(char.IsDigit)).ToList();
                return nums.Count > 0 ? nums.OrderByDescending(s => s.Length).First() : "";
            }
            else
            {
                return string.Join(" ", texts).Trim();
            }
        }

        public IntPtr GetBoundHwnd(TargetType target)
        {
            return target == TargetType.A ? _boundAHwnd : _boundBHwnd;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            _hwnd = helper.Handle;
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            RegisterStopHotkey();
        }
        protected override void OnClosed(EventArgs e)
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
            base.OnClosed(e);
        }
        private void RegisterStopHotkey()
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
            // 仅在运行时或绑定后显示文本更新，但只有运行时才真正生效，这里只负责更新UI文本
            string label;
            if (_singleModifierOnly)
            {
                label = $"{(_singleModifier.HasFlag(System.Windows.Input.ModifierKeys.Control) ? "Ctrl" : _singleModifier.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? "Alt" : "Shift")}";
            }
            else
            {
                label = $"{(_stopCtrl ? "Ctrl+" : "")}{(_stopAlt ? "Alt+" : "")}{(_stopShift ? "Shift+" : "")}{_stopKey}";
            }
            if (StopAllButton != null) StopAllButton.Content = $"停止全部步骤({label})";
            
            // 如果正在运行，则立即注册
            if (_isRunning)
            {
                DoRegisterHotkey();
            }
        }
        
        private void DoRegisterHotkey()
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
            
            if (_singleModifierOnly)
            {
                // 单修饰键无法通过 RegisterHotKey 注册，只能通过键盘钩子或 KeyDown 事件捕获
                // 这里暂不处理，依赖全局键盘钩子或者仅支持组合键
                // 如果必须支持单键，需要全局钩子。目前代码使用 RegisterHotKey，所以单键实际上在后台无法生效。
                // 为了简单起见，如果用户设置了单键，我们尝试注册一个特殊的无效热键或提示
                // 现有的 PreviewKeyDown 逻辑只在窗口激活时有效。
                // 若要全局生效，必须用 RegisterHotKey。RegisterHotKey 不支持单 Ctrl/Shift。
                // 因此这里如果用户设置单键，仅在窗口激活时有效，不注册全局热键。
                return; 
            }

            uint mods = 0;
            if (_stopCtrl) mods |= 0x0002;
            if (_stopAlt) mods |= 0x0001;
            if (_stopShift) mods |= 0x0004;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(_stopKey);
            if (vk != 0)
            {
                NativeMethods.RegisterHotKey(_hwnd, HOTKEY_ID, mods, vk);
            }
        }

        private void UnregisterStopHotkey()
        {
            try { NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
        }

        private void InstallHook()
        {
            if (_hookID != IntPtr.Zero) return;
            bool enabled = false;
            Dispatcher.Invoke(() => enabled = FailSafeCheck?.IsChecked == true);
            if (!enabled) return;

            lock (_rightClickTimes) _rightClickTimes.Clear();
            using (Process curProcess = Process.GetCurrentProcess())
            {
                using ProcessModule? curModule = curProcess.MainModule;
                if (curModule != null)
                {
                    _hookID = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _hookProc, NativeMethods.GetModuleHandle(curModule.ModuleName), 0);
                }
            }
        }

        private void UninstallHook()
        {
            if (_hookID != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (int)wParam == NativeMethods.WM_RBUTTONDOWN)
            {
                lock (_rightClickTimes)
                {
                    var now = DateTime.Now;
                    _rightClickTimes.Enqueue(now);
                    
                    // 移除5秒前的记录
                    while (_rightClickTimes.Count > 0 && (now - _rightClickTimes.Peek()).TotalSeconds > 5)
                    {
                        _rightClickTimes.Dequeue();
                    }

                    if (_rightClickTimes.Count >= 10)
                    {
                        _stopAll = true;
                        Dispatcher.Invoke(() => AppendLog("触发防卡死保护(5秒内右键10次)"));
                        _rightClickTimes.Clear(); // 防止重复触发
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                _stopAll = true;
                AppendLog("系统快捷键停止全部步骤");
                handled = true; // 表示消息已处理，但这可能会阻止其他应用接收按键
            }
            return IntPtr.Zero;
        }

        private void Icon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            Mouse.Capture((IInputElement)sender);
            Cursor = Cursors.Cross;
        }

        private void Icon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Mouse.Capture(null);
            Cursor = Cursors.Arrow;
            if (NativeMethods.GetCursorPos(out var pt))
            {
                var hwnd = NativeMethods.WindowFromPoint(pt);
                const uint GA_ROOT = 2;
                hwnd = NativeMethods.GetAncestor(hwnd, GA_ROOT);
                _capturedHwnd = hwnd;
                AppendLog("已选择窗口");
            }
        }

        private void UpdateBoundTitles()
        {
            BoundATitle.Text = _boundAHwnd == IntPtr.Zero ? "" : NativeMethods.GetWindowTitle(_boundAHwnd);
            BoundBTitle.Text = _boundBHwnd == IntPtr.Zero ? "" : NativeMethods.GetWindowTitle(_boundBHwnd);
        }

        private void ShotButtonA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero)
            {
                AppendLog("请先绑定窗口A");
                return;
            }
            bool onlyNums = OcrAOnlyNums?.IsChecked == true;
            Task.Run(() =>
            {
                try
                {
                    using Bitmap? bmp = NativeMethods.CaptureWindow(_boundAHwnd);
                    if (bmp == null) return;
                    string ocrText = "";
                    if (_rectA is { } r)
                    {
                        using var matFull = BitmapConverter.ToMat(bmp);
                        var x = Math.Max(0, Math.Min(matFull.Cols - 1, r.X));
                        var y = Math.Max(0, Math.Min(matFull.Rows - 1, r.Y));
                        var w = Math.Max(1, Math.Min(matFull.Cols - x, r.Width));
                        var h = Math.Max(1, Math.Min(matFull.Rows - y, r.Height));
                        var roi = new OpenCvSharp.Rect(x, y, w, h);
                        using var matRoi = new Mat(matFull, roi);
                        var regions = _ocr.OcrAsync(matRoi).GetAwaiter().GetResult();
                        var texts = regions.Select(z => z.Text ?? "").ToList();
                        
                        if (onlyNums)
                        {
                            var nums = texts.Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                          .Where(s => s.Any(char.IsDigit)).ToList();
                            if (nums.Count > 0) ocrText = nums.OrderByDescending(s => s.Length).First();
                        }
                        else
                        {
                            ocrText = string.Join(" ", texts).Trim();
                        }

                        using var g = Graphics.FromImage(bmp);
                        using var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 3);
                        g.DrawRectangle(pen, r);
                    }
                    Dispatcher.Invoke(() => SetOcrResult(OcrResultTextA, ocrText));
                    var path = NativeMethods.SaveBitmap(bmp);
                    Dispatcher.Invoke(() => AppendLog($"已保存：{path}"));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"截图失败：{ex.Message}"));
                }
            });
        }

        private void SelectAreaButtonA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero)
            {
                AppendLog("请先绑定窗口A");
                return;
            }
            var overlay = new OverlaySelectWindow();
            var ok = overlay.ShowDialog();
            if (ok != true) return;
            var sel = overlay.SelectedRect;
            var wrect = NativeMethods.GetRect(_boundAHwnd);
            var interLeft = Math.Max(wrect.Left, (int)sel.Left);
            var interTop = Math.Max(wrect.Top, (int)sel.Top);
            var interRight = Math.Min(wrect.Right, (int)(sel.Left + sel.Width));
            var interBottom = Math.Min(wrect.Bottom, (int)(sel.Top + sel.Height));
            if (interRight <= interLeft || interBottom <= interTop)
            {
                AppendLog("A：选择区域不在目标窗口内");
                return;
            }
            _rectA = System.Drawing.Rectangle.FromLTRB(
                interLeft - wrect.Left,
                interTop - wrect.Top,
                interRight - wrect.Left,
                interBottom - wrect.Top
            );
            OcrResultTextA.Text = "";
            OcrResultTextA.Foreground = System.Windows.Media.Brushes.Black;
            AppendLog($"A已选择区域：{_rectA.Value.Width}x{_rectA.Value.Height}");
        }

        private void BindBButton_Click(object sender, RoutedEventArgs e)
        {
            if (_capturedHwnd == IntPtr.Zero) { AppendLog("请先通过圆形图标选择一个窗口"); return; }
            _boundBHwnd = _capturedHwnd;
            UpdateBoundTitles();
            AppendLog("已绑定窗口B");
        }

        private void BindAButton_Click(object sender, RoutedEventArgs e)
        {
            if (_capturedHwnd == IntPtr.Zero) { AppendLog("请先通过圆形图标选择一个窗口"); return; }
            _boundAHwnd = _capturedHwnd;
            UpdateBoundTitles();
            AppendLog("已绑定窗口A");
        }
        
        private void BindA_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var picker = new OverlayPickWindow();
            var ok = picker.ShowDialog();
            if (ok == true)
            {
                var p = picker.ClickPoint;
                var pt = new WindowSpy.NativeMethods.POINT { X = (int)p.X, Y = (int)p.Y };
                var hwnd = NativeMethods.WindowFromPoint(pt);
                const uint GA_ROOT = 2;
                hwnd = NativeMethods.GetAncestor(hwnd, GA_ROOT);
                _boundAHwnd = hwnd;
                UpdateBoundTitles();
                AppendLog("已绑定窗口A(拖拽)");
            }
        }

        private void BindB_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var picker = new OverlayPickWindow();
            var ok = picker.ShowDialog();
            if (ok == true)
            {
                var p = picker.ClickPoint;
                var pt = new WindowSpy.NativeMethods.POINT { X = (int)p.X, Y = (int)p.Y };
                var hwnd = NativeMethods.WindowFromPoint(pt);
                const uint GA_ROOT = 2;
                hwnd = NativeMethods.GetAncestor(hwnd, GA_ROOT);
                _boundBHwnd = hwnd;
                UpdateBoundTitles();
                AppendLog("已绑定窗口B(拖拽)");
            }
        }

        private void ShotButtonB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero)
            {
                AppendLog("请先绑定窗口B");
                return;
            }
            bool onlyNums = OcrBOnlyNums?.IsChecked == true;
            Task.Run(() =>
            {
                try
                {
                    using Bitmap? bmp = NativeMethods.CaptureWindow(_boundBHwnd);
                    if (bmp == null) return;
                    string ocrText = "";
                    if (_rectB is { } r)
                    {
                        using var matFull = BitmapConverter.ToMat(bmp);
                        var x = Math.Max(0, Math.Min(matFull.Cols - 1, r.X));
                        var y = Math.Max(0, Math.Min(matFull.Rows - 1, r.Y));
                        var w = Math.Max(1, Math.Min(matFull.Cols - x, r.Width));
                        var h = Math.Max(1, Math.Min(matFull.Rows - y, r.Height));
                        var roi = new OpenCvSharp.Rect(x, y, w, h);
                        using var matRoi = new Mat(matFull, roi);
                        var regions = _ocr.OcrAsync(matRoi).GetAwaiter().GetResult();
                        var texts = regions.Select(z => z.Text ?? "").ToList();

                        if (onlyNums)
                        {
                            var nums = texts.Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                          .Where(s => s.Any(char.IsDigit)).ToList();
                            if (nums.Count > 0) ocrText = nums.OrderByDescending(s => s.Length).First();
                        }
                        else
                        {
                            ocrText = string.Join(" ", texts).Trim();
                        }

                        using var g = Graphics.FromImage(bmp);
                        using var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 3);
                        g.DrawRectangle(pen, r);
                    }
                    Dispatcher.Invoke(() => SetOcrResult(OcrResultTextB, ocrText));
                    var path = NativeMethods.SaveBitmap(bmp);
                    Dispatcher.Invoke(() => AppendLog($"已保存：{path}"));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"截图失败：{ex.Message}"));
                }
            });
        }

        private void SelectAreaButtonB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero)
            {
                AppendLog("请先绑定窗口B");
                return;
            }
            var overlay = new OverlaySelectWindow();
            var ok = overlay.ShowDialog();
            if (ok != true) return;
            var sel = overlay.SelectedRect;
            var wrect = NativeMethods.GetRect(_boundBHwnd);
            var interLeft = Math.Max(wrect.Left, (int)sel.Left);
            var interTop = Math.Max(wrect.Top, (int)sel.Top);
            var interRight = Math.Min(wrect.Right, (int)(sel.Left + sel.Width));
            var interBottom = Math.Min(wrect.Bottom, (int)(sel.Top + sel.Height));
            if (interRight <= interLeft || interBottom <= interTop)
            {
                AppendLog("B：选择区域不在目标窗口内");
                return;
            }
            _rectB = System.Drawing.Rectangle.FromLTRB(
                interLeft - wrect.Left,
                interTop - wrect.Top,
                interRight - wrect.Left,
                interBottom - wrect.Top
            );
            OcrResultTextB.Text = "";
            OcrResultTextB.Foreground = System.Windows.Media.Brushes.Black;
            AppendLog($"B已选择区域：{_rectB.Value.Width}x{_rectB.Value.Height}");
        }

        private void SelectClickPosA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            var picker = new OverlayPickWindow();
            var ok = picker.ShowDialog();
            if (ok == true)
            {
                var wrect = NativeMethods.GetRect(_boundAHwnd);
                int sx = (int)picker.ClickPoint.X;
                int sy = (int)picker.ClickPoint.Y;
                if (sx < wrect.Left || sy < wrect.Top || sx >= wrect.Right || sy >= wrect.Bottom)
                {
                    AppendLog("A：点击位置不在绑定窗口内");
                    return;
                }
                _clickA = new System.Drawing.Point(sx - wrect.Left, sy - wrect.Top);
                AppendLog($"A已选择点击位置：{_clickA.Value.X},{_clickA.Value.Y}");
            }
        }

        private void ClickSelectedPosA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            if (_clickA == null) { AppendLog("A：尚未选择点击位置"); return; }
            var wrect = NativeMethods.GetRect(_boundAHwnd);
            int sx = wrect.Left + _clickA.Value.X;
            int sy = wrect.Top + _clickA.Value.Y;
            if (NativeMethods.IsIconic(_boundAHwnd)) NativeMethods.ShowWindow(_boundAHwnd, 9);
            NativeMethods.SetForegroundWindow(_boundAHwnd);
            int dwell = ParseInt(DwellMsA?.Text, 100);
            NativeMethods.ClickAtScreen(sx, sy, dwell);
            AppendLog($"A已点击位置：{sx},{sy}");
        }

        private void SelectClickPosB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            var picker = new OverlayPickWindow();
            var ok = picker.ShowDialog();
            if (ok == true)
            {
                var wrect = NativeMethods.GetRect(_boundBHwnd);
                int sx = (int)picker.ClickPoint.X;
                int sy = (int)picker.ClickPoint.Y;
                if (sx < wrect.Left || sy < wrect.Top || sx >= wrect.Right || sy >= wrect.Bottom)
                {
                    AppendLog("B：点击位置不在绑定窗口内");
                    return;
                }
                _clickB = new System.Drawing.Point(sx - wrect.Left, sy - wrect.Top);
                AppendLog($"B已选择点击位置：{_clickB.Value.X},{_clickB.Value.Y}");
            }
        }

        private void ClickSelectedPosB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            if (_clickB == null) { AppendLog("B：尚未选择点击位置"); return; }
            var wrect = NativeMethods.GetRect(_boundBHwnd);
            int sx = wrect.Left + _clickB.Value.X;
            int sy = wrect.Top + _clickB.Value.Y;
            if (NativeMethods.IsIconic(_boundBHwnd)) NativeMethods.ShowWindow(_boundBHwnd, 9);
            NativeMethods.SetForegroundWindow(_boundBHwnd);
            int dwell = ParseInt(DwellMsB?.Text, 100);
            NativeMethods.ClickAtScreen(sx, sy, dwell);
            AppendLog($"B已点击位置：{sx},{sy}");
        }

        private int ParseInt(string? s, int def)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            var digits = new string(s.Where(char.IsDigit).ToArray());
            if (string.IsNullOrEmpty(digits)) return def;
            if (int.TryParse(digits, out var v)) return v;
            return def;
        }

        private void AddOcrStepA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            if (_rectA == null) { AppendLog("A：尚未选择识别区域"); return; }
            _steps.Add(new ScriptStep { 
                Target = TargetType.A, Type = ActionType.Ocr, Rect = _rectA.Value, 
                DelayMs = ParseInt(DelayMsA?.Text, 300), 
                RandomDelay = ParseInt(RandomDelayA?.Text, 0),
                DwellMs = ParseInt(DwellMsA?.Text, 100),
                RandomDwell = ParseInt(RandomDwellA?.Text, 0),
                RandomX = ParseInt(RandomXA?.Text, 0),
                RandomY = ParseInt(RandomYA?.Text, 0),
                OcrNumbersOnly = OcrAOnlyNums?.IsChecked == true,
                ReuseOcrOnRoiUnchanged = ReuseOcrRoiCacheCheck?.IsChecked == true
            });
            AppendLog("A步骤已添加：识别");
            RefreshSteps();
        }

        private void AddClickStepA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            if (_clickA == null) { AppendLog("A：尚未选择点击位置"); return; }
            _steps.Add(new ScriptStep { 
                Target = TargetType.A, Type = ActionType.Click, Point = _clickA.Value, 
                DelayMs = ParseInt(DelayMsA?.Text, 300), 
                RandomDelay = ParseInt(RandomDelayA?.Text, 0),
                DwellMs = ParseInt(DwellMsA?.Text, 100),
                RandomDwell = ParseInt(RandomDwellA?.Text, 0),
                RandomX = ParseInt(RandomXA?.Text, 0),
                RandomY = ParseInt(RandomYA?.Text, 0)
            });
            AppendLog("A步骤已添加：点击");
            RefreshSteps();
        }
        private void AddBringFrontA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            _steps.Add(new ScriptStep { Target = TargetType.A, Type = ActionType.BringFront, DelayMs = ParseInt(DelayMsA?.Text, 0) });
            AppendLog("A步骤已添加：置顶窗口");
            RefreshSteps();
        }

        private int GetRandomVal(int baseVal, int randomRange)
        {
            if (randomRange <= 0) return baseVal;
            return baseVal + _rng.Next(-randomRange, randomRange + 1);
        }

        private async void RunScriptA_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            var list = _steps.Where(s => s.Target == TargetType.A).ToList();
            if (list.Count == 0) { AppendLog("A：步骤为空"); return; }
            
            _stopAll = false;
            _isRunning = true;
            DoRegisterHotkey();
            InstallHook();
            
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string lastOcrA = OcrResultTextA?.Text ?? "";
                    foreach (var step in list)
                {
                    if (_stopAll) break;
                    int delay = Math.Max(0, GetRandomVal(step.DelayMs, step.RandomDelay));
                    System.Threading.Thread.Sleep(delay);
                    
                    if (step.Type == ActionType.Click)
                    {
                        var wrect = NativeMethods.GetRect(_boundAHwnd);
                        int offX = GetRandomVal(0, step.RandomX);
                        int offY = GetRandomVal(0, step.RandomY);
                        int sx = wrect.Left + step.Point.X + offX;
                        int sy = wrect.Top + step.Point.Y + offY;
                        
                        if (NativeMethods.IsIconic(_boundAHwnd)) NativeMethods.ShowWindow(_boundAHwnd, 9);
                        NativeMethods.SetForegroundWindow(_boundAHwnd);
                        
                        int dwell = Math.Max(0, GetRandomVal(step.DwellMs, step.RandomDwell));
                        NativeMethods.ClickAtScreen(sx, sy, dwell);
                        Dispatcher.Invoke(() => AppendLog($"A执行：点击 {sx},{sy} (延{delay} 停{dwell})"));
                    }
                    else if (step.Type == ActionType.Ocr)
                    {
                        using var bmp = NativeMethods.CaptureWindow(_boundAHwnd);
                        using var mat = BitmapConverter.ToMat(bmp);
                        var r = step.Rect;
                        var x = Math.Max(0, Math.Min(mat.Cols - 1, r.X));
                        var y = Math.Max(0, Math.Min(mat.Rows - 1, r.Y));
                        var w = Math.Max(1, Math.Min(mat.Cols - x, r.Width));
                        var h = Math.Max(1, Math.Min(mat.Rows - y, r.Height));
                        var roi = new OpenCvSharp.Rect(x, y, w, h);
                        using var matRoi = new Mat(mat, roi);
                        var ocr = PerformOcrWithCache(step, matRoi);
                        lastOcrA = ocr.text;
                        Dispatcher.Invoke(() => SetOcrResult(OcrResultTextA, ocr.text));
                        var tag = step.ReuseOcrOnRoiUnchanged ? (ocr.reused ? "(复用)" : "(重新识别)") : "";
                        Dispatcher.Invoke(() => AppendLog($"A执行：识别{tag} {ocr.text}"));
                    }
                    else if (step.Type == ActionType.Condition)
                    {
                        string text = (!string.IsNullOrWhiteSpace(step.Key) && _vars.TryGetValue(step.Key, out var v)) ? v : lastOcrA;
                        bool match = !string.IsNullOrEmpty(text) && System.Text.RegularExpressions.Regex.IsMatch(text, step.Pattern);
                        step.LastResult = match;
                        Dispatcher.Invoke(() => AppendLog($"A条件检查: {(match ? "匹配" : "不匹配")} 模式 {step.Pattern} 文本 {text}"));
                        Dispatcher.Invoke(() => RefreshSteps());
                        if ((step.JumpOnTrue && match) || (!step.JumpOnTrue && !match)) break;
                    }
                    else if (step.Type == ActionType.Expression)
                    {
                        bool ok = EvaluateExpression(step.Pattern, out string? errorMsg);
                        step.LastResult = ok;
                        if (errorMsg != null) Dispatcher.Invoke(() => AppendLog($"A表达式错误: {errorMsg}"));
                        Dispatcher.Invoke(() => AppendLog($"A表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}，跳出条件={ (step.JumpOnTrue ? "为真" : "为假") }"));
                        Dispatcher.Invoke(() => AppendExprLog($"A表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}"));
                        Dispatcher.Invoke(() => RefreshSteps());
                        if ((step.JumpOnTrue && ok) || (!step.JumpOnTrue && !ok)) break;
                    }
                    else if (step.Type == ActionType.KeyPress)
                    {
                        var keyStr = step.Key;
                        if (Enum.TryParse<System.Windows.Input.Key>(keyStr, true, out var k))
                        {
                            var vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
                            NativeMethods.keybd_event((byte)vk, 0, 0, 0);
                            if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
                            NativeMethods.keybd_event((byte)vk, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
                            Dispatcher.Invoke(() => AppendLog($"A执行按键：{keyStr}"));
                        }
                        else
                        {
                            Dispatcher.Invoke(() => AppendLog($"A未知按键：{keyStr}"));
                        }
                    }
                    else if (step.Type == ActionType.Network)
                    {
                        if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
                    }
                }
            }
            finally
            {
                _isRunning = false;
                Dispatcher.Invoke(() => UnregisterStopHotkey());
                UninstallHook();
            }
            });
        }

        private void ClearScriptA_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in _steps.Where(s => s.Target == TargetType.A).ToList())
            {
                _ocrRoiCache.Remove(s);
            }
            _steps.RemoveAll(s => s.Target == TargetType.A);
            AppendLog("A：已清空步骤");
            RefreshSteps();
        }

        private void AddOcrStepB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            if (_rectB == null) { AppendLog("B：尚未选择识别区域"); return; }
            _steps.Add(new ScriptStep { 
                Target = TargetType.B, Type = ActionType.Ocr, Rect = _rectB.Value, 
                DelayMs = ParseInt(DelayMsB?.Text, 300), 
                RandomDelay = ParseInt(RandomDelayB?.Text, 0),
                DwellMs = ParseInt(DwellMsB?.Text, 100),
                RandomDwell = ParseInt(RandomDwellB?.Text, 0),
                RandomX = ParseInt(RandomXB?.Text, 0),
                RandomY = ParseInt(RandomYB?.Text, 0),
                OcrNumbersOnly = OcrBOnlyNums?.IsChecked == true,
                ReuseOcrOnRoiUnchanged = ReuseOcrRoiCacheCheck?.IsChecked == true
            });
            AppendLog("B步骤已添加：识别");
            RefreshSteps();
        }

        private void AddClickStepB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            if (_clickB == null) { AppendLog("B：尚未选择点击位置"); return; }
            _steps.Add(new ScriptStep { 
                Target = TargetType.B, Type = ActionType.Click, Point = _clickB.Value, 
                DelayMs = ParseInt(DelayMsB?.Text, 300), 
                RandomDelay = ParseInt(RandomDelayB?.Text, 0),
                DwellMs = ParseInt(DwellMsB?.Text, 100),
                RandomDwell = ParseInt(RandomDwellB?.Text, 0),
                RandomX = ParseInt(RandomXB?.Text, 0),
                RandomY = ParseInt(RandomYB?.Text, 0)
            });
            AppendLog("B步骤已添加：点击");
            RefreshSteps();
        }
        private void AddBringFrontB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            _steps.Add(new ScriptStep { Target = TargetType.B, Type = ActionType.BringFront, DelayMs = ParseInt(DelayMsB?.Text, 0) });
            AppendLog("B步骤已添加：置顶窗口");
            RefreshSteps();
        }

        // ============================
        // 快捷操作（人类化时序）
        // ============================
        private string CaptureAndOcrRegion(IntPtr hwnd, System.Drawing.Rectangle rect, bool numbersOnly)
        {
            using var bmp = NativeMethods.CaptureWindow(hwnd);
            using var mat = BitmapConverter.ToMat(bmp);
            var x = Math.Max(0, Math.Min(mat.Cols - 1, rect.X));
            var y = Math.Max(0, Math.Min(mat.Rows - 1, rect.Y));
            var w = Math.Max(1, Math.Min(mat.Cols - x, rect.Width));
            var h = Math.Max(1, Math.Min(mat.Rows - y, rect.Height));
            using var matRoi = new Mat(mat, new OpenCvSharp.Rect(x, y, w, h));
            var regions = _ocr.OcrAsync(matRoi).GetAwaiter().GetResult();
            var texts = regions.Select(z => z.Text ?? "").ToList();
            if (numbersOnly)
            {
                var nums = texts.Select(t => new string(t.Where(ch => char.IsDigit(ch) || ch == ',').ToArray()))
                                .Where(s => s.Any(char.IsDigit)).ToList();
                return nums.Count > 0 ? nums.OrderByDescending(s => s.Length).First() : "";
            }
            return string.Join(" ", texts).Trim();
        }

        private static int ClampInt(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

        // 从「高级设置」读取参数并应用到 HumanClicker
        private void ApplyTimingConfig()
        {
            var p = new HumanTimingParams
            {
                ReactionMeanMs = ParseInt(AdvReactMean?.Text, (int)p0.ReactionMeanMs),
                ReactionFloorMs = ParseInt(AdvReactFloor?.Text, p0.ReactionFloorMs),
                InterClickMeanMs = ParseInt(AdvInterMean?.Text, (int)p0.InterClickMeanMs),
                InterClickFloorMs = ParseInt(AdvInterFloor?.Text, p0.InterClickFloorMs),
                DwellMeanMs = ParseInt(AdvDwellMean?.Text, (int)p0.DwellMeanMs),
                JitterChance = ClampInt(ParseInt(AdvJitterPct?.Text, 12), 0, 100) / 100.0,
                JitterLargePx = ClampInt(ParseInt(AdvJitterPx?.Text, p0.JitterLargePx), 1, 50),
                DistractChance = ClampInt(ParseInt(AdvDistractPct?.Text, 6), 0, 100) / 100.0,
                DistractMeanMs = ParseInt(AdvDistractMs?.Text, (int)p0.DistractMeanMs),
                CheckMinGroups = ClampInt(ParseInt(AdvCheckMin?.Text, p0.CheckMinGroups), 1, 100),
                CheckMaxGroups = ClampInt(ParseInt(AdvCheckMax?.Text, p0.CheckMaxGroups), 1, 100),
                CheckPauseMeanMs = ParseInt(AdvCheckMs?.Text, (int)p0.CheckPauseMeanMs),
            };
            if (p.CheckMaxGroups < p.CheckMinGroups) p.CheckMaxGroups = p.CheckMinGroups;
            double tempoPct = ClampInt(ParseInt(AdvTempoPct?.Text, 15), 1, 60);
            p.TempoMin = 1 - tempoPct / 100.0;
            p.TempoMax = 1 + tempoPct / 100.0;
            HumanClicker.Configure(p);
        }
        private static readonly HumanTimingParams p0 = new HumanTimingParams();

        // ============================
        // 快捷操作：流程构建器（点击 / 检测(if)/否则/结束 + 多子条件 + 多停止条件）
        // ============================

        // ============================
        // 快捷操作：流程构建器（点击 / 检测(if)/否则/结束 / 循环 / 跳转 + 多子条件 + 多停止条件）
        // ============================

        private int SelectedFlowIndex => QuickFlowList?.SelectedIndex ?? -1;

        private void SyncButtonsAndEditor()
        {
            int idx = SelectedFlowIndex;
            bool has = idx >= 0 && idx < _flowNodes.Count;
            if (MoveUpButton != null) { MoveUpButton.IsEnabled = has && idx > 0; }
            if (MoveDownButton != null) { MoveDownButton.IsEnabled = has && idx < _flowNodes.Count - 1; }
            if (DeleteNodeButton != null) { DeleteNodeButton.IsEnabled = has; }
            // 编辑器重载延到空闲时执行，避免在 SelectionChanged/layout 派发里改 ComboBox 项触发重入
            DeferUi(LoadEditorFromSelected);
        }

        private string FlowNodeLabel(QuickFlowNode n)
        {
            string win = n.Target == TargetType.A ? "A" : "B";
            switch (n.Type)
            {
                case QuickNodeType.Click:
                    {
                        string pt = n.Point.IsEmpty ? "未录点" : $"{n.Point.X},{n.Point.Y}";
                        string rep = n.RepeatMin == n.RepeatMax ? n.RepeatMin.ToString() : $"{n.RepeatMin}~{n.RepeatMax}";
                        return $"[点击] 窗口{win} ({pt}) ×{rep}";
                    }
                case QuickNodeType.If:
                    {
                        string rect = (n.Rect.Width > 0 && n.Rect.Height > 0) ? $"{n.Rect.Width}×{n.Rect.Height}" : "未选区";
                        string trig = n.TriggerMode == CheckTriggerMode.EveryRound ? " [每轮判断]" : " [变化触发]";
                        string stop = n.StopWhenTrue ? " [满足即停]" : "";
                        return $"[检测] 窗口{win} 区{rect}{trig}{stop} 条件:{QuickFlowEval.DescribeConditions(n.Conditions)}";
                    }
                case QuickNodeType.Else: return "[否则]";
                case QuickNodeType.End: return "[结束]";
                case QuickNodeType.LoopStart: return $"[循环 x{Math.Max(1, n.LoopCount)}]";
                case QuickNodeType.LoopEnd: return "[循环结束]";
                case QuickNodeType.Jump:
                    return n.JumpTarget >= 0 && n.JumpTarget < _flowNodes.Count
                        ? $"[跳到 第{n.JumpTarget + 1}行]"
                        : "[跳转] 未设目标";
                default: return n.Type.ToString();
            }
        }

        private void RefreshFlowList(bool reselectCurrent)
        {
            if (!EnterEdit("RefreshFlowList")) { TraceLog("RefreshFlowList 递归已截断"); return; }
            try
            {
            int keep = QuickFlowList.SelectedIndex;
            var contents = new string[_flowNodes.Count];
            int depth = 0;
            for (int i = 0; i < _flowNodes.Count; i++)
            {
                var nd = _flowNodes[i];
                int showDepth = depth;
                if (nd.Type == QuickNodeType.End || nd.Type == QuickNodeType.LoopEnd)
                {
                    depth = Math.Max(0, depth - 1);
                    showDepth = depth;
                }
                else if (nd.Type == QuickNodeType.Else)
                {
                    showDepth = Math.Max(0, depth - 1);
                }
                else if (nd.Type == QuickNodeType.If || nd.Type == QuickNodeType.LoopStart)
                {
                    depth++;
                }
                contents[i] = $"{i + 1}. {new string(' ', showDepth * 3)}{FlowNodeLabel(nd)}";
            }

            // 数量一致时只就地改行文字，避免整表清空导致选中/焦点抖动
            if (contents.Length == QuickFlowList.Items.Count)
            {
                for (int i = 0; i < contents.Length; i++)
                    if (QuickFlowList.Items[i] is ListBoxItem it) it.Content = contents[i];
            }
            else
            {
                TraceLog($"FlowList 重建 {contents.Length} 行 keep={keep} reselect={reselectCurrent}");
                QuickFlowList.SelectionChanged -= QuickFlowList_SelectionChanged;
                QuickFlowList.Items.Clear();
                for (int i = 0; i < contents.Length; i++)
                    QuickFlowList.Items.Add(new ListBoxItem { Content = contents[i], FontSize = 12 });
                if (reselectCurrent && keep >= 0 && keep < _flowNodes.Count) QuickFlowList.SelectedIndex = keep;
                QuickFlowList.SelectionChanged += QuickFlowList_SelectionChanged;
            }
            SyncButtonsAndEditor();
            }
            finally { ExitEdit(); }
        }

        private int InsertIndexAfterSelected() => SelectedFlowIndex >= 0 && SelectedFlowIndex < _flowNodes.Count ? SelectedFlowIndex + 1 : _flowNodes.Count;

        private TargetType PreferredTarget()
        {
            if (_boundBHwnd != IntPtr.Zero && _boundAHwnd == IntPtr.Zero) return TargetType.B;
            return TargetType.A;
        }

        private void AddFlowClick_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero && _boundBHwnd == IntPtr.Zero) { AppendLog("快捷操作：请先绑定窗口A或B"); return; }
            int at = InsertIndexAfterSelected();
            var n = new QuickFlowNode { Type = QuickNodeType.Click, Target = PreferredTarget(), RepeatMin = 1, RepeatMax = 1 };
            ApplyFineDefaults(n);
            _flowNodes.Insert(at, n);
            RefreshFlowList(false);
            QuickFlowList.SelectedIndex = at;
            AppendLog($"已添加点击节点（第 {at + 1} 行）：在右侧重录点击位置");
        }

        private void AddFlowIf_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero && _boundBHwnd == IntPtr.Zero) { AppendLog("快捷操作：请先绑定窗口A或B"); return; }
            int at = InsertIndexAfterSelected();
            var n = new QuickFlowNode { Type = QuickNodeType.If, Target = PreferredTarget() };
            n.Conditions.Add(new FlowCondition { Kind = CheckConditionKind.HasContent });
            _flowNodes.Insert(at, n);
            RefreshFlowList(false);
            QuickFlowList.SelectedIndex = at;
            AppendLog($"已添加检测(if)节点（第 {at + 1} 行）：框区域、写子条件；其后的行是真分支，直到 否则/结束");
        }

        private void AddFlowElse_Click(object sender, RoutedEventArgs e)
        {
            int at = InsertIndexAfterSelected();
            _flowNodes.Insert(at, new QuickFlowNode { Type = QuickNodeType.Else, Target = PreferredTarget() });
            RefreshFlowList(false);
            QuickFlowList.SelectedIndex = at;
            AppendLog($"已添加否则(else)节点（第 {at + 1} 行）：对应最近一个未配对的检测(if)");
        }

        private void AddFlowEnd_Click(object sender, RoutedEventArgs e)
        {
            int at = InsertIndexAfterSelected();
            _flowNodes.Insert(at, new QuickFlowNode { Type = QuickNodeType.End, Target = PreferredTarget() });
            RefreshFlowList(false);
            QuickFlowList.SelectedIndex = at;
            AppendLog($"已添加结束(end)节点（第 {at + 1} 行）：关闭最近一个未配对的 if/否则");
        }

        private void AddFlowLoop_Click(object sender, RoutedEventArgs e)
        {
            int at = InsertIndexAfterSelected();
            _flowNodes.Insert(at, new QuickFlowNode { Type = QuickNodeType.LoopStart, Target = PreferredTarget(), LoopCount = 3 });
            RefreshFlowList(false);
            QuickFlowList.SelectedIndex = at;
            AppendLog($"已添加循环(开始)节点（第 {at + 1} 行，默认 3 次）：右侧改次数，用 循环结束 收尾");
        }

        private void AddFlowLoopEnd_Click(object sender, RoutedEventArgs e)
        {
            int at = InsertIndexAfterSelected();
            _flowNodes.Insert(at, new QuickFlowNode { Type = QuickNodeType.LoopEnd, Target = PreferredTarget() });
            RefreshFlowList(false);
            QuickFlowList.SelectedIndex = at;
            AppendLog($"已添加循环结束节点（第 {at + 1} 行）：关闭最近一个未配对的 循环(开始)");
        }

        private void AddFlowJump_Click(object sender, RoutedEventArgs e)
        {
            int at = InsertIndexAfterSelected();
            _flowNodes.Insert(at, new QuickFlowNode { Type = QuickNodeType.Jump, Target = PreferredTarget(), JumpTarget = -1 });
            RefreshFlowList(false);
            QuickFlowList.SelectedIndex = at;
            AppendLog($"已添加跳转节点（第 {at + 1} 行）：右侧选要跳到的行");
        }

        private void QuickFlowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 重建/清空期间的瞬时 -1 不处理，避免编辑器闪隐（隐藏交给 RefreshFlowList 末的 Sync）
            if (QuickFlowList.SelectedIndex < 0) return;
            DeferUi(SyncButtonsAndEditor);
        }

        private void MoveFlowUp_Click(object sender, RoutedEventArgs e)
        {
            int i = SelectedFlowIndex;
            if (i <= 0 || i >= _flowNodes.Count) return;
            (_flowNodes[i - 1], _flowNodes[i]) = (_flowNodes[i], _flowNodes[i - 1]);
            RefreshFlowList(false);
            QuickFlowList.SelectedIndex = i - 1;
        }

        private void MoveFlowDown_Click(object sender, RoutedEventArgs e)
        {
            int i = SelectedFlowIndex;
            if (i < 0 || i >= _flowNodes.Count - 1) return;
            (_flowNodes[i], _flowNodes[i + 1]) = (_flowNodes[i + 1], _flowNodes[i]);
            RefreshFlowList(false);
            QuickFlowList.SelectedIndex = i + 1;
        }

        private void DeleteFlowNode_Click(object sender, RoutedEventArgs e)
        {
            int i = SelectedFlowIndex;
            if (i < 0 || i >= _flowNodes.Count) return;
            var n = _flowNodes[i];
            _flowNodes.RemoveAt(i);
            RefreshFlowList(false);
            AppendLog($"已删除第 {i + 1} 行：{FlowNodeLabel(n)}");
        }

        // ---------- 节点编辑器同步 ----------

        private void LoadEditorFromSelected()
        {
            if (!EnterEdit("LoadEditorFromSelected")) { TraceLog("LoadEditor 递归已截断"); return; }
            try
            {
            int idx = SelectedFlowIndex;
            bool has = idx >= 0 && idx < _flowNodes.Count;
            if (FlowEditorEmptyHint != null) FlowEditorEmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (FlowEditorFields != null) FlowEditorFields.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has) return;
            MarkUiWrite();

            var n = _flowNodes[idx];
            if (FlowEditorTitle != null)
                FlowEditorTitle.Text = $"编辑选中节点（第 {idx + 1} 行）";
            TraceLog($"LoadEditor idx={idx} type={n.Type}");

            _syncingFlowEditor = true;
            try
            {
                bool showTarget = n.Type == QuickNodeType.Click || n.Type == QuickNodeType.If;
                bool isMark = n.Type == QuickNodeType.Else || n.Type == QuickNodeType.End || n.Type == QuickNodeType.LoopEnd;
                if (EditorTargetRow != null) EditorTargetRow.Visibility = showTarget ? Visibility.Visible : Visibility.Collapsed;
                if (FlowClickEditor != null) FlowClickEditor.Visibility = n.Type == QuickNodeType.Click ? Visibility.Visible : Visibility.Collapsed;
                if (FlowIfEditor != null) FlowIfEditor.Visibility = n.Type == QuickNodeType.If ? Visibility.Visible : Visibility.Collapsed;
                if (FlowLoopEditor != null) FlowLoopEditor.Visibility = n.Type == QuickNodeType.LoopStart ? Visibility.Visible : Visibility.Collapsed;
                if (FlowJumpEditor != null) FlowJumpEditor.Visibility = n.Type == QuickNodeType.Jump ? Visibility.Visible : Visibility.Collapsed;
                if (FlowMarkEditor != null) FlowMarkEditor.Visibility = isMark ? Visibility.Visible : Visibility.Collapsed;
                if (EditTargetCombo != null) EditTargetCombo.SelectedIndex = n.Target == TargetType.B ? 1 : 0;

                if (n.Type == QuickNodeType.Click)
                {
                    if (EditRepeatMin != null) EditRepeatMin.Text = n.RepeatMin.ToString();
                    if (EditRepeatMax != null) EditRepeatMax.Text = n.RepeatMax.ToString();
                    if (EditPointText != null) EditPointText.Text = n.Point.IsEmpty ? "未选择" : $"{n.Point.X},{n.Point.Y}";
                    LoadFineFields(n);
                }
                else if (n.Type == QuickNodeType.If)
                {
                    if (EditRectText != null)
                        EditRectText.Text = (n.Rect.Width > 0 && n.Rect.Height > 0)
                            ? $"区域已录：{n.Rect.X},{n.Rect.Y}  {n.Rect.Width}x{n.Rect.Height}" : "未选择区域";
                    if (EditRectTestText != null) { EditRectTestText.Text = ""; }
                    if (EditTrigEveryRound != null) EditTrigEveryRound.IsChecked = n.TriggerMode != CheckTriggerMode.OncePerAppearance;
                    if (EditTrigOnce != null) EditTrigOnce.IsChecked = n.TriggerMode == CheckTriggerMode.OncePerAppearance;
                    if (EditStopWhenTrueCheck != null) EditStopWhenTrueCheck.IsChecked = n.StopWhenTrue;
                    if (n.Conditions.Count == 0) n.Conditions.Add(new FlowCondition { Kind = CheckConditionKind.HasContent });
                    TraceLog("LoadEditor -> RefreshCondList");
                    RefreshCondList();
                }
                else if (n.Type == QuickNodeType.LoopStart)
                {
                    if (EditLoopCount != null) EditLoopCount.Text = Math.Max(1, n.LoopCount).ToString();
                }
                else if (n.Type == QuickNodeType.Jump)
                {
                    TraceLog("LoadEditor -> PopulateJumpTarget");
                    PopulateJumpTarget(idx, n);
                }
            }
            catch (Exception ex)
            {
                AppendLog("节点编辑器载入异常：" + ex);
                TraceLog("LoadEditor exception: " + ex);
            }
            finally { _syncingFlowEditor = false; }
            }
            finally { ExitEdit(); }
        }

        private void LoadFineFields(QuickFlowNode n)
        {
            if (EditDelayMs != null) EditDelayMs.Text = n.DelayMs.ToString();
            if (EditRandomDelay != null) EditRandomDelay.Text = n.RandomDelay.ToString();
            if (EditDwellMs != null) EditDwellMs.Text = n.DwellMs.ToString();
            if (EditRandomDwell != null) EditRandomDwell.Text = n.RandomDwell.ToString();
            if (EditRandomX != null) EditRandomX.Text = n.RandomX.ToString();
            if (EditRandomY != null) EditRandomY.Text = n.RandomY.ToString();
        }

        private void ApplyFineDefaults(QuickFlowNode n)
        {
            n.DelayMs = ParseInt(DfltDelay?.Text, 300);
            n.RandomDelay = ParseInt(DfltRandomDelay?.Text, 0);
            n.DwellMs = ParseInt(DfltDwell?.Text, 100);
            n.RandomDwell = ParseInt(DfltRandomDwell?.Text, 0);
            n.RandomX = ParseInt(DfltRandomX?.Text, 0);
            n.RandomY = ParseInt(DfltRandomY?.Text, 0);
        }

        private void PopulateJumpTarget(int selfIdx, QuickFlowNode n)
        {
            if (EditJumpTarget == null) return;
            var labels = new System.Collections.Generic.List<string>();
            for (int i = 0; i < _flowNodes.Count; i++)
            {
                if (i == selfIdx) continue;
                labels.Add($"{i + 1}. {FlowNodeLabel(_flowNodes[i])}");
            }
            bool same = EditJumpTarget.Items.Count == labels.Count;
            if (same)
            {
                for (int i = 0; i < labels.Count; i++)
                    if (!Equals((EditJumpTarget.Items[i] as ComboBoxItem)?.Content, labels[i])) { same = false; break; }
            }
            // 内容没变就不重建，避免 Clear/Add 反复触发 SelectionChanged + layout 重入
            if (!same)
            {
                EditJumpTarget.SelectionChanged -= EditNode_Changed;
                try
                {
                    EditJumpTarget.Items.Clear();
                    foreach (var lab in labels) EditJumpTarget.Items.Add(new ComboBoxItem { Content = lab });
                }
                finally { EditJumpTarget.SelectionChanged += EditNode_Changed; }
            }
            int k = -1;
            if (n.JumpTarget >= 0 && n.JumpTarget < _flowNodes.Count && n.JumpTarget != selfIdx)
                k = n.JumpTarget > selfIdx ? n.JumpTarget - 1 : n.JumpTarget;
            if (k >= 0 && k < EditJumpTarget.Items.Count && EditJumpTarget.SelectedIndex != k)
            {
                EditJumpTarget.SelectionChanged -= EditNode_Changed;
                try { EditJumpTarget.SelectedIndex = k; }
                finally { EditJumpTarget.SelectionChanged += EditNode_Changed; }
            }
            else if (k < 0 && EditJumpTarget.SelectedIndex >= 0)
            {
                EditJumpTarget.SelectionChanged -= EditNode_Changed;
                try { EditJumpTarget.SelectedIndex = -1; }
                finally { EditJumpTarget.SelectionChanged += EditNode_Changed; }
            }
        }

        private void CommitEditorToSelected()
        {
            int idx = SelectedFlowIndex;
            if (idx < 0 || idx >= _flowNodes.Count) return;
            var n = _flowNodes[idx];
            if (n.Type == QuickNodeType.Else || n.Type == QuickNodeType.End || n.Type == QuickNodeType.LoopEnd) return;
            if ((n.Type == QuickNodeType.Click || n.Type == QuickNodeType.If) && EditTargetCombo != null)
                n.Target = EditTargetCombo.SelectedIndex == 1 ? TargetType.B : TargetType.A;
            if (n.Type == QuickNodeType.Click)
            {
                int mn = ClampInt(ParseInt(EditRepeatMin?.Text, 1), 1, 100);
                int mx = ClampInt(ParseInt(EditRepeatMax?.Text, mn), mn, 100);
                n.RepeatMin = mn; n.RepeatMax = mx;
                n.DelayMs = Math.Max(0, ParseInt(EditDelayMs?.Text, 300));
                n.RandomDelay = Math.Max(0, ParseInt(EditRandomDelay?.Text, 0));
                n.DwellMs = Math.Max(0, ParseInt(EditDwellMs?.Text, 100));
                n.RandomDwell = Math.Max(0, ParseInt(EditRandomDwell?.Text, 0));
                n.RandomX = Math.Max(0, ParseInt(EditRandomX?.Text, 0));
                n.RandomY = Math.Max(0, ParseInt(EditRandomY?.Text, 0));
            }
            else if (n.Type == QuickNodeType.If)
            {
                n.TriggerMode = EditTrigOnce?.IsChecked == true ? CheckTriggerMode.OncePerAppearance : CheckTriggerMode.EveryRound;
                n.StopWhenTrue = EditStopWhenTrueCheck?.IsChecked == true;
            }
            else if (n.Type == QuickNodeType.LoopStart)
            {
                n.LoopCount = ClampInt(ParseInt(EditLoopCount?.Text, 3), 1, 9999);
            }
            else if (n.Type == QuickNodeType.Jump)
            {
                int k = EditJumpTarget?.SelectedIndex ?? -1;
                n.JumpTarget = k >= 0 ? (k >= idx ? k + 1 : k) : -1;
            }
        }

        private void EditNode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingFlowEditor || _uiWritePending || _suppressUiEvents) return;
            CommitEditorToSelected();
            DeferUi(() => RefreshFlowList(true));
        }

        private void EditNode_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingFlowEditor || _uiWritePending || _suppressUiEvents) return;
            CommitEditorToSelected();
            DeferUi(() => RefreshFlowList(true));
        }

        private void EditNode_Click(object sender, RoutedEventArgs e)
        {
            if (_syncingFlowEditor || _uiWritePending || _suppressUiEvents) return;
            CommitEditorToSelected();
            DeferUi(() => RefreshFlowList(true));
        }

        private void EditTriggerRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingFlowEditor || _uiWritePending || _suppressUiEvents) return;
            CommitEditorToSelected();
            DeferUi(() => RefreshFlowList(true));
        }

        private void ReRecordFlowPoint_Click(object sender, RoutedEventArgs e)
        {
            int idx = SelectedFlowIndex;
            if (idx < 0 || idx >= _flowNodes.Count) return;
            var n = _flowNodes[idx];
            IntPtr hwnd = n.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
            if (hwnd == IntPtr.Zero) { AppendLog("快捷操作：请先绑定该窗口"); return; }
            var picker = new OverlayPickWindow();
            if (picker.ShowDialog() == true)
            {
                var wrect = NativeMethods.GetRect(hwnd);
                int sx = (int)picker.ClickPoint.X;
                int sy = (int)picker.ClickPoint.Y;
                if (sx < wrect.Left || sy < wrect.Top || sx >= wrect.Right || sy >= wrect.Bottom)
                { AppendLog("快捷操作：位置不在该窗口内，请重试"); return; }
                n.Point = new System.Drawing.Point(sx - wrect.Left, sy - wrect.Top);
                if (EditPointText != null) EditPointText.Text = $"{n.Point.X},{n.Point.Y}";
                RefreshFlowList(true);
                AppendLog($"快捷操作：已记录点击位置 {n.Point.X},{n.Point.Y}");
            }
        }

        private void ReRecordFlowRect_Click(object sender, RoutedEventArgs e)
        {
            int idx = SelectedFlowIndex;
            if (idx < 0 || idx >= _flowNodes.Count) return;
            var n = _flowNodes[idx];
            IntPtr hwnd = n.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
            if (hwnd == IntPtr.Zero) { AppendLog("快捷操作：请先绑定该窗口"); return; }
            var overlay = new OverlaySelectWindow();
            if (overlay.ShowDialog() == true)
            {
                var sel = overlay.SelectedRect;
                var wrect = NativeMethods.GetRect(hwnd);
                var il = Math.Max(wrect.Left, (int)sel.Left);
                var it = Math.Max(wrect.Top, (int)sel.Top);
                var ir = Math.Min(wrect.Right, (int)(sel.Left + sel.Width));
                var ib = Math.Min(wrect.Bottom, (int)(sel.Top + sel.Height));
                if (ir <= il || ib <= it) { AppendLog("快捷操作：选择区域不在该窗口内，请重试"); return; }
                n.Rect = System.Drawing.Rectangle.FromLTRB(il - wrect.Left, it - wrect.Top, ir - wrect.Left, ib - wrect.Top);
                if (EditRectText != null) EditRectText.Text = $"区域已录：{n.Rect.X},{n.Rect.Y}  {n.Rect.Width}x{n.Rect.Height}";
                if (EditRectTestText != null) EditRectTestText.Text = "";
                RefreshFlowList(true);
                AppendLog($"快捷操作：已记录检测区域 {n.Rect.Width}x{n.Rect.Height}");
            }
        }

        // 对选中检测节点区域做一次 OCR 测试（后台执行）
        private async void TestFlowRegion_Click(object sender, RoutedEventArgs e)
        {
            int idx = SelectedFlowIndex;
            if (idx < 0 || idx >= _flowNodes.Count) return;
            var n = _flowNodes[idx];
            if (_isRunning) { AppendLog("快捷操作：运行时不能测试识别"); return; }
            IntPtr hwnd = n.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
            if (hwnd == IntPtr.Zero) { AppendLog("快捷操作：请先绑定该窗口"); return; }
            if (n.Rect.Width <= 0 || n.Rect.Height <= 0) { AppendLog("快捷操作：请先记录检测区域"); return; }
            var rect = n.Rect;
            if (TestFlowRegionButton != null) { TestFlowRegionButton.IsEnabled = false; TestFlowRegionButton.Content = "识别中..."; }
            string text = "";
            try { text = await System.Threading.Tasks.Task.Run(() => CaptureAndOcrRegion(hwnd, rect, false)); }
            catch (Exception ex) { text = ""; AppendLog($"快捷操作：测试识别出错 {ex.Message}"); }
            bool empty = string.IsNullOrWhiteSpace(text);
            if (EditRectTestText != null)
            {
                EditRectTestText.Text = empty ? "未识别到数据（可换区域或改用“出现文字/为空”等条件）" : $"识别到：{text}";
                EditRectTestText.Foreground = empty ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;
            }
            AppendLog($"快捷操作：测试识别 => {(empty ? "未识别到数据" : text)}");
            if (TestFlowRegionButton != null) { TestFlowRegionButton.IsEnabled = true; TestFlowRegionButton.Content = "测试识别"; }
        }

        // ---------- 检测节点子条件编辑 ----------

        private int SelectedCondIndex => FlowCondList?.SelectedIndex ?? -1;

        private QuickFlowNode? CurrentIfNode()
        {
            int idx = SelectedFlowIndex;
            if (idx < 0 || idx >= _flowNodes.Count) return null;
            var n = _flowNodes[idx];
            return n.Type == QuickNodeType.If ? n : null;
        }

        private void RefreshCondList()
        {
            if (!EnterEdit("RefreshCondList")) { TraceLog("RefreshCondList 递归已截断"); return; }
            try
            {
                MarkUiWrite();
                var n = CurrentIfNode();
                int keep = FlowCondList.SelectedIndex;
                var labels = new string[n == null ? 0 : n.Conditions.Count];
                if (n != null)
                {
                    for (int i = 0; i < n.Conditions.Count; i++)
                    {
                        var c = n.Conditions[i];
                        string pre = i == 0 ? "当 " : (c.Conj == ConjType.Or ? "或 " : "且 ");
                        labels[i] = $"{i + 1}. {pre}{QuickFlowEval.DescribeCond(c)}";
                    }
                }
                // 数量一致时只就地改行文字，避免整表清空造成抖动/递归
                if (labels.Length == FlowCondList.Items.Count)
                {
                    for (int i = 0; i < labels.Length; i++)
                        if (FlowCondList.Items[i] is ListBoxItem it) it.Content = labels[i];
                }
                else
                {
                    TraceLog($"CondList 重建 {labels.Length} 条 keep={keep}");
                    FlowCondList.SelectionChanged -= FlowCondList_SelectionChanged;
                    FlowCondList.Items.Clear();
                    for (int i = 0; i < labels.Length; i++)
                        FlowCondList.Items.Add(new ListBoxItem { Content = labels[i], FontSize = 12 });
                    if (keep >= 0 && keep < labels.Length) FlowCondList.SelectedIndex = keep;
                    FlowCondList.SelectionChanged += FlowCondList_SelectionChanged;
                }
                DeferUi(SyncCondUI);
            }
            catch (Exception ex)
            {
                AppendLog($"子条件刷新异常：{ex.Message}");
                TraceLog("RefreshCondList exception: " + ex.Message);
            }
            finally { ExitEdit(); }
        }

        private void SyncCondUI()
        {
            if (_condBusy) return;
            _condBusy = true;
            try
            {
                var n = CurrentIfNode();
                int ci = SelectedCondIndex;
                bool valid = n != null && ci >= 0 && ci < n!.Conditions.Count;
                if (DelFlowCondButton != null) DelFlowCondButton.IsEnabled = valid;
                if (FlowCondDetail != null) FlowCondDetail.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
                if (valid) LoadCondDetail(n!.Conditions[ci], ci);
            }
            catch (Exception ex)
            {
                AppendLog($"子条件界面同步异常：{ex.Message}");
            }
            finally { _condBusy = false; }
        }

        private void LoadCondDetail(FlowCondition c, int ci)
        {
            if (_syncingCondEditor || _uiWritePending || _suppressUiEvents) return;
            MarkUiWrite();
            _syncingCondEditor = true;
            TraceLog($"LoadCondDetail ci={ci} kind={c.Kind}");
            try
            {
                if (CondConjCombo != null)
                {
                    TraceLog("  -> CondConjCombo.SelectedIndex");
                    CondConjCombo.SelectedIndex = c.Conj == ConjType.Or ? 1 : 0;
                    CondConjCombo.IsEnabled = ci > 0;
                    TraceLog("  <- CondConjCombo ok");
                }
                TraceLog("  -> CondKindCombo.SelectedIndex");
                if (CondKindCombo != null) CondKindCombo.SelectedIndex = (int)c.Kind;
                TraceLog("  <- CondKindCombo ok");
                RefreshCondSubPanels();
                if (CondNumOpCombo != null) CondNumOpCombo.SelectedIndex = (int)c.NumOp;
                if (CondNumValue != null) CondNumValue.Text = c.NumThreshold.ToString();
                if (CondTextOpCombo != null) CondTextOpCombo.SelectedIndex = c.TextOp == TextMatchOp.Equal ? 0 : 1;
                if (CondTextValue != null) CondTextValue.Text = c.TextValue;
                TraceLog("  LoadCondDetail done");
            }
            catch (Exception ex)
            {
                AppendLog($"子条件载入异常：{ex.Message}");
                TraceLog("LoadCondDetail exception: " + ex.Message);
            }
            finally { _syncingCondEditor = false; }
        }

        private void CommitCondDetail()
        {
            var n = CurrentIfNode();
            int ci = SelectedCondIndex;
            if (n == null || ci < 0 || ci >= n.Conditions.Count) return;
            var c = n.Conditions[ci];
            if (CondConjCombo != null && ci > 0) c.Conj = CondConjCombo.SelectedIndex == 1 ? ConjType.Or : ConjType.And;
            if (CondKindCombo != null) c.Kind = (CheckConditionKind)ClampInt(CondKindCombo.SelectedIndex, 0, 3);
            if (c.Kind == CheckConditionKind.NumCompare)
            {
                if (CondNumOpCombo != null) c.NumOp = (NumCompareOp)ClampInt(CondNumOpCombo.SelectedIndex, 0, 4);
                c.NumThreshold = ParseInt(CondNumValue?.Text, 0);
            }
            else if (c.Kind == CheckConditionKind.TextMatch)
            {
                c.TextOp = CondTextOpCombo?.SelectedIndex == 0 ? TextMatchOp.Equal : TextMatchOp.Contains;
                c.TextValue = CondTextValue?.Text ?? "";
            }
        }

        private void RefreshCondSubPanels()
        {
            int kind = CondKindCombo?.SelectedIndex ?? -1;
            if (CondNumPanel != null) CondNumPanel.Visibility = kind == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (CondTextPanel != null) CondTextPanel.Visibility = kind == 3 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FlowCondList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FlowCondList.SelectedIndex < 0) return;
            DeferUi(SyncCondUI);
        }

        private void CondSub_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingCondEditor || _uiWritePending || _suppressUiEvents) return;
            try
            {
                CommitCondDetail();
                RefreshCondSubPanels();
                DeferUi(RefreshCondList);
            }
            catch (Exception ex) { AppendLog($"子条件切换异常：{ex.Message}"); }
        }

        private void CondSub_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingCondEditor || _uiWritePending || _suppressUiEvents) return;
            try
            {
                CommitCondDetail();
                DeferUi(RefreshCondList);
            }
            catch (Exception ex) { AppendLog($"子条件输入异常：{ex.Message}"); }
        }

        private void AddFlowCond_Click(object sender, RoutedEventArgs e)
        {
            var n = CurrentIfNode();
            if (n == null) return;
            n.Conditions.Add(new FlowCondition { Kind = CheckConditionKind.HasContent });
            RefreshCondList();
            FlowCondList.SelectedIndex = n.Conditions.Count - 1;
        }

        private void DelFlowCond_Click(object sender, RoutedEventArgs e)
        {
            var n = CurrentIfNode();
            int ci = SelectedCondIndex;
            if (n == null || ci < 0 || ci >= n.Conditions.Count) return;
            if (n.Conditions.Count <= 1) { AppendLog("至少保留一条子条件"); return; }
            n.Conditions.RemoveAt(ci);
            RefreshCondList();
            RefreshFlowList(true);
        }

        // ---------- 停止条件 / 其它 ----------

        private void StopToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (StopClicksBox != null) StopClicksBox.IsEnabled = StopUseClicksCheck?.IsChecked == true;
            if (StopTriggersBox != null) StopTriggersBox.IsEnabled = StopUseTriggersCheck?.IsChecked == true;
            if (StopRoundsBox != null) StopRoundsBox.IsEnabled = StopUseRoundsCheck?.IsChecked == true;
            if (StopMinutesBox != null) StopMinutesBox.IsEnabled = StopUseMinutesCheck?.IsChecked == true;
        }

        private void DecimalOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = sender as System.Windows.Controls.TextBox;
            string t = (tb?.Text ?? "") + e.Text;
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c) && c != '.') { e.Handled = true; return; }
            }
            if (t.IndexOf('.') != t.LastIndexOf('.')) e.Handled = true;
        }

        private static double ParseDouble(string? s, double def)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            return double.TryParse(s, out var v) ? v : def;
        }

        // ---------- 保存 / 加载 ----------

        private string FlowSaveDir => System.IO.Path.Combine(_savedQueuesDir, "QuickFlows");

        private void EnsureFlowDir()
        {
            try { if (!System.IO.Directory.Exists(FlowSaveDir)) System.IO.Directory.CreateDirectory(FlowSaveDir); } catch { }
        }

        private QuickFlowStopDto ReadStopDto()
        {
            return new QuickFlowStopDto
            {
                UseClicks = StopUseClicksCheck?.IsChecked == true,
                Clicks = ParseInt(StopClicksBox?.Text, 0),
                UseTriggers = StopUseTriggersCheck?.IsChecked == true,
                Triggers = ParseInt(StopTriggersBox?.Text, 0),
                UseRounds = StopUseRoundsCheck?.IsChecked == true,
                Rounds = ParseInt(StopRoundsBox?.Text, 0),
                UseMinutes = StopUseMinutesCheck?.IsChecked == true,
                Minutes = ParseDouble(StopMinutesBox?.Text, 0),
            };
        }

        private void SaveFlow_Click(object sender, RoutedEventArgs e)
        {
            EnsureFlowDir();
            var dlg = new SaveFileDialog();
            dlg.Filter = "JSON 文件|*.json";
            dlg.FileName = "quickflow.json";
            dlg.InitialDirectory = FlowSaveDir;
            if (dlg.ShowDialog() == true)
            {
                var fileData = new QuickFlowFileDto
                {
                    Nodes = _flowNodes.Select(QuickFlowMapper.ToDto).ToList(),
                    Stop = ReadStopDto(),
                };
                var json = JsonSerializer.Serialize(fileData, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(dlg.FileName, json);
                AppendLog($"已保存流程：{dlg.FileName}");
            }
        }

        private void LoadFlow_Click(object sender, RoutedEventArgs e)
        {
            EnsureFlowDir();
            var dlg = new OpenFileDialog();
            dlg.Filter = "JSON 文件|*.json";
            dlg.InitialDirectory = FlowSaveDir;
            if (dlg.ShowDialog() != true) return;
            try
            {
                var fileData = JsonSerializer.Deserialize<QuickFlowFileDto>(System.IO.File.ReadAllText(dlg.FileName));
                if (fileData == null) { AppendLog("加载流程失败：文件为空"); return; }
                _flowNodes.Clear();
                if (fileData.Nodes != null)
                    foreach (var d in fileData.Nodes) _flowNodes.Add(QuickFlowMapper.FromDto(d));

                _syncingFlowEditor = true;
                try
                {
                    var s = fileData.Stop ?? new QuickFlowStopDto();
                    if (StopUseClicksCheck != null) StopUseClicksCheck.IsChecked = s.UseClicks;
                    if (StopClicksBox != null) { StopClicksBox.Text = s.Clicks.ToString(); StopClicksBox.IsEnabled = s.UseClicks; }
                    if (StopUseTriggersCheck != null) StopUseTriggersCheck.IsChecked = s.UseTriggers;
                    if (StopTriggersBox != null) { StopTriggersBox.Text = s.Triggers.ToString(); StopTriggersBox.IsEnabled = s.UseTriggers; }
                    if (StopUseRoundsCheck != null) StopUseRoundsCheck.IsChecked = s.UseRounds;
                    if (StopRoundsBox != null) { StopRoundsBox.Text = s.Rounds.ToString(); StopRoundsBox.IsEnabled = s.UseRounds; }
                    if (StopUseMinutesCheck != null) StopUseMinutesCheck.IsChecked = s.UseMinutes;
                    if (StopMinutesBox != null) { StopMinutesBox.Text = s.Minutes.ToString("0.##"); StopMinutesBox.IsEnabled = s.UseMinutes; }
                }
                finally { _syncingFlowEditor = false; }

                RefreshFlowList(false);
                AppendLog($"已加载流程：{dlg.FileName}（{_flowNodes.Count} 个节点）");
            }
            catch (Exception ex)
            {
                AppendLog("加载流程失败：" + ex.Message);
            }
        }

        // ---------- 运行 ----------

        private void SetFlowEditingEnabled(bool en)
        {
            if (QuickFlowList != null) QuickFlowList.IsEnabled = en;
            if (AddFlowClickButton != null) AddFlowClickButton.IsEnabled = en;
            if (AddFlowIfButton != null) AddFlowIfButton.IsEnabled = en;
            if (AddFlowElseButton != null) AddFlowElseButton.IsEnabled = en;
            if (AddFlowEndButton != null) AddFlowEndButton.IsEnabled = en;
            if (AddFlowLoopButton != null) AddFlowLoopButton.IsEnabled = en;
            if (AddFlowLoopEndButton != null) AddFlowLoopEndButton.IsEnabled = en;
            if (AddFlowJumpButton != null) AddFlowJumpButton.IsEnabled = en;
            if (SaveFlowButton != null) SaveFlowButton.IsEnabled = en;
            if (LoadFlowButton != null) LoadFlowButton.IsEnabled = en;
            if (MoveUpButton != null) MoveUpButton.IsEnabled = en && SelectedFlowIndex > 0;
            if (MoveDownButton != null) MoveDownButton.IsEnabled = en && SelectedFlowIndex >= 0 && SelectedFlowIndex < _flowNodes.Count - 1;
            if (DeleteNodeButton != null) DeleteNodeButton.IsEnabled = en && SelectedFlowIndex >= 0;
            if (FlowEditorPanel != null) FlowEditorPanel.IsEnabled = en;
        }

        // 结构校验（栈）：If/LoopStart 开块，Else/End/LoopEnd 收尾配对
        private string? ValidateStructure(System.Collections.Generic.List<QuickFlowNode> nodes)
        {
            var stack = new System.Collections.Generic.Stack<QuickNodeType>();
            for (int i = 0; i < nodes.Count; i++)
            {
                var nd = nodes[i];
                switch (nd.Type)
                {
                    case QuickNodeType.If:
                    case QuickNodeType.LoopStart:
                        stack.Push(nd.Type);
                        break;
                    case QuickNodeType.Else:
                        if (stack.Count == 0 || stack.Peek() != QuickNodeType.If)
                            return $"第 {i + 1} 行「否则」前没有未配对的检测(if)";
                        break;
                    case QuickNodeType.End:
                        if (stack.Count == 0 || stack.Peek() != QuickNodeType.If)
                            return $"第 {i + 1} 行「结束」多余（没有与之配对的检测）";
                        stack.Pop();
                        break;
                    case QuickNodeType.LoopEnd:
                        if (stack.Count == 0 || stack.Peek() != QuickNodeType.LoopStart)
                            return $"第 {i + 1} 行「循环结束」多余（没有与之配对的循环开始）";
                        stack.Pop();
                        break;
                }
            }
            if (stack.Count > 0)
                return $"有 {stack.Count} 个块未收尾（缺 结束/循环结束）";
            return null;
        }

        // 预计算：elseFor[i]=If 的否则；endFor[i]=If/Else 匹配的结束；loopEndFor[i]=LoopStart 的循环结束
        private void BuildBlockMaps(System.Collections.Generic.List<QuickFlowNode> nodes, int[] elseFor, int[] endFor, int[] loopEndFor)
        {
            int n = nodes.Count;
            for (int i = 0; i < n; i++) { elseFor[i] = -1; endFor[i] = -1; loopEndFor[i] = -1; }
            var stack = new System.Collections.Generic.Stack<int>();
            var kinds = new System.Collections.Generic.Stack<QuickNodeType>();
            for (int i = 0; i < n; i++)
            {
                var t = nodes[i].Type;
                if (t == QuickNodeType.If || t == QuickNodeType.LoopStart) { stack.Push(i); kinds.Push(t); }
                else if (t == QuickNodeType.Else)
                {
                    if (stack.Count > 0 && kinds.Peek() == QuickNodeType.If && elseFor[stack.Peek()] < 0)
                        elseFor[stack.Peek()] = i;
                }
                else if (t == QuickNodeType.End)
                {
                    if (stack.Count > 0 && kinds.Peek() == QuickNodeType.If)
                    {
                        int opener = stack.Pop(); kinds.Pop();
                        endFor[opener] = i;
                        if (elseFor[opener] >= 0) endFor[elseFor[opener]] = i;
                    }
                }
                else if (t == QuickNodeType.LoopEnd)
                {
                    if (stack.Count > 0 && kinds.Peek() == QuickNodeType.LoopStart)
                    {
                        int opener = stack.Pop(); kinds.Pop();
                        loopEndFor[opener] = i;
                    }
                }
            }
        }

        private sealed class FlowRunOptions
        {
            public bool UseHumanTiming = true;
            public bool UseClicks; public int StopClicks;
            public bool UseTriggers; public int StopTriggers;
            public bool UseRounds; public int StopRounds;
            public bool UseMinutes; public double StopMinutes;
        }

        private async void StartFlow_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) { AppendLog("快捷操作：已在运行"); return; }

            var errors = new System.Collections.Generic.List<string>();
            if (_flowNodes.Count == 0) errors.Add("流程为空");
            var structErr = ValidateStructure(_flowNodes);
            if (structErr != null) errors.Add(structErr);
            if (errors.Count == 0)
            {
                for (int i = 0; i < _flowNodes.Count; i++)
                {
                    var n = _flowNodes[i];
                    IntPtr h = n.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
                    if ((n.Type == QuickNodeType.Click || n.Type == QuickNodeType.If) && h == IntPtr.Zero)
                    { errors.Add($"第 {i + 1} 行所在窗口{n.Target}未绑定"); break; }
                    if (n.Type == QuickNodeType.Click && n.Point.IsEmpty)
                    { errors.Add($"第 {i + 1} 行点击节点未记录位置"); break; }
                    if (n.Type == QuickNodeType.If)
                    {
                        if (n.Rect.Width <= 0 || n.Rect.Height <= 0) { errors.Add($"第 {i + 1} 行检测(if)节点未记录区域"); break; }
                        if (n.Conditions.Count == 0) { errors.Add($"第 {i + 1} 行检测(if)节点没有子条件"); break; }
                        if (n.Conditions.Any(c => c.Kind == CheckConditionKind.TextMatch && string.IsNullOrWhiteSpace(c.TextValue)))
                        { errors.Add($"第 {i + 1} 行存在“文本匹配”子条件但未填目标文字"); break; }
                    }
                    if (n.Type == QuickNodeType.LoopStart && n.LoopCount < 1)
                    { errors.Add($"第 {i + 1} 行循环次数需≥1"); break; }
                    if (n.Type == QuickNodeType.Jump && (n.JumpTarget < 0 || n.JumpTarget >= _flowNodes.Count))
                    { errors.Add($"第 {i + 1} 行跳转未选目标行"); break; }
                }
            }
            if (errors.Count > 0) { AppendLog("快捷操作：无法开始 - " + string.Join("；", errors)); return; }

            if (TimingHumanRadio?.IsChecked != false) ApplyTimingConfig();   // 人类化才读人类参数
            var nodes = _flowNodes.Select(x => x.Clone()).ToList();
            IntPtr hwndA = _boundAHwnd, hwndB = _boundBHwnd;

            var stopDto = ReadStopDto();
            var opts = new FlowRunOptions
            {
                UseHumanTiming = TimingFineRadio?.IsChecked != true,
            };
            opts.UseClicks = stopDto.UseClicks && stopDto.Clicks > 0; opts.StopClicks = stopDto.Clicks;
            opts.UseTriggers = stopDto.UseTriggers && stopDto.Triggers > 0; opts.StopTriggers = stopDto.Triggers;
            opts.UseRounds = stopDto.UseRounds && stopDto.Rounds > 0; opts.StopRounds = stopDto.Rounds;
            opts.UseMinutes = stopDto.UseMinutes && stopDto.Minutes > 0; opts.StopMinutes = stopDto.Minutes;

            _stopAll = false;
            _isRunning = true;
            DoRegisterHotkey();
            InstallHook();
            SetFlowEditingEnabled(false);
            StartFlowButton.Content = "运行中...";
            FlowStatusText.Text = "运行中";

            await System.Threading.Tasks.Task.Run(() => RunFlowEngine(nodes, hwndA, hwndB, opts));
        }

        private void RunFlowEngine(System.Collections.Generic.List<QuickFlowNode> nodes, IntPtr hwndA, IntPtr hwndB, FlowRunOptions opts)
        {
            int totalClicks = 0, totalHits = 0, totalRounds = 0;
            bool stopRequested = false;
            string stopReason = "";
            int n = nodes.Count;
            var elseFor = new int[n];
            var endFor = new int[n];
            var loopEndFor = new int[n];
            BuildBlockMaps(nodes, elseFor, endFor, loopEndFor);
            var prevIf = new bool[n];
            IntPtr HwndOf(TargetType t) => t == TargetType.A ? hwndA : hwndB;
            bool reactNext = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (!_stopAll && !stopRequested)
                {
                    int roundClicks = 0, roundHits = 0;
                    int i = 0;
                    long stepsThisRound = 0;
                    var loopStack = new System.Collections.Generic.List<(int start, int remain)>();
                    while (i < n && !_stopAll && !stopRequested)
                    {
                        stepsThisRound++;
                        if (stepsThisRound > 200000)
                        {
                            Dispatcher.Invoke(() => AppendLog("快捷操作：疑似跳转/循环死循环，强制结束本轮"));
                            break;
                        }
                        var nd = nodes[i];
                        switch (nd.Type)
                        {
                            case QuickNodeType.Click:
                                roundClicks += FireClickNode(nd, HwndOf(nd.Target), reactNext, opts.UseHumanTiming);
                                reactNext = false;
                                i++;
                                break;

                            case QuickNodeType.If:
                                {
                                    string text = OcrRegion(HwndOf(nd.Target), nd);
                                    bool cur = QuickFlowEval.EvaluateConditions(nd.Conditions, text);
                                    if (nd.StopWhenTrue && cur)
                                    {
                                        stopRequested = true;
                                        stopReason = $"第 {i + 1} 行检测满足({QuickFlowEval.DescribeConditions(nd.Conditions)}) → 停止信号";
                                        break;
                                    }
                                    bool runThen = false, runElse = false;
                                    if (nd.TriggerMode == CheckTriggerMode.EveryRound)
                                    {
                                        if (cur) runThen = true;
                                        else if (elseFor[i] >= 0) runElse = true;
                                    }
                                    else
                                    {
                                        bool prev = prevIf[i];
                                        if (!prev && cur) runThen = true;
                                        else if (prev && !cur && elseFor[i] >= 0) runElse = true;
                                        prevIf[i] = cur;
                                    }
                                    if (runThen)
                                    {
                                        roundHits++;
                                        if (text.Length > 0)
                                            Dispatcher.Invoke(() => AppendLog($"快捷操作：第 {i + 1} 行检测满足({QuickFlowEval.DescribeConditions(nd.Conditions)})，OCR={text}，走真分支"));
                                        i = i + 1;
                                        reactNext = true;
                                    }
                                    else if (runElse)
                                    {
                                        if (text.Length > 0)
                                            Dispatcher.Invoke(() => AppendLog($"快捷操作：第 {i + 1} 行检测不满足({QuickFlowEval.DescribeConditions(nd.Conditions)})，OCR={text}，走否则"));
                                        i = elseFor[i] + 1;
                                        reactNext = true;
                                    }
                                    else
                                    {
                                        i = (endFor[i] >= 0 ? endFor[i] : n) + 1;
                                    }
                                }
                                break;

                            case QuickNodeType.Else:
                                i = (endFor[i] >= 0 ? endFor[i] : n) + 1;   // 真分支落到否则 → 跳过否则块
                                break;

                            case QuickNodeType.LoopStart:
                                loopStack.Add((i, Math.Max(1, nd.LoopCount)));
                                i++;
                                break;

                            case QuickNodeType.LoopEnd:
                                if (loopStack.Count > 0)
                                {
                                    var top = loopStack[loopStack.Count - 1];
                                    if (top.remain > 1)
                                    {
                                        top.remain--;
                                        loopStack[loopStack.Count - 1] = top;
                                        i = top.start + 1;
                                    }
                                    else { loopStack.RemoveAt(loopStack.Count - 1); i++; }
                                }
                                else i++;
                                break;

                            case QuickNodeType.Jump:
                                if (nd.JumpTarget >= 0 && nd.JumpTarget < n) i = nd.JumpTarget;
                                else i++;
                                break;

                            default:   // End
                                i++;
                                break;
                        }
                    }

                    totalRounds++;
                    totalClicks += roundClicks;
                    totalHits += roundHits;
                    if (roundClicks > 0) HumanClicker.MaybeCheckRecords(1);
                    if (stopRequested)
                    {
                        Dispatcher.Invoke(() => AppendLog($"快捷操作：{stopReason}，停止"));
                        break;
                    }
                    if (roundClicks > 0 || roundHits > 0)
                        Dispatcher.Invoke(() => FlowStatusText.Text = $"运行中：点击 {totalClicks}｜命中 {totalHits}｜轮 {totalRounds}");

                    if (opts.UseClicks && totalClicks >= opts.StopClicks)
                    { Dispatcher.Invoke(() => AppendLog($"快捷操作：已达点击数 {opts.StopClicks}，停止")); break; }
                    if (opts.UseTriggers && totalHits >= opts.StopTriggers)
                    { Dispatcher.Invoke(() => AppendLog($"快捷操作：已达命中次数 {opts.StopTriggers}，停止")); break; }
                    if (opts.UseRounds && totalRounds >= opts.StopRounds)
                    { Dispatcher.Invoke(() => AppendLog($"快捷操作：已达轮数 {opts.StopRounds}，停止")); break; }
                    if (opts.UseMinutes && sw.Elapsed.TotalMinutes >= opts.StopMinutes)
                    { Dispatcher.Invoke(() => AppendLog($"快捷操作：已达运行时长 {opts.StopMinutes:0.##} 分钟，停止")); break; }

                    Thread.Sleep(HumanClicker.NextScanWaitMs());
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"快捷操作：流程异常 {ex.Message}"));
            }
            finally
            {
                _isRunning = false;
                Dispatcher.Invoke(() =>
                {
                    UnregisterStopHotkey();
                    SetFlowEditingEnabled(true);
                    if (StartFlowButton != null) StartFlowButton.Content = "开始执行";
                    if (FlowStatusText != null) FlowStatusText.Text = _stopAll ? "已停止" : "已结束";
                    AppendLog("流程执行结束");
                });
                UninstallHook();
            }
        }

        // 点击：人类化 走 HumanClicker；精细延迟 用节点自带延迟±随机/偏移/停留
        private int FireClickNode(QuickFlowNode nd, IntPtr hwnd, bool reactionDelay, bool humanTiming)
        {
            int mn = Math.Max(1, nd.RepeatMin);
            int mx = Math.Max(mn, nd.RepeatMax);
            int count = _rng.Next(mn, mx + 1);
            if (NativeMethods.IsIconic(hwnd)) { NativeMethods.ShowWindow(hwnd, 9); Thread.Sleep(200); }
            var wrect = NativeMethods.GetRect(hwnd);
            if (humanTiming)
            {
                Thread.Sleep(reactionDelay ? HumanClicker.ReactionDelayMs() : HumanClicker.InterClickMs());
                var center = new System.Drawing.Point(wrect.Left + nd.Point.X, wrect.Top + nd.Point.Y);
                return HumanClicker.ClickBurst(hwnd, center, count);
            }
            // 精细延迟模式
            Thread.Sleep(Math.Max(0, GetRandomVal(nd.DelayMs, nd.RandomDelay)));
            int dwell = Math.Max(10, GetRandomVal(nd.DwellMs, nd.RandomDwell));
            for (int k = 0; k < count; k++)
            {
                int offX = GetRandomVal(0, nd.RandomX);
                int offY = GetRandomVal(0, nd.RandomY);
                NativeMethods.ClickAtScreen(wrect.Left + nd.Point.X + offX, wrect.Top + nd.Point.Y + offY, dwell);
            }
            return count;
        }

        private string OcrRegion(IntPtr hwnd, QuickFlowNode nd)
        {
            try
            {
                if (NativeMethods.IsIconic(hwnd)) { NativeMethods.ShowWindow(hwnd, 9); Thread.Sleep(250); }
                return CaptureAndOcrRegion(hwnd, nd.Rect, false);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"快捷操作：OCR 出错 {ex.Message}"));
                return "";
            }
        }

        // 右侧队列随主 Tab 切换
        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool flow = MainTabs?.SelectedIndex == 0;
            if (QueueFlowPanel != null) QueueFlowPanel.Visibility = flow ? Visibility.Visible : Visibility.Collapsed;
            if (QueueStepsPanel != null) QueueStepsPanel.Visibility = flow ? Visibility.Collapsed : Visibility.Visible;
            if (flow) RefreshFlowList(false);
            else RefreshSteps();
        }
        private async void RunScriptB_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            var list = _steps.Where(s => s.Target == TargetType.B).ToList();
            if (list.Count == 0) { AppendLog("B：步骤为空"); return; }
            
            _stopAll = false;
            _isRunning = true;
            DoRegisterHotkey();
            InstallHook();
            
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string lastOcrB = OcrResultTextB?.Text ?? "";
                foreach (var step in list)
                {
                    if (_stopAll) break;
                    int delay = Math.Max(0, GetRandomVal(step.DelayMs, step.RandomDelay));
                    System.Threading.Thread.Sleep(delay);
                    
                    if (step.Type == ActionType.Click)
                    {
                        var wrect = NativeMethods.GetRect(_boundBHwnd);
                        int offX = GetRandomVal(0, step.RandomX);
                        int offY = GetRandomVal(0, step.RandomY);
                        int sx = wrect.Left + step.Point.X + offX;
                        int sy = wrect.Top + step.Point.Y + offY;
                        
                        if (NativeMethods.IsIconic(_boundBHwnd)) NativeMethods.ShowWindow(_boundBHwnd, 9);
                        NativeMethods.SetForegroundWindow(_boundBHwnd);
                        
                        int dwell = Math.Max(0, GetRandomVal(step.DwellMs, step.RandomDwell));
                        NativeMethods.ClickAtScreen(sx, sy, dwell);
                        Dispatcher.Invoke(() => AppendLog($"B执行：点击 {sx},{sy} (延{delay} 停{dwell})"));
                    }
                    else if (step.Type == ActionType.Ocr)
                    {
                        using var bmp = NativeMethods.CaptureWindow(_boundBHwnd);
                        using var mat = BitmapConverter.ToMat(bmp);
                        var r = step.Rect;
                        var x = Math.Max(0, Math.Min(mat.Cols - 1, r.X));
                        var y = Math.Max(0, Math.Min(mat.Rows - 1, r.Y));
                        var w = Math.Max(1, Math.Min(mat.Cols - x, r.Width));
                        var h = Math.Max(1, Math.Min(mat.Rows - y, r.Height));
                        var roi = new OpenCvSharp.Rect(x, y, w, h);
                        using var matRoi = new Mat(mat, roi);
                        var ocr = PerformOcrWithCache(step, matRoi);
                        lastOcrB = ocr.text;
                        Dispatcher.Invoke(() => SetOcrResult(OcrResultTextB, ocr.text));
                        var tag = step.ReuseOcrOnRoiUnchanged ? (ocr.reused ? "(复用)" : "(重新识别)") : "";
                        Dispatcher.Invoke(() => AppendLog($"B执行：识别{tag} {ocr.text}"));
                    }
                    else if (step.Type == ActionType.Condition)
                    {
                        string text = (!string.IsNullOrWhiteSpace(step.Key) && _vars.TryGetValue(step.Key, out var v)) ? v : lastOcrB;
                        bool match = !string.IsNullOrEmpty(text) && System.Text.RegularExpressions.Regex.IsMatch(text, step.Pattern);
                        step.LastResult = match;
                        Dispatcher.Invoke(() => AppendLog($"B条件检查: {(match ? "匹配" : "不匹配")} 模式 {step.Pattern} 文本 {text}"));
                        Dispatcher.Invoke(() => RefreshSteps());
                        if ((step.JumpOnTrue && match) || (!step.JumpOnTrue && !match)) break;
                    }
                    else if (step.Type == ActionType.Expression)
                    {
                        bool ok = EvaluateExpression(step.Pattern, out string? errorMsg);
                        step.LastResult = ok;
                        if (errorMsg != null) Dispatcher.Invoke(() => AppendLog($"B表达式错误: {errorMsg}"));
                        Dispatcher.Invoke(() => AppendLog($"B表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}，跳出条件={ (step.JumpOnTrue ? "为真" : "为假") }"));
                        Dispatcher.Invoke(() => AppendExprLog($"B表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}"));
                        Dispatcher.Invoke(() => RefreshSteps());
                        if ((step.JumpOnTrue && ok) || (!step.JumpOnTrue && !ok)) break;
                    }
                    else if (step.Type == ActionType.KeyPress)
                    {
                        var keyStr = step.Key;
                        if (Enum.TryParse<System.Windows.Input.Key>(keyStr, true, out var k))
                        {
                            var vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
                            NativeMethods.keybd_event((byte)vk, 0, 0, 0);
                            if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
                            NativeMethods.keybd_event((byte)vk, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
                            Dispatcher.Invoke(() => AppendLog($"B执行按键：{keyStr}"));
                        }
                        else
                        {
                            Dispatcher.Invoke(() => AppendLog($"B未知按键：{keyStr}"));
                        }
                    }
                    else if (step.Type == ActionType.Network)
                    {
                        if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
                    }
                }
            }
            finally
            {
                _isRunning = false;
                Dispatcher.Invoke(() => UnregisterStopHotkey());
                UninstallHook();
            }
            });
        }

        private void ClearScriptB_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in _steps.Where(s => s.Target == TargetType.B).ToList())
            {
                _ocrRoiCache.Remove(s);
            }
            _steps.RemoveAll(s => s.Target == TargetType.B);
            AppendLog("B：已清空步骤");
            RefreshSteps();
        }

        private string FormatStep(ScriptStep s)
        {
            if (s.Type == ActionType.Click)
                return $"{s.Target} 延 {s.DelayMs}±{s.RandomDelay}ms → 点 ({s.Point.X}±{s.RandomX},{s.Point.Y}±{s.RandomY}) 停 {s.DwellMs}±{s.RandomDwell}ms";
            if (s.Type == ActionType.Condition)
                return $"条件: 模式 \"{s.Pattern}\" 来源键 \"{s.Key}\" 跳出={(s.JumpOnTrue ? "为真" : "为假")}";
            if (s.Type == ActionType.Save)
                return $"保存 {s.Target} 结果 到键 \"{s.Key}\"";
            if (s.Type == ActionType.Expression)
                return $"表达式条件: {s.Pattern} 跳出={(s.JumpOnTrue ? "为真" : "为假")}";
            if (s.Type == ActionType.LoopStart)
                return $"循环开始 次数 {s.Count}";
            if (s.Type == ActionType.LoopEnd)
                return $"循环结束";
            if (s.Type == ActionType.BringFront)
                return $"{s.Target} 置顶窗口";
            if (s.Type == ActionType.KeyPress)
                return $"按键 \"{s.Key}\" (按压 {s.DwellMs}ms)";
            if (s.Type == ActionType.Comment)
                return $"注释: {s.Pattern}";
            if (s.Type == ActionType.IfStart) return $"If {s.Pattern}";
            if (s.Type == ActionType.ElseIf) return $"ElseIf {s.Pattern}";
            if (s.Type == ActionType.Else) return "Else";
            if (s.Type == ActionType.EndIf) return "EndIf";
            if (s.Type == ActionType.BreakLoop) return "跳出循环";
            if (s.Type == ActionType.ContinueLoop) return "跳入下次循环";
            if (s.Type == ActionType.Goto) return $"跳转到 {s.Pattern}";
            if (s.Type == ActionType.Label) return $"标签 {s.Pattern}:";
            if (s.Type == ActionType.BreakBlock) return "跳出代码块(If/Else)";
            if (s.Type == ActionType.Network)
                return $"网络: {s.NetworkAdapterName} -> {(s.NetworkEnable ? "恢复" : "断开")}{(s.NetworkSync ? " (同步)" : "")}{(s.DelayMs > 0 ? $" 延{s.DelayMs}ms" : "")}{(s.DwellMs > 0 ? $" 停{s.DwellMs}ms" : "")}";
            return $"{s.Target} 延 {s.DelayMs}±{s.RandomDelay}ms → 识别{(s.OcrNumbersOnly ? "(仅数字)" : "")}{(s.ReuseOcrOnRoiUnchanged ? "(ROI复用)" : "")} 区域 ({s.Rect.X},{s.Rect.Y},{s.Rect.Width},{s.Rect.Height})";
        }

        private void RefreshSteps()
        {
            StepsList.Items.Clear();
            int indent = 0;
            foreach (var s in _steps)
            {
                int currentIndent = indent;
                if (s.Type == ActionType.ElseIf || s.Type == ActionType.Else)
                {
                    currentIndent = Math.Max(0, indent - 1);
                }
                else if (s.Type == ActionType.EndIf || s.Type == ActionType.LoopEnd)
                {
                    indent = Math.Max(0, indent - 1);
                    currentIndent = indent;
                }

                string prefix = new string(' ', currentIndent * 4);
                var item = new System.Windows.Controls.ListBoxItem
                {
                    Content = prefix + FormatStep(s)
                };
                if (s.Type == ActionType.Comment)
                {
                    item.Foreground = System.Windows.Media.Brushes.Gray;
                }
                StepsList.Items.Add(item);

                if (s.Type == ActionType.IfStart || s.Type == ActionType.LoopStart)
                {
                    indent++;
                }
            }
        }

        private void SetOcrResult(System.Windows.Controls.TextBlock? block, string text)
        {
            if (block == null) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                block.Text = "未识别到数据";
                block.Foreground = System.Windows.Media.Brushes.Red;
            }
            else
            {
                block.Text = text;
                block.Foreground = System.Windows.Media.Brushes.Black;
            }
        }

        private void StepsList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(StepsList);
            var idx = GetIndexAtPoint(StepsList, pos);
            if (idx >= 0)
            {
                var item = StepsList.Items[idx];
                if (StepsList.SelectedItems.Contains(item))
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                    {
                        _lastMouseDown = pos;
                        e.Handled = true;
                        StepsList.Focus();
                    }
                }
            }
        }

        private void StepsList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_lastMouseDown != null)
            {
                var pos = e.GetPosition(StepsList);
                var idx = GetIndexAtPoint(StepsList, pos);
                if (idx >= 0)
                {
                    StepsList.SelectedItems.Clear();
                    StepsList.SelectedItems.Add(StepsList.Items[idx]);
                }
                _lastMouseDown = null;
            }
        }

        private void StepsList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                if (_lastMouseDown != null)
                {
                    var current = e.GetPosition(StepsList);
                    if (Math.Abs(current.X - _lastMouseDown.Value.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(current.Y - _lastMouseDown.Value.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        StartDrag(current);
                        _lastMouseDown = null;
                    }
                }
                else if (StepsList.SelectedItems.Count > 0)
                {
                    StartDrag(e.GetPosition(StepsList));
                }
            }
        }

        private void StartDrag(System.Windows.Point pos)
        {
            var idx = GetIndexAtPoint(StepsList, pos);
            if (idx >= 0 && StepsList.SelectedItems.Contains(StepsList.Items[idx]))
            {
                var indices = StepsList.SelectedItems.Cast<System.Windows.Controls.ListBoxItem>()
                               .Select(item => StepsList.Items.IndexOf(item))
                               .OrderBy(i => i).ToList();
                System.Windows.DragDrop.DoDragDrop(StepsList, indices, System.Windows.DragDropEffects.Move);
            }
        }
        private void StepsList_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(System.Collections.Generic.List<int>)))
            {
                var indices = (System.Collections.Generic.List<int>)e.Data.GetData(typeof(System.Collections.Generic.List<int>));
                var targetIndex = GetIndexAtPoint(StepsList, e.GetPosition(StepsList));
                if (targetIndex < 0) targetIndex = _steps.Count; // Drop at end if invalid

                var itemsToMove = new System.Collections.Generic.List<ScriptStep>();
                foreach (var i in indices) itemsToMove.Add(_steps[i]);

                for (int i = indices.Count - 1; i >= 0; i--)
                {
                    _steps.RemoveAt(indices[i]);
                }

                int adjustment = indices.Count(i => i < targetIndex);
                int newTarget = Math.Max(0, targetIndex - adjustment);
                if (newTarget > _steps.Count) newTarget = _steps.Count;

                _steps.InsertRange(newTarget, itemsToMove);
                RefreshSteps();

                StepsList.SelectedItems.Clear();
                for (int i = 0; i < itemsToMove.Count; i++)
                {
                    StepsList.SelectedItems.Add(StepsList.Items[newTarget + i]);
                }
            }
        }
        private void StepsList_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
        }
        private int GetIndexAtPoint(System.Windows.Controls.ListBox list, System.Windows.Point p)
        {
            var hit = VisualTreeHelper.HitTest(list, p);
            if (hit == null) return -1;
            DependencyObject obj = hit.VisualHit;
            while (obj != null && obj is not System.Windows.Controls.ListBoxItem)
                obj = VisualTreeHelper.GetParent(obj);
            if (obj is System.Windows.Controls.ListBoxItem item)
            {
                return list.ItemContainerGenerator.IndexFromContainer(item);
            }
            return -1;
        }

        private void MenuItem_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (StepsList.SelectedItems.Count == 0) return;
            var indices = new System.Collections.Generic.List<int>();
            foreach (var item in StepsList.SelectedItems)
            {
                indices.Add(StepsList.Items.IndexOf(item));
            }
            indices.Sort();

            for (int i = indices.Count - 1; i >= 0; i--)
            {
                int idx = indices[i];
                if (idx >= 0 && idx < _steps.Count)
                {
                    _ocrRoiCache.Remove(_steps[idx]);
                    _steps.RemoveAt(idx);
                }
            }
            RefreshSteps();
            AppendLog($"已删除 {indices.Count} 个步骤");
        }

        private void MenuItem_ExecuteSelected_Click(object sender, RoutedEventArgs e)
        {
            var indices = new System.Collections.Generic.List<int>();
            foreach (var item in StepsList.SelectedItems)
            {
                indices.Add(StepsList.Items.IndexOf(item));
            }
            indices.Sort();

            var selected = new System.Collections.Generic.List<ScriptStep>();
            foreach (var idx in indices)
            {
                if (idx >= 0 && idx < _steps.Count) selected.Add(_steps[idx]);
            }

            if (selected.Count > 0)
            {
                AppendLog($"开始执行选中的 {selected.Count} 个步骤...");
                RunSteps(selected);
            }
            else
            {
                AppendLog("未选择要执行的步骤");
            }
        }

        private void MenuItem_Edit_Click(object sender, RoutedEventArgs e)
        {
            if (StepsList.SelectedIndex < 0) return;
            var step = _steps[StepsList.SelectedIndex];
            var editor = new EditStepWindow(step, GetNetworkAdapters());
            editor.Owner = this;
            if (editor.ShowDialog() == true)
            {
                RefreshSteps();
                AppendLog("已更新所选步骤");
            }
        }

        private void AddBreakLoop_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.BreakLoop });
            RefreshSteps();
            AppendLog("已添加跳出循环");
        }
        private void AddBreakBlock_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.BreakBlock });
            RefreshSteps();
            AppendLog("已添加跳出If/Else块");
        }

        private void ExprBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (ExprHelpText != null) ExprHelpText.Visibility = Visibility.Visible;
        }

        private void ExprBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ExprHelpText != null) ExprHelpText.Visibility = Visibility.Collapsed;
        }

        private void AddContinue_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.ContinueLoop });
            RefreshSteps();
            AppendLog("已添加跳入下次循环");
        }
        private void AddLabel_Click(object sender, RoutedEventArgs e)
        {
            var lbl = LabelBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(lbl)) { AppendLog("标签名不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.Label, Pattern = lbl });
            RefreshSteps();
            AppendLog($"已添加标签: {lbl}");
        }
        private void AddGoto_Click(object sender, RoutedEventArgs e)
        {
            var lbl = LabelBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(lbl)) { AppendLog("标签名不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.Goto, Pattern = lbl });
            RefreshSteps();
            AppendLog($"已添加跳转: {lbl}");
        }

        private void AddLoopStart_Click(object sender, RoutedEventArgs e)
        {
            int cnt = ParseInt(LoopInnerCount?.Text, 2);
            if (cnt < 1) cnt = 1;
            _steps.Add(new ScriptStep { Type = ActionType.LoopStart, Count = cnt });
            RefreshSteps();
            AppendLog($"已添加循环开始：{cnt}");
        }
        private void AddLoopEnd_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.LoopEnd });
            RefreshSteps();
            AppendLog("已添加循环结束");
        }

        private void AddSaveA_Click(object sender, RoutedEventArgs e)
        {
            var key = SaveKey?.Text ?? "";
            if (string.IsNullOrWhiteSpace(key)) { AppendLog("保存键不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.Save, Target = TargetType.A, Key = key, DelayMs = 0, DwellMs = 0 });
            RefreshSteps();
            AppendLog($"已添加保存A结果步骤：{key}");
        }

        private void AddSaveB_Click(object sender, RoutedEventArgs e)
        {
            var key = SaveKey?.Text ?? "";
            if (string.IsNullOrWhiteSpace(key)) { AppendLog("保存键不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.Save, Target = TargetType.B, Key = key, DelayMs = 0, DwellMs = 0 });
            RefreshSteps();
            AppendLog($"已添加保存B结果步骤：{key}");
        }

        private void AddKeyPressStep_Click(object sender, RoutedEventArgs e)
        {
            var key = KeyPressBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(key)) { AppendLog("按键名称不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.KeyPress, Key = key, DwellMs = 50 });
            RefreshSteps();
            AppendLog($"已添加按键步骤：{key}");
        }

        private void AddCommentStep_Click(object sender, RoutedEventArgs e)
        {
            var text = CommentText?.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) { AppendLog("注释内容不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.Comment, Pattern = text, DelayMs = 0, DwellMs = 0 });
            RefreshSteps();
            AppendLog($"已添加注释步骤：{text}");
        }

        private void MenuItem_Duplicate_Click(object sender, RoutedEventArgs e)
        {
            var selectedIndices = new System.Collections.Generic.List<int>();
            foreach (var item in StepsList.SelectedItems)
            {
                selectedIndices.Add(StepsList.Items.IndexOf(item));
            }
            selectedIndices.Sort();

            if (selectedIndices.Count == 0) return;

            // 获取要复制的步骤对象
            var selectedSteps = new System.Collections.Generic.List<ScriptStep>();
            foreach (var idx in selectedIndices)
            {
                if (idx >= 0 && idx < _steps.Count)
                {
                    selectedSteps.Add(_steps[idx]);
                }
            }

            // 找到插入位置：最后一个选中项的后面
            int lastIndex = selectedIndices[selectedIndices.Count - 1];

            foreach (var item in selectedSteps)
            {
                // 深拷贝
                var newItem = new ScriptStep
                {
                    Type = item.Type,
                    Target = item.Target,
                    Rect = item.Rect,
                    Point = item.Point,
                    DelayMs = item.DelayMs,
                    RandomDelay = item.RandomDelay,
                    DwellMs = item.DwellMs,
                    RandomDwell = item.RandomDwell,
                    RandomX = item.RandomX,
                    RandomY = item.RandomY,
                    Pattern = item.Pattern,
                    Key = item.Key,
                    Count = item.Count,
                    JumpOnTrue = item.JumpOnTrue,
                    OcrNumbersOnly = item.OcrNumbersOnly,
                    ReuseOcrOnRoiUnchanged = item.ReuseOcrOnRoiUnchanged,
                    NetworkAdapterName = item.NetworkAdapterName,
                    NetworkEnable = item.NetworkEnable,
                    NetworkSync = item.NetworkSync
                };
                
                _steps.Insert(lastIndex + 1, newItem);
                lastIndex++;
            }

            RefreshSteps();
            AppendLog($"已复制 {selectedSteps.Count} 个步骤");
        }

        private void NumberOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = new System.Text.RegularExpressions.Regex("[^0-9]+").IsMatch(e.Text);
        }

        // ============================
        // 定时任务相关
        // ============================
        private System.Windows.Threading.DispatcherTimer? _scheduleTimer;
        private DateTime? _scheduleStartTargetTime;
        private DateTime? _scheduleStopTargetTime;
        private bool _isStartScheduleEnabled = false;
        private bool _isStopScheduleEnabled = false;

        private void ToggleStartScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isStartScheduleEnabled)
            {
                // 停止启动定时
                _isStartScheduleEnabled = false;
                _scheduleStartTargetTime = null;
                ToggleStartScheduleButton.Content = "启用启动";
                AppendLog("已关闭定时启动");
            }
            else
            {
                // 启用启动定时
                string h = ScheduleStartH.Text.Trim().PadLeft(2, '0');
                string m = ScheduleStartM.Text.Trim().PadLeft(2, '0');
                string s = ScheduleStartS.Text.Trim().PadLeft(2, '0');
                string timeStr = $"{h}:{m}:{s}";

                if (!DateTime.TryParse(timeStr, out DateTime dt))
                {
                    AppendLog("启动时间格式错误，请使用 HH:mm:ss 格式");
                    MessageBox.Show("启动时间格式错误，请使用 HH:mm:ss 格式");
                    return;
                }

                DateTime now = DateTime.Now;
                var target = new DateTime(now.Year, now.Month, now.Day, dt.Hour, dt.Minute, dt.Second);
                if (target <= now)
                {
                    target = target.AddDays(1);
                }
                _scheduleStartTargetTime = target;
                _isStartScheduleEnabled = true;
                ToggleStartScheduleButton.Content = "取消启动";
                AppendLog($"已启用定时启动，目标时间：{target:yyyy-MM-dd HH:mm:ss}");
            }
            EnsureScheduleTimer();
            UpdateScheduleStatus();
        }

        private void ToggleStopScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isStopScheduleEnabled)
            {
                // 停止停止定时
                _isStopScheduleEnabled = false;
                _scheduleStopTargetTime = null;
                ToggleStopScheduleButton.Content = "启用停止";
                AppendLog("已关闭定时停止");
            }
            else
            {
                // 启用停止定时
                string h = ScheduleStopH.Text.Trim().PadLeft(2, '0');
                string m = ScheduleStopM.Text.Trim().PadLeft(2, '0');
                string s = ScheduleStopS.Text.Trim().PadLeft(2, '0');
                
                // Only enable stop time if at least one field is filled, or default to 00:00:00?
                // Actually the user probably wants to set a specific time.
                // If fields are empty, we can default to 00, but let's check if they are valid.
                
                string timeStr = $"{h.PadLeft(2, '0')}:{m.PadLeft(2, '0')}:{s.PadLeft(2, '0')}";

                if (!DateTime.TryParse(timeStr, out DateTime dt))
                {
                    AppendLog("停止时间格式错误，请使用 HH:mm:ss 格式");
                    MessageBox.Show("停止时间格式错误，请使用 HH:mm:ss 格式");
                    return;
                }

                DateTime now = DateTime.Now;
                var target = new DateTime(now.Year, now.Month, now.Day, dt.Hour, dt.Minute, dt.Second);
                if (target <= now)
                {
                    target = target.AddDays(1);
                }
                _scheduleStopTargetTime = target;
                _isStopScheduleEnabled = true;
                ToggleStopScheduleButton.Content = "取消停止";
                AppendLog($"已启用定时停止，目标时间：{target:yyyy-MM-dd HH:mm:ss}");
            }
            EnsureScheduleTimer();
            UpdateScheduleStatus();
        }

        private void EnsureScheduleTimer()
        {
            if (_isStartScheduleEnabled || _isStopScheduleEnabled)
            {
                if (_scheduleTimer == null)
                {
                    _scheduleTimer = new System.Windows.Threading.DispatcherTimer();
                    _scheduleTimer.Interval = TimeSpan.FromSeconds(1);
                    _scheduleTimer.Tick += ScheduleTimer_Tick;
                }
                if (!_scheduleTimer.IsEnabled)
                {
                    _scheduleTimer.Start();
                }
            }
            else
            {
                if (_scheduleTimer != null && _scheduleTimer.IsEnabled)
                {
                    _scheduleTimer.Stop();
                }
            }
        }

        private void ScheduleTimer_Tick(object? sender, EventArgs e)
        {
            var now = DateTime.Now;

            if (_isStartScheduleEnabled && _scheduleStartTargetTime.HasValue)
            {
                if (now >= _scheduleStartTargetTime.Value)
                {
                    AppendLog("定时启动时间到达，开始执行...");
                    RunScriptAll();
                    // 推迟到下一天
                    _scheduleStartTargetTime = _scheduleStartTargetTime.Value.AddDays(1);
                }
            }

            if (_isStopScheduleEnabled && _scheduleStopTargetTime.HasValue)
            {
                if (now >= _scheduleStopTargetTime.Value)
                {
                    AppendLog("定时停止时间到达，正在停止脚本...");
                    StopScript();
                    // 推迟到下一天
                    _scheduleStopTargetTime = _scheduleStopTargetTime.Value.AddDays(1);
                }
            }

            UpdateScheduleStatus();
        }

        private void UpdateScheduleStatus()
        {
            string status = "";
            if (_isStartScheduleEnabled && _scheduleStartTargetTime.HasValue)
            {
                status += $"启: {_scheduleStartTargetTime.Value:MM-dd HH:mm:ss} ";
            }
            else
            {
                status += "启: 未启用 ";
            }

            if (_isStopScheduleEnabled && _scheduleStopTargetTime.HasValue)
            {
                status += $"| 停: {_scheduleStopTargetTime.Value:MM-dd HH:mm:ss}";
            }
            else
            {
                status += "| 停: 未启用";
            }
            
            ScheduleStatusText.Text = status;
        }

        private void TimeInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                // Remove non-digits
                string text = new string(tb.Text.Where(char.IsDigit).ToArray());
                
                if (int.TryParse(text, out int val))
                {
                    // Check if it is Hour or Minute/Second based on Name
                    if (tb.Name.EndsWith("H")) // Hour
                    {
                        if (val > 23) text = "23";
                    }
                    else // Minute or Second
                    {
                        if (val > 59) text = "59";
                    }
                }
                
                if (text != tb.Text)
                {
                    tb.Text = text;
                    tb.SelectionStart = text.Length; // Restore cursor position
                }
            }
        }

        private void RunScriptAll()
        {
            if (_steps.Count == 0)
            {
                AppendLog("没有要执行的步骤");
                return;
            }
            RunSteps(_steps.ToList());
        }

        private void RunScriptAll_Click(object sender, RoutedEventArgs e)
        {
            RunScriptAll();
        }

        private async void RunSteps(System.Collections.Generic.List<ScriptStep> steps)
        {
            int loops = ParseInt(LoopCount?.Text, 1);
            bool breakOnEmpty = BreakOnEmpty?.IsChecked == true;
            _stopAll = false;
            _isRunning = true;
            DoRegisterHotkey(); // 开始运行时注册热键
            InstallHook();
            
            await System.Threading.Tasks.Task.Run(() =>
            {
                string lastOcrA = "", lastOcrB = "";
                try
                {
                    int n = steps.Count;

                    var loopEndByStart = new System.Collections.Generic.Dictionary<int, int>();
                    var labelIndex = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
                    var nextBranch = new System.Collections.Generic.Dictionary<int, int>();
                    var endIfByBranch = new System.Collections.Generic.Dictionary<int, int>();
                    var endIfContaining = new int[n];
                    for (int i = 0; i < n; i++) endIfContaining[i] = -1;

                    {
                        var loopStack = new System.Collections.Generic.Stack<int>();
                        for (int i = 0; i < n; i++)
                        {
                            var t = steps[i].Type;
                            if (t == ActionType.LoopStart) loopStack.Push(i);
                            else if (t == ActionType.LoopEnd)
                            {
                                if (loopStack.Count > 0)
                                {
                                    var start = loopStack.Pop();
                                    loopEndByStart[start] = i;
                                }
                            }
                            else if (t == ActionType.Label)
                            {
                                var name = steps[i].Pattern ?? "";
                                if (!string.IsNullOrWhiteSpace(name) && !labelIndex.ContainsKey(name))
                                {
                                    labelIndex[name] = i;
                                }
                            }
                        }
                    }

                    {
                        var ifStack = new System.Collections.Generic.Stack<(int ifStart, System.Collections.Generic.List<int> branches)>();
                        for (int i = 0; i < n; i++)
                        {
                            var t = steps[i].Type;
                            if (t == ActionType.IfStart)
                            {
                                ifStack.Push((i, new System.Collections.Generic.List<int> { i }));
                            }
                            else if (t == ActionType.ElseIf || t == ActionType.Else)
                            {
                                if (ifStack.Count > 0)
                                {
                                    var top = ifStack.Pop();
                                    top.branches.Add(i);
                                    ifStack.Push(top);
                                }
                            }
                            else if (t == ActionType.EndIf)
                            {
                                if (ifStack.Count > 0)
                                {
                                    var top = ifStack.Pop();
                                    for (int b = 0; b < top.branches.Count; b++)
                                    {
                                        var branchIdx = top.branches[b];
                                        endIfByBranch[branchIdx] = i;
                                        nextBranch[branchIdx] = (b + 1 < top.branches.Count) ? top.branches[b + 1] : i;
                                    }
                                }
                            }
                        }

                        var endStack = new System.Collections.Generic.Stack<int>();
                        for (int i = 0; i < n; i++)
                        {
                            var t = steps[i].Type;
                            if (t == ActionType.IfStart)
                            {
                                if (endIfByBranch.TryGetValue(i, out var endIfIdx))
                                {
                                    endStack.Push(endIfIdx);
                                }
                            }
                            if (endStack.Count > 0) endIfContaining[i] = endStack.Peek();
                            if (t == ActionType.EndIf && endStack.Count > 0)
                            {
                                var topEnd = endStack.Peek();
                                if (topEnd == i) endStack.Pop();
                            }
                        }
                    }

                    for (int i = 0; i < Math.Max(1, loops); i++)
                    {
                        bool breakAll = false;
                        var stack = new System.Collections.Generic.Stack<(int start, int remain)>();
                        int idx = 0;
                        bool forceEval = false;
                        while (idx < n)
                        {
                        if (_stopAll) { breakAll = true; break; }
                        var step = steps[idx];
                        int delay = Math.Max(0, GetRandomVal(step.DelayMs, step.RandomDelay));
                        System.Threading.Thread.Sleep(delay);
                        
                        if (step.Type == ActionType.Click)
                        {
                            var hwnd = step.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
                            if (hwnd == IntPtr.Zero) { AppendLog($"{step.Target}：未绑定窗口"); breakAll = true; break; }
                            var wrect = NativeMethods.GetRect(hwnd);
                            int offX = GetRandomVal(0, step.RandomX);
                            int offY = GetRandomVal(0, step.RandomY);
                            int sx = wrect.Left + step.Point.X + offX;
                            int sy = wrect.Top + step.Point.Y + offY;
                            
                            if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, 9);
                            NativeMethods.SetForegroundWindow(hwnd);
                            
                            int dwell = Math.Max(0, GetRandomVal(step.DwellMs, step.RandomDwell));
                            NativeMethods.ClickAtScreen(sx, sy, dwell);
                            AppendLog($"{step.Target}执行：点击 {sx},{sy} (延{delay} 停{dwell})");
                        }
                        else if (step.Type == ActionType.Ocr)
                        {
                            var hwnd = step.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
                            if (hwnd == IntPtr.Zero) { AppendLog($"{step.Target}：未绑定窗口"); breakAll = true; break; }
                            using var bmp = NativeMethods.CaptureWindow(hwnd);
                            using var mat = BitmapConverter.ToMat(bmp);
                            var r = step.Rect;
                            var x = Math.Max(0, Math.Min(mat.Cols - 1, r.X));
                            var y = Math.Max(0, Math.Min(mat.Rows - 1, r.Y));
                            var w = Math.Max(1, Math.Min(mat.Cols - x, r.Width));
                            var h = Math.Max(1, Math.Min(mat.Rows - y, r.Height));
                            var roi = new OpenCvSharp.Rect(x, y, w, h);
                            using var matRoi = new Mat(mat, roi);
                            var ocr = PerformOcrWithCache(step, matRoi);

                            if (step.Target == TargetType.A) { lastOcrA = ocr.text; Dispatcher.Invoke(() => SetOcrResult(OcrResultTextA, ocr.text)); }
                            else { lastOcrB = ocr.text; Dispatcher.Invoke(() => SetOcrResult(OcrResultTextB, ocr.text)); }
                            var tag = step.ReuseOcrOnRoiUnchanged ? (ocr.reused ? "(复用)" : "(重新识别)") : "";
                            string logText = $"{step.Target}执行：识别{tag} {ocr.text}";
                            if (string.IsNullOrWhiteSpace(ocr.text))
                            {
                                logText += " 未识别到数据 大概原因：识别区域过小或执行速度太快没有识别到对应图片";
                            }
                            AppendLog(logText);
                            if (breakOnEmpty && string.IsNullOrWhiteSpace(ocr.text)) { breakAll = true; break; }
                        }
                        else if (step.Type == ActionType.Save)
                        {
                            var txt = step.Target == TargetType.A ? lastOcrA : lastOcrB;
                            var savedVal = txt ?? "";
                            _vars[step.Key] = savedVal;
                            AppendLog($"保存 {step.Target} 结果到 {step.Key}: {savedVal}");
                        }
                        else if (step.Type == ActionType.Condition)
                        {
                            string text;
                            if (!string.IsNullOrWhiteSpace(step.Key) && _vars.TryGetValue(step.Key, out var v))
                                text = v;
                            else
                                text = !string.IsNullOrEmpty(lastOcrA) ? lastOcrA : lastOcrB;
                            bool match = !string.IsNullOrEmpty(text) && System.Text.RegularExpressions.Regex.IsMatch(text, step.Pattern);
                            step.LastResult = match;
                            AppendLog($"条件检查: {(match ? "匹配" : "不匹配")} 模式 {step.Pattern} 文本 {text}");
                            if ((step.JumpOnTrue && match) || (!step.JumpOnTrue && !match))
                            {
                                if (stack.Count > 0)
                                {
                                    bool skipped = false;
                                    var top = stack.Pop();
                                    if (loopEndByStart.TryGetValue(top.start, out var loopEndIdx))
                                    {
                                        idx = loopEndIdx + 1;
                                        skipped = true;
                                    }
                                    if (skipped) continue;
                                }
                                breakAll = true; break;
                            }
                        }
                        else if (step.Type == ActionType.KeyPress)
                        {
                            var keyStr = step.Key;
                            if (Enum.TryParse<System.Windows.Input.Key>(keyStr, true, out var k))
                            {
                                var vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
                                NativeMethods.keybd_event((byte)vk, 0, 0, 0);
                                if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
                                NativeMethods.keybd_event((byte)vk, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
                                AppendLog($"执行按键：{keyStr}");
                            }
                            else
                            {
                                AppendLog($"未知的按键名称：{keyStr}");
                            }
                        }
                        else if (step.Type == ActionType.Expression)
                        {
                            bool ok = EvaluateExpression(step.Pattern, out string? errorMsg);
                            step.LastResult = ok;
                            if (errorMsg != null)
                            {
                                AppendLog($"表达式错误: {errorMsg}");
                            }
                            AppendLog($"表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}，跳出条件={ (step.JumpOnTrue ? "为真" : "为假") }");
                            AppendExprLog($"表达式检查: {(ok ? "为真" : "为假")} 表达式 {step.Pattern}");
                            if ((step.JumpOnTrue && ok) || (!step.JumpOnTrue && !ok))
                            {
                                if (stack.Count > 0)
                                {
                                    bool skipped = false;
                                    var top = stack.Pop();
                                    if (loopEndByStart.TryGetValue(top.start, out var loopEndIdx))
                                    {
                                        idx = loopEndIdx + 1;
                                        skipped = true;
                                    }
                                    if (skipped) continue;
                                }
                                breakAll = true; break;
                            }
                        }
                        else if (step.Type == ActionType.BringFront)
                        {
                            var hwnd = step.Target == TargetType.A ? _boundAHwnd : _boundBHwnd;
                            if (hwnd == IntPtr.Zero) { AppendLog($"{step.Target}：未绑定窗口"); breakAll = true; break; }
                            if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, 9);
                            NativeMethods.SetForegroundWindow(hwnd);
                            AppendLog($"{step.Target}执行：置顶窗口");
                        }
                        else if (step.Type == ActionType.LoopStart)
                        {
                            stack.Push((idx, Math.Max(1, step.Count)));
                        }
                        else if (step.Type == ActionType.LoopEnd)
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                if (top.remain > 1)
                                {
                                    stack.Push((top.start, top.remain - 1));
                                    idx = top.start + 1;
                                    continue;
                                }
                            }
                        }
                        else if (step.Type == ActionType.IfStart)
                        {
                            bool res = EvaluateExpression(step.Pattern, out string? errorMsg);
                            if (errorMsg != null)
                            {
                                AppendLog($"If 检查: 错误 - {errorMsg} {step.Pattern}");
                            }
                            else
                            {
                                AppendLog($"If 检查: {(res ? "为真" : "为假")} {step.Pattern}");
                            }
                            if (!res)
                            {
                                if (nextBranch.TryGetValue(idx, out var nb)) idx = nb;
                                else idx = n;
                                forceEval = true;
                                continue;
                            }
                        }
                        else if (step.Type == ActionType.ElseIf)
                        {
                            if (forceEval)
                            {
                                forceEval = false;
                                bool res = EvaluateExpression(step.Pattern, out string? errorMsg);
                                if (errorMsg != null)
                                {
                                    AppendLog($"ElseIf 检查: 错误 - {errorMsg} {step.Pattern}");
                                }
                                else
                                {
                                    AppendLog($"ElseIf 检查: {(res ? "为真" : "为假")} {step.Pattern}");
                                }
                                if (!res)
                                {
                                    if (nextBranch.TryGetValue(idx, out var nb)) idx = nb;
                                    else idx = n;
                                    forceEval = true;
                                    continue;
                                }
                            }
                            else
                            {
                                if (endIfByBranch.TryGetValue(idx, out var endIf)) idx = endIf;
                                else if (endIfContaining[idx] >= 0) idx = endIfContaining[idx];
                                else idx = n;
                                continue;
                            }
                        }
                        else if (step.Type == ActionType.Else)
                        {
                            if (forceEval)
                            {
                                forceEval = false;
                            }
                            else
                            {
                                if (endIfContaining[idx] >= 0) idx = endIfContaining[idx];
                                else idx = n;
                                continue;
                            }
                        }
                        else if (step.Type == ActionType.EndIf)
                        {
                            forceEval = false;
                        }
                        else if (step.Type == ActionType.BreakLoop)
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                if (loopEndByStart.TryGetValue(top.start, out var loopEndIdx))
                                {
                                    idx = loopEndIdx + 1;
                                    AppendLog("执行：跳出循环");
                                    continue;
                                }
                            }
                            AppendLog("跳出循环失败：未在循环内");
                        }
                        else if (step.Type == ActionType.ContinueLoop)
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Peek();
                                if (loopEndByStart.TryGetValue(top.start, out var loopEndIdx))
                                {
                                    idx = loopEndIdx; // Jump to LoopEnd, which will handle decr and jump back
                                    AppendLog("执行：跳入下次循环");
                                    continue;
                                }
                            }
                             AppendLog("跳入下次循环失败：未在循环内");
                        }
                        else if (step.Type == ActionType.BreakBlock)
                        {
                             if (endIfContaining[idx] >= 0)
                             {
                                 idx = endIfContaining[idx];
                                 AppendLog("执行：跳出代码块");
                                 continue;
                             }
                             AppendLog("跳出代码块失败：未在 If/Else 块内");
                        }
                        else if (step.Type == ActionType.Goto)
                        {
                            var targetLabel = step.Pattern;
                            if (!string.IsNullOrWhiteSpace(targetLabel) && labelIndex.TryGetValue(targetLabel, out var targetIdx))
                            {
                                idx = targetIdx;
                                AppendLog($"执行：跳转到 {targetLabel}");
                                continue;
                            }
                            else
                            {
                                AppendLog($"跳转失败：找不到标签 {targetLabel}");
                                breakAll = true; break;
                            }
                        }
                        else if (step.Type == ActionType.Label)
                        {
                            // Do nothing
                        }
                        else if (step.Type == ActionType.Network)
                        {
                            if (step.DwellMs > 0) System.Threading.Thread.Sleep(step.DwellMs);
                        }
                        else if (step.Type == ActionType.Comment)
                        {
                        }
                        idx++;
                    }
                    if (breakAll) break;
                }
            }
            finally
            {
                _isRunning = false;
                Dispatcher.Invoke(() => UnregisterStopHotkey()); // 运行结束注销热键
                UninstallHook();
                AppendLog("全部步骤执行完毕/停止");
            }
            });
            _stopAll = false;
        }

        private bool EvaluateExpression(string expr, out string? errorMsg)
        {
            errorMsg = null;
            // 预处理：替换中文引号和双引号为单引号
            expr = expr.Replace("“", "'").Replace("”", "'").Replace("\"", "'");
            
            // 定义替换逻辑函数
            string BuildExpression(bool forceString, out Dictionary<string, string> currentMaps)
            {
                currentMaps = new Dictionary<string, string>();
                var maps = currentMaps; // capture for lambda
                
                // 匹配 字符串字面量(支持转义'') 或 变量名
                var tokenRegex = new System.Text.RegularExpressions.Regex(@"'(''|[^'])*'|(?<var>\b[A-Za-z_]\w*\b)");
                
                // 忽略的关键字
                var keywords = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { 
                    "AND", "OR", "NOT", "TRUE", "FALSE", "NULL", "LIKE", "IN", "IS" 
                };

                string replaced = tokenRegex.Replace(expr, m =>
                {
                    // 如果是字符串字面量，原样返回
                    if (m.Value.StartsWith("'")) return m.Value;

                    var name = m.Value;
                    if (keywords.Contains(name)) return name;

                    if (_vars.TryGetValue(name, out var val))
                    {
                        if (!forceString)
                        {
                            // 尝试解析为数字
                            if (double.TryParse(val, out double d))
                            {
                                 string s = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                 maps[name] = s;
                                 return s;
                            }
                        }
                        // 否则作为字符串，转义单引号
                        string strVal = "'" + (val ?? "").Replace("'", "''") + "'";
                        maps[name] = val ?? ""; 
                        return strVal;
                    }
                    
                    // 未找到变量，返回0
                    maps[name] = "0";
                    return forceString ? "'0'" : "0";
                });

                // 支持 C# 风格操作符
                return replaced.Replace("||", " OR ")
                               .Replace("&&", " AND ")
                               .Replace("!=", "<>")
                               .Replace("==", "=");
            }

            // 执行计算的辅助函数
            object RunCompute(string finalExpr)
            {
                var dt = new System.Data.DataTable();
                dt.Columns.Add("x", typeof(double));
                return dt.Compute(finalExpr, "");
            }

            Dictionary<string, string> maps;
            string finalExpr = BuildExpression(false, out maps);
            object? result = null;
            string usedExpr = finalExpr;

            // 检查是否包含字符串大小比较 (禁止 >, <, >=, <= 用于字符串)
            // 字符串字面量正则: '(''|[^'])*'
            // 比较运算符正则: (>=|<=|>(?![=])|<(?![=>]))  注意排除 = 和 <>
            // 匹配模式: 字符串+运算符 或 运算符+字符串
            var strCmpRegex = new System.Text.RegularExpressions.Regex(
                @"('(''|[^'])*'\s*(>=|<=|>(?![=])|<(?![=>])))|((>=|<=|>(?![=])|<(?![=>]))\s*'(''|[^'])*')");

            if (strCmpRegex.IsMatch(finalExpr))
            {
                errorMsg = "原因没有开启仅识别数字或未识别到数字，所以不支持大小比较运算 (>, <, >=, <=)";
                string msg = errorMsg;
                Dispatcher.Invoke(() => AppendExprLog($"表达式错误：{msg}"));
                return false;
            }

            try
            {
                result = RunCompute(finalExpr);
            }
            catch (Exception ex)
            {
                // 如果是类型不匹配错误，尝试强制字符串模式
                if (ex.Message.Contains("System.Int32") && ex.Message.Contains("System.String") || ex is System.Data.EvaluateException)
                {
                    try 
                    {
                        finalExpr = BuildExpression(true, out maps);
                        usedExpr = finalExpr;
                        result = RunCompute(finalExpr);
                    }
                    catch (Exception ex2)
                    {
                        errorMsg = ex2.Message;
                        Dispatcher.Invoke(() => AppendExprLog($"表达式错误(重试失败)：{ex2.Message}"));
                        return false;
                    }
                }
                else
                {
                    errorMsg = ex.Message;
                    Dispatcher.Invoke(() => AppendExprLog($"表达式错误：{ex.Message}"));
                    return false;
                }
            }

            // 记录日志 (使用最终成功的表达式)
            try
            {
                // 尝试计算每个子表达式的算术结果用于显示
                string displayReplaced = usedExpr;
                var dt = new System.Data.DataTable(); // New instance for display eval
                dt.Columns.Add("x", typeof(double));
                
                try
                {
                    // 1. 按逻辑运算符分割: AND, OR
                    var logicParts = System.Text.RegularExpressions.Regex.Split(usedExpr, @"( AND | OR )", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    for (int i = 0; i < logicParts.Length; i++)
                    {
                        var part = logicParts[i];
                        if (part.Trim().ToUpper() == "AND" || part.Trim().ToUpper() == "OR") continue;

                        // 2. 按关系运算符分割: <=, >=, <>, =, <, >
                        string[] ops = new[] { "<=", ">=", "<>", "=", "<", ">" };
                        string? foundOp = null;
                        foreach (var op in ops) { if (part.Contains(op)) { foundOp = op; break; } }

                        if (foundOp != null)
                        {
                            int idx = part.IndexOf(foundOp);
                            string left = part.Substring(0, idx);
                            string right = part.Substring(idx + foundOp.Length);

                            string valLeft = EvalSimple(left, dt);
                            string valRight = EvalSimple(right, dt);
                            logicParts[i] = $"{valLeft}{foundOp}{valRight}";
                        }
                        else
                        {
                            logicParts[i] = EvalSimple(part, dt);
                        }
                    }
                    displayReplaced = string.Join("", logicParts);
                }
                catch { }

                string mapping = string.Join(", ", maps.Where(kv => kv.Value != "").Select(kv => kv.Key + "=" + kv.Value));
                Dispatcher.Invoke(() => AppendExprLog($" 原='{expr}' | [{mapping}]\n 结果='{displayReplaced}' | {result}"));
            }
            catch { }

            if (result is bool b) return b;
            if (result is IConvertible c)
            {
                double v = c.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                return v != 0;
            }
            
            errorMsg = "计算结果无效";
            return false;
        }

        private string EvalSimple(string expr, System.Data.DataTable dt)
        {
            if (string.IsNullOrWhiteSpace(expr)) return expr;
            try
            {
                var res = dt.Compute(expr, "");
                if (res is IConvertible conv)
                {
                    double d = conv.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                    // 如果是整数，去掉小数点
                    if (Math.Abs(d % 1) < 1e-9) return ((long)d).ToString();
                    return d.ToString("0.##");
                }
                return res.ToString() ?? "";
            }
            catch 
            {
                return expr; 
            }
        }

        private class StepDto
        {
            public string Type { get; set; } = "";
            public string Target { get; set; } = "";
            public int DelayMs { get; set; }
            public int RandomDelay { get; set; }
            public int DwellMs { get; set; }
            public int RandomDwell { get; set; }
            public int RandomX { get; set; }
            public int RandomY { get; set; }
            public int Count { get; set; }
            public string Pattern { get; set; } = "";
            public string Key { get; set; } = "";
            public int RectX { get; set; }
            public int RectY { get; set; }
            public int RectW { get; set; }
            public int RectH { get; set; }
            public int PointX { get; set; }
            public int PointY { get; set; }
            public bool JumpOnTrue { get; set; }
            public bool OcrNumbersOnly { get; set; }
            public bool ReuseOcrOnRoiUnchanged { get; set; }
            public string NetworkAdapterName { get; set; } = "";
            public bool NetworkEnable { get; set; }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureSavedQueuesDir();
            LoadSavedScripts();
        }

        private void EnsureSavedQueuesDir()
        {
            if (!System.IO.Directory.Exists(_savedQueuesDir))
            {
                try { System.IO.Directory.CreateDirectory(_savedQueuesDir); } catch { }
            }
        }

        private void LoadSavedScripts()
        {
            if (QuickLoadCombo == null) return;
            try
            {
                EnsureSavedQueuesDir();
                var files = System.IO.Directory.GetFiles(_savedQueuesDir, "*.json");
                var items = files.Select(f => System.IO.Path.GetFileName(f)).ToList();
                QuickLoadCombo.ItemsSource = items;
            }
            catch (Exception ex)
            {
                AppendLog($"刷新队列列表失败: {ex.Message}");
            }
        }

        private void QuickLoadCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (QuickLoadCombo.SelectedItem is string filename)
            {
                var fullPath = System.IO.Path.Combine(_savedQueuesDir, filename);
                LoadStepsFromFile(fullPath);
            }
        }

        private void RefreshScripts_Click(object sender, RoutedEventArgs e)
        {
            LoadSavedScripts();
            AppendLog("已刷新队列列表");
        }

        private void OpenScriptDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureSavedQueuesDir();
                Process.Start("explorer.exe", _savedQueuesDir);
            }
            catch (Exception ex)
            {
                AppendLog($"无法打开目录: {ex.Message}");
            }
        }

        private void SaveSteps_Click(object sender, RoutedEventArgs e)
        {
            EnsureSavedQueuesDir();
            var dlg = new SaveFileDialog();
            dlg.Filter = "JSON 文件|*.json";
            dlg.FileName = "steps.json";
            dlg.InitialDirectory = _savedQueuesDir;
            if (dlg.ShowDialog() == true)
            {
                var stepList = _steps
                    .Where(s => s.Type != ActionType.Network)
                    .Select(s => new StepDto
                {
                    Type = s.Type.ToString(),
                    Target = s.Target.ToString(),
                    DelayMs = s.DelayMs,
                    RandomDelay = s.RandomDelay,
                    DwellMs = s.DwellMs,
                    RandomDwell = s.RandomDwell,
                    RandomX = s.RandomX,
                    RandomY = s.RandomY,
                    Count = s.Count,
                    Pattern = s.Pattern,
                    Key = s.Key,
                    RectX = s.Rect.X, RectY = s.Rect.Y, RectW = s.Rect.Width, RectH = s.Rect.Height,
                    PointX = s.Point.X, PointY = s.Point.Y,
                    JumpOnTrue = s.JumpOnTrue,
                    OcrNumbersOnly = s.OcrNumbersOnly,
                    ReuseOcrOnRoiUnchanged = s.ReuseOcrOnRoiUnchanged,
                    NetworkAdapterName = s.NetworkAdapterName,
                    NetworkEnable = s.NetworkEnable
                }).ToList();

                var inputDialog = new InputDialog("步骤队列使用事项", "请输入使用说明（可选）：");
                if (inputDialog.ShowDialog() == true)
                {
                    var fileData = new SavedFileDto
                    {
                        Note = inputDialog.InputText,
                        Steps = stepList
                    };
                    var json = JsonSerializer.Serialize(fileData, new JsonSerializerOptions { WriteIndented = true });
                    System.IO.File.WriteAllText(dlg.FileName, json);
                    AppendLog($"已保存队列：{dlg.FileName}");
                    LoadSavedScripts(); // Refresh list after save
                }
            }
        }

        private class SavedFileDto
        {
            public string Note { get; set; } = "";
            public System.Collections.Generic.List<StepDto> Steps { get; set; } = new();
        }

        private void LoadSteps_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = "JSON 文件|*.json";
            dlg.InitialDirectory = _savedQueuesDir;
            if (dlg.ShowDialog() == true)
            {
                LoadStepsFromFile(dlg.FileName);
            }
        }

        private void LoadStepsFromFile(string filePath)
        {
                try
                {
                    var json = System.IO.File.ReadAllText(filePath);
                    // 尝试作为新格式（带Note）解析
                    SavedFileDto? fileData = null;
                    try
                    {
                        fileData = JsonSerializer.Deserialize<SavedFileDto>(json);
                    }
                    catch
                    {
                        // 忽略解析错误，说明可能不是 SavedFileDto 格式
                    }

                    var list = fileData?.Steps;
                    string note = fileData?.Note ?? "";

                    // 如果解析失败或Steps为空，尝试旧格式（直接List）
                    if (list == null || list.Count == 0)
                    {
                        try 
                        {
                            list = JsonSerializer.Deserialize<System.Collections.Generic.List<StepDto>>(json);
                            note = ""; // 旧格式无说明
                        }
                        catch {}
                    }

                    if (list == null) list = new System.Collections.Generic.List<StepDto>();

                    _steps.Clear();
                    _ocrRoiCache.Clear();
                    foreach (var d in list)
                    {
                        if (string.Equals(d.Type, "Network", StringComparison.OrdinalIgnoreCase)) continue;
                        Enum.TryParse<ActionType>(d.Type, out var t);
                        Enum.TryParse<TargetType>(d.Target, out var tg);
                        var s = new ScriptStep
                        {
                            Type = t,
                            Target = tg,
                            DelayMs = d.DelayMs,
                            RandomDelay = d.RandomDelay,
                            DwellMs = d.DwellMs,
                            RandomDwell = d.RandomDwell,
                            RandomX = d.RandomX,
                            RandomY = d.RandomY,
                            Count = d.Count,
                            Pattern = d.Pattern ?? "",
                            Key = d.Key ?? "",
                            Rect = new System.Drawing.Rectangle(d.RectX, d.RectY, d.RectW, d.RectH),
                            Point = new System.Drawing.Point(d.PointX, d.PointY),
                            JumpOnTrue = d.JumpOnTrue,
                            OcrNumbersOnly = d.OcrNumbersOnly,
                            ReuseOcrOnRoiUnchanged = d.ReuseOcrOnRoiUnchanged,
                            NetworkAdapterName = d.NetworkAdapterName ?? "",
                            NetworkEnable = d.NetworkEnable
                        };
                        _steps.Add(s);
                    }
                    RefreshSteps();
                    
                    // 显示加载的说明
                    if (UsageNoteText != null)
                    {
                        UsageNoteText.Text = $"步骤队列使用事项：{(string.IsNullOrWhiteSpace(note) ? "(无)" : note)}";
                        UsageNoteText.Visibility = Visibility.Visible;
                    }

                    AppendLog($"已加载队列：{System.IO.Path.GetFileName(filePath)}");
                }
                catch (Exception ex)
                {
                    AppendLog($"加载失败：{ex.Message}");
                }
        }

        private void StopScript()
        {
            _stopAll = true;
            AppendLog("已请求停止全部步骤");
        }

        private void StopAll_Click(object sender, RoutedEventArgs e)
        {
            StopScript();
        }

        private bool _identifyingKey = false;

        private void IdentifyKey_Click(object sender, RoutedEventArgs e)
        {
            _identifyingKey = true;
            IdentifyKeyButton.Content = "请按键...";
            // 确保窗口获得焦点以接收键盘事件
            this.Focus();
        }

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_identifyingKey)
            {
                var key = (e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key);
                // 忽略单独的 Ctrl/Shift/Alt 键本身，除非用户就是想录制修饰键
                // 这里我们直接记录所有键
                KeyPressBox.Text = key.ToString();
                
                _identifyingKey = false;
                IdentifyKeyButton.Content = "识别按键";
                e.Handled = true;
                return;
            }

            if (_bindingHotkey)
            {
                if (IsModifier(e.Key))
                {
                    _pressedModsDuringBinding |= ToModifierFlag(e.Key);
                    e.Handled = true;
                    return;
                }
                else
                {
                    _nonModifierPressedDuringBinding = true;
                    _singleModifierOnly = false;
                    _stopKey = GetEventKey(e);
                    var modsNow = System.Windows.Input.Keyboard.Modifiers;
                    _stopCtrl = modsNow.HasFlag(System.Windows.Input.ModifierKeys.Control);
                    _stopShift = modsNow.HasFlag(System.Windows.Input.ModifierKeys.Shift);
                    _stopAlt = false;
                    _bindingHotkey = false;
                    RegisterStopHotkey();
                    var label = $"{(_stopCtrl ? "Ctrl+" : "")}{(_stopAlt ? "Alt+" : "")}{(_stopShift ? "Shift+" : "")}{_stopKey}";
                    AppendLog($"已绑定快捷键：{label}");
                    _pressedModsDuringBinding = System.Windows.Input.ModifierKeys.None;
                    _nonModifierPressedDuringBinding = false;
                    e.Handled = true;
                    return;
                }
            }
            bool ctrl = (e.KeyboardDevice.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
            bool alt = (e.KeyboardDevice.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0;
            bool shift = (e.KeyboardDevice.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            if (_singleModifierOnly)
            {
                if ((_singleModifier.HasFlag(System.Windows.Input.ModifierKeys.Control) && ctrl && !alt && !shift && IsModifier(e.Key)) ||
                    (_singleModifier.HasFlag(System.Windows.Input.ModifierKeys.Shift) && shift && !ctrl && !alt && IsModifier(e.Key)))
                {
                    _stopAll = true;
                    AppendLog("快捷键停止全部步骤");
                    e.Handled = true;
                    return;
                }
            }
            var ek = GetEventKey(e);
            if (ek == _stopKey && ctrl == _stopCtrl && alt == _stopAlt && shift == _stopShift)
            {
                _stopAll = true;
                AppendLog("快捷键停止全部步骤");
                e.Handled = true;
            }
        }
        protected override void OnPreviewKeyUp(System.Windows.Input.KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            if (_bindingHotkey && IsModifier(e.Key))
            {
                var flag = ToModifierFlag(e.Key);
                if (_pressedModsDuringBinding == flag && !_nonModifierPressedDuringBinding)
                {
                    if (flag.HasFlag(System.Windows.Input.ModifierKeys.Control) || flag.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                    {
                        _singleModifierOnly = true;
                        _singleModifier = flag;
                        _bindingHotkey = false;
                        RegisterStopHotkey();
                        var labelSingle = $"{(flag.HasFlag(System.Windows.Input.ModifierKeys.Control) ? "Ctrl" : "Shift")}";
                        AppendLog($"已绑定快捷键：{labelSingle}");
                        _pressedModsDuringBinding = System.Windows.Input.ModifierKeys.None;
                        _nonModifierPressedDuringBinding = false;
                        e.Handled = true;
                    }
                }
                else
                {
                    _pressedModsDuringBinding &= ~flag;
                }
            }
        }

        private void BindHotkey_Click(object sender, RoutedEventArgs e)
        {
            _bindingHotkey = true;
            _pressedModsDuringBinding = System.Windows.Input.ModifierKeys.None;
            _nonModifierPressedDuringBinding = false;
            AppendLog("请按下要绑定的停止快捷键：单独Ctrl/Shift或组合键");
            this.Focus();
        }

        private static bool IsModifier(System.Windows.Input.Key key)
        {
            return key == System.Windows.Input.Key.LeftCtrl
                || key == System.Windows.Input.Key.RightCtrl
                || key == System.Windows.Input.Key.LeftAlt
                || key == System.Windows.Input.Key.RightAlt
                || key == System.Windows.Input.Key.LeftShift
                || key == System.Windows.Input.Key.RightShift;
        }
        private static System.Windows.Input.ModifierKeys ToModifierFlag(System.Windows.Input.Key key)
        {
            if (key == System.Windows.Input.Key.LeftCtrl || key == System.Windows.Input.Key.RightCtrl) return System.Windows.Input.ModifierKeys.Control;
            if (key == System.Windows.Input.Key.LeftAlt || key == System.Windows.Input.Key.RightAlt) return System.Windows.Input.ModifierKeys.Alt;
            if (key == System.Windows.Input.Key.LeftShift || key == System.Windows.Input.Key.RightShift) return System.Windows.Input.ModifierKeys.Shift;
            return System.Windows.Input.ModifierKeys.None;
        }
        private static System.Windows.Input.Key GetEventKey(System.Windows.Input.KeyEventArgs e)
        {
            return e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        }

        private void ClearScriptAll_Click(object sender, RoutedEventArgs e)
        {
            _steps.Clear();
            _ocrRoiCache.Clear();
            RefreshSteps();
            if (UsageNoteText != null) UsageNoteText.Visibility = Visibility.Collapsed;
            AppendLog("已清空全部步骤");
        }
        private void BringATopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_boundAHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口A"); return; }
            _steps.Add(new ScriptStep { Target = TargetType.A, Type = ActionType.BringFront, DelayMs = ParseInt(DelayMsA?.Text, 0) });
            RefreshSteps();
            AppendLog("A步骤已添加：置顶窗口");
        }
        private void BringBTopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_boundBHwnd == IntPtr.Zero) { AppendLog("请先绑定窗口B"); return; }
            _steps.Add(new ScriptStep { Target = TargetType.B, Type = ActionType.BringFront, DelayMs = ParseInt(DelayMsB?.Text, 0) });
            RefreshSteps();
            AppendLog("B步骤已添加：置顶窗口");
        }

/*
        private void DetectGpu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                string info = "";
                foreach (var obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "Unknown";
                    string driver = obj["DriverVersion"]?.ToString() ?? "Unknown";
                    info += $"{name} (Driver: {driver}); ";
                }
                if (string.IsNullOrWhiteSpace(info)) info = "未检测到显卡信息";
                // GpuInfoText.Text = info;
                AppendLog($"显卡检测结果：{info}");
            }
            catch (Exception ex)
            {
                // GpuInfoText.Text = "检测失败";
                AppendLog($"显卡检测失败：{ex.Message}");
            }
        }

        private void CheckEnv_Click(object sender, RoutedEventArgs e)
        {
            // EnvCheckResultBox.Text = "正在运行环境检测脚本...";
            RunPythonScript("check_gpu_env.py");
        }

        private void InstallDirectMlEnv_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("即将执行以下操作：\n1. 卸载当前的 onnxruntime 相关库\n2. 安装 onnxruntime-directml (支持 AMD/Intel/NVIDIA)\n\n确定要继续吗？", "确认安装", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            RunPipCommand("uninstall onnxruntime onnxruntime-gpu onnxruntime-directml -y", () => 
            {
                RunPipCommand("install onnxruntime-directml", () => 
                {
                     Dispatcher.Invoke(() => MessageBox.Show("DirectML 环境安装完成！请重新运行检测脚本验证。", "完成", MessageBoxButton.OK, MessageBoxImage.Information));
                });
            });
        }

        private void InstallCpuEnv_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("即将切换回仅 CPU 模式。\n这会卸载 GPU 加速库并安装标准版 onnxruntime。\n\n确定要继续吗？", "确认安装", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            RunPipCommand("uninstall onnxruntime onnxruntime-gpu onnxruntime-directml -y", () => 
            {
                RunPipCommand("install onnxruntime", () => 
                {
                     Dispatcher.Invoke(() => MessageBox.Show("CPU 环境已恢复。", "完成", MessageBoxButton.OK, MessageBoxImage.Information));
                });
            });
        }

        private void RunPythonScript(string scriptName)
        {
             Task.Run(() => 
            {
                // 使用临时文件路径，避免污染程序目录
                var scriptPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"df_{Guid.NewGuid()}_{scriptName}");
                try
                {
                    // 始终从嵌入资源中释放最新版本
                    try 
                    {
                        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                        var resourceName = "WindowSpy." + scriptName;
                        using (var stream = assembly.GetManifestResourceStream(resourceName))
                        {
                            if (stream != null)
                            {
                                using (var fileStream = System.IO.File.Create(scriptPath))
                                {
                                    stream.CopyTo(fileStream);
                                }
                            }
                            else
                            {
                                // Dispatcher.Invoke(() => EnvCheckResultBox.Text = $"内部错误：未找到嵌入资源 {resourceName}");
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Dispatcher.Invoke(() => EnvCheckResultBox.Text = $"释放脚本失败: {ex.Message}");
                        return;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"\"{scriptPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };

                    // 尝试使用项目自带的嵌入式 Python
                    var embeddedPython = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "python.exe");
                    if (System.IO.File.Exists(embeddedPython))
                    {
                        psi.FileName = embeddedPython;
                    }
                    
                    using var proc = Process.Start(psi);
                    if (proc == null) 
                    {
                        // Dispatcher.Invoke(() => EnvCheckResultBox.Text = "无法启动 Python 进程");
                        return;
                    }

                    var output = proc.StandardOutput.ReadToEnd();
                    var err = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    Dispatcher.Invoke(() => 
                    {
                        // EnvCheckResultBox.Text = output + (string.IsNullOrEmpty(err) ? "" : "\n错误:\n" + err);
                        // EnvCheckResultBox.ScrollToEnd();
                        AppendLog("环境检测输出:\n" + output);
                    });
                }
                catch (Exception ex)
                {
                    // Dispatcher.Invoke(() => EnvCheckResultBox.Text = $"运行失败: {ex.Message}");
                }
                finally
                {
                    // 运行完后清理临时文件
                    try { if (System.IO.File.Exists(scriptPath)) System.IO.File.Delete(scriptPath); } catch { }
                }
            });
        }

        private void RunPipCommand(string args, Action? onComplete = null)
        {
            // Dispatcher.Invoke(() => EnvCheckResultBox.Text = $"正在执行: pip {args}...\n");
            
            Task.Run(() => 
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pip", // 假设 pip 在 PATH 中，或者使用 "python" Arguments = "-m pip ..."
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };
                    
                    // 尝试使用 python -m pip 以获得更好的兼容性
                    psi.FileName = "python";
                    psi.Arguments = $"-m pip {args}";

                    using var proc = Process.Start(psi);
                    if (proc == null) 
                    {
                        // Dispatcher.Invoke(() => EnvCheckResultBox.AppendText("\n无法启动 pip 进程"));
                        return;
                    }

                    // proc.OutputDataReceived += (s, e) => { if (e.Data != null) Dispatcher.Invoke(() => { EnvCheckResultBox.AppendText(e.Data + "\n"); EnvCheckResultBox.ScrollToEnd(); }); };
                    // proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Dispatcher.Invoke(() => { EnvCheckResultBox.AppendText(e.Data + "\n"); EnvCheckResultBox.ScrollToEnd(); }); };
                    
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit();

                    if (onComplete != null) onComplete();
                }
                catch (Exception ex)
                {
                    // Dispatcher.Invoke(() => EnvCheckResultBox.AppendText($"\n执行失败: {ex.Message}"));
                }
            });
        }
*/

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog($"打开链接失败：{ex.Message}");
                MessageBox.Show($"无法打开浏览器，请手动复制链接：\n{url}");
            }
        }

        private void AppendExprLog(string text)
        {
            lock (_exprLogLock)
            {
                _pendingExprLogs.Enqueue((DateTime.Now, text));
                if (_exprLogFlushScheduled) return;
                _exprLogFlushScheduled = true;
            }

            Dispatcher.BeginInvoke(new Action(FlushExprLogs));
        }
        private void AppendLog(string text)
        {
            lock (_logLock)
            {
                _pendingLogs.Enqueue((DateTime.Now, text));
                if (_logFlushScheduled) return;
                _logFlushScheduled = true;
            }

            Dispatcher.BeginInvoke(new Action(FlushLogs));
        }

        private void FlushLogs()
        {
            System.Collections.Generic.List<(DateTime ts, string text)> batch = new();
            lock (_logLock)
            {
                _logFlushScheduled = false;
                while (_pendingLogs.Count > 0 && batch.Count < 200)
                {
                    batch.Add(_pendingLogs.Dequeue());
                }
                if (_pendingLogs.Count > 0) _logFlushScheduled = true;
            }

            if (batch.Count > 0 && OutputBox != null)
            {
                var paragraph = OutputBox.Document.Blocks.FirstBlock as System.Windows.Documents.Paragraph;
                if (paragraph == null)
                {
                    paragraph = new System.Windows.Documents.Paragraph();
                    OutputBox.Document.Blocks.Add(paragraph);
                }

                foreach (var item in batch)
                {
                    var timestampRun = new System.Windows.Documents.Run(item.ts.ToString("HH:mm:ss") + " ") { Foreground = System.Windows.Media.Brushes.Black };
                    paragraph.Inlines.Add(timestampRun);

                    string[] logParts = System.Text.RegularExpressions.Regex.Split(item.text, @"( 未识别到数据 大概原因：识别区域过小或执行速度太快没有识别到对应图片| 未识别到数据 大概原因：识别区域过小或执行速度太快没有识别到对应应图片| 未识别到数据可能原因：识别区域过小或执行速度太快没有识别到对应应图片| 未识别到数据| 未识别到)");
                    foreach (var part in logParts)
                    {
                        if (string.IsNullOrEmpty(part)) continue;
                        if (part.StartsWith(" 未识别到"))
                        {
                            paragraph.Inlines.Add(new System.Windows.Documents.Run(part) { Foreground = System.Windows.Media.Brushes.Red });
                        }
                        else
                        {
                            paragraph.Inlines.Add(new System.Windows.Documents.Run(part) { Foreground = System.Windows.Media.Brushes.Black });
                        }
                    }
                    paragraph.Inlines.Add(new System.Windows.Documents.LineBreak());
                }

                if (paragraph.Inlines.Count > 2000)
                {
                    OutputBox.Document.Blocks.Clear();
                    paragraph = new System.Windows.Documents.Paragraph();
                    paragraph.Inlines.Add(new System.Windows.Documents.Run($"[系统] 日志过长已自动清理 ({DateTime.Now:HH:mm:ss})\n") { Foreground = System.Windows.Media.Brushes.Gray });
                    OutputBox.Document.Blocks.Add(paragraph);
                }

                OutputBox.ScrollToEnd();
            }

            if (batch.Count > 0)
            {
                try
                {
                    var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jietu");
                    System.IO.Directory.CreateDirectory(dir);
                    var log = System.IO.Path.Combine(dir, "app.log");
                    var sbFile = new System.Text.StringBuilder();
                    foreach (var item in batch)
                    {
                        sbFile.Append(item.ts.ToString("yyyy-MM-dd HH:mm:ss")).Append(' ').Append(item.text).Append('\n');
                    }
                    System.IO.File.AppendAllText(log, sbFile.ToString());
                }
                catch { }
            }

            lock (_logLock)
            {
                if (_logFlushScheduled)
                {
                    Dispatcher.BeginInvoke(new Action(FlushLogs));
                }
            }
        }

        private void FlushExprLogs()
        {
            System.Collections.Generic.List<(DateTime ts, string text)> batch = new();
            lock (_exprLogLock)
            {
                _exprLogFlushScheduled = false;
                while (_pendingExprLogs.Count > 0 && batch.Count < 200)
                {
                    batch.Add(_pendingExprLogs.Dequeue());
                }
                if (_pendingExprLogs.Count > 0) _exprLogFlushScheduled = true;
            }

            if (batch.Count > 0 && ExprLogBox != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in batch)
                {
                    sb.Append(item.ts.ToString("HH:mm:ss")).Append(' ').Append(item.text).Append('\n');
                }
                ExprLogBox.AppendText(sb.ToString());
                if (ExprLogBox.Text.Length > 20000)
                {
                    ExprLogBox.Text = "[系统] 日志过长已清理\n" + ExprLogBox.Text.Substring(ExprLogBox.Text.Length - 10000);
                }
                ExprLogBox.ScrollToEnd();
            }

            lock (_exprLogLock)
            {
                if (_exprLogFlushScheduled)
                {
                    Dispatcher.BeginInvoke(new Action(FlushExprLogs));
                }
            }
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            if (OutputBox != null)
            {
                var textRange = new System.Windows.Documents.TextRange(OutputBox.Document.ContentStart, OutputBox.Document.ContentEnd);
                if (!string.IsNullOrWhiteSpace(textRange.Text))
                {
                    try
                    {
                        Clipboard.SetText(textRange.Text);
                        MessageBox.Show("日志已复制到剪贴板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"复制失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }
        private void AddIfStep_Click(object sender, RoutedEventArgs e)
        {
            var expr = ExprBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(expr)) { AppendLog("If 表达式不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.IfStart, Pattern = expr });
            RefreshSteps();
            AppendLog($"已添加 If: {expr}");
        }
        private void AddElseIfStep_Click(object sender, RoutedEventArgs e)
        {
            var expr = ExprBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(expr)) { AppendLog("ElseIf 表达式不能为空"); return; }
            _steps.Add(new ScriptStep { Type = ActionType.ElseIf, Pattern = expr });
            RefreshSteps();
            AppendLog($"已添加 ElseIf: {expr}");
        }
        private void AddElseStep_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.Else });
            RefreshSteps();
            AppendLog("已添加 Else");
        }
        private void AddEndIfStep_Click(object sender, RoutedEventArgs e)
        {
            _steps.Add(new ScriptStep { Type = ActionType.EndIf });
            RefreshSteps();
            AppendLog("已添加 EndIf");
        }

        private int FindNextBranch(int startIdx)
        {
            int depth = 0;
            for (int i = startIdx + 1; i < _steps.Count; i++)
            {
                var t = _steps[i].Type;
                if (t == ActionType.IfStart) depth++;
                else if (t == ActionType.EndIf)
                {
                    if (depth == 0) return i;
                    depth--;
                }
                else if ((t == ActionType.ElseIf || t == ActionType.Else) && depth == 0)
                {
                    return i;
                }
            }
            return _steps.Count;
        }

        private int FindEndIf(int startIdx)
        {
            int depth = 0;
            for (int i = startIdx + 1; i < _steps.Count; i++)
            {
                var t = _steps[i].Type;
                if (t == ActionType.IfStart) depth++;
                else if (t == ActionType.EndIf)
                {
                    if (depth == 0) return i;
                    depth--;
                }
            }
            return _steps.Count;
        }
    }
}
