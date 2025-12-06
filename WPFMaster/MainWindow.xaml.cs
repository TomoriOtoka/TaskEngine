using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskEngine.Models;
using TaskEngine.Services;
using TaskEngine.Utils;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Timer = System.Timers.Timer;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace TaskEngine
{
    public partial class MainWindow : Window
    {
        private readonly string machineName = Environment.MachineName;
        private readonly FirebaseService firebase = new FirebaseService();

        private Timer refreshTimer;
        private int _refreshRunning = 0;

        private Dictionary<string, PCControls> _pcControls = new Dictionary<string, PCControls>();
        private Dictionary<string, Expander> _groupExpanders = new Dictionary<string, Expander>();
        private Dictionary<string, ToggleButton> _classModeButtons = new Dictionary<string, ToggleButton>(); // 👈
        private HashSet<string> activeToasts = new HashSet<string>();

        private Dictionary<string, List<TemperatureRecord>> _temperatureHistory = new Dictionary<string, List<TemperatureRecord>>();
        private Dictionary<string, bool> _expanderStates = new Dictionary<string, bool>();

        public MainWindow()
        {
            InitializeComponent();

            Loaded += (s, e) =>
            {
                var graph = new PcGraphWindow("pc-prueba");
                graph.Show();
            };

            FlashWindow(this, 10);
            TitleBlock.Text = $"MASTER: {machineName}";
            StartRefreshLoop();
        }

        private void StartRefreshLoop()
        {
            refreshTimer = new Timer(3000);
            refreshTimer.Elapsed += async (s, e) => await OnTimerElapsedAsync();
            refreshTimer.AutoReset = true;
            refreshTimer.Start();
            _ = RefreshAsync();
        }

        private async Task OnTimerElapsedAsync()
        {
            if (Interlocked.Exchange(ref _refreshRunning, 1) == 1) return;
            try
            {
                await RefreshAsync();
                await CheckTemperatureAlertsAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshRunning, 0);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        public const uint FLASHW_TRAY = 2;
        public const uint FLASHW_TIMERNOFG = 12;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        public void FlashWindow(Window window, int count = 5)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            FLASHWINFO fw = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                hwnd = hwnd,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = (uint)count,
                dwTimeout = 0
            };
            FlashWindowEx(ref fw);
        }

        private async Task RefreshAsync()
        {
            try
            {
                var machines = await firebase.GetAllMachinesAsync();
                if (machines == null)
                {
                    Dispatcher.Invoke(() => StatusBlock.Text = $"No hay datos (last: {DateTime.Now:HH:mm:ss})");
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    var groups = machines.Values
                        .GroupBy(pc => pc.Group ?? "Sin grupo")
                        .OrderBy(g => g.Key)
                        .ToList();

                    _expanderStates.Clear();
                    foreach (var kvp in _groupExpanders)
                    {
                        _expanderStates[kvp.Key] = kvp.Value.IsExpanded;
                    }

                    CardsPanel.Children.Clear();
                    _groupExpanders.Clear();
                    _classModeButtons.Clear(); //  Limpiar referencias

                    foreach (var group in groups)
                    {
                        string groupKey = group.Key;

                        // === HEADER CON MODO CLASE ===
                        // === Contenedor para el header personalizado ===
                        var headerContainer = new StackPanel { Orientation = Orientation.Horizontal };
                        var headerText = new TextBlock
                        {
                            Text = groupKey.ToUpper(),
                            FontSize = 20,
                            FontWeight = FontWeights.Bold,
                            Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        headerContainer.Children.Add(headerText);

                        var classModeButton = new ToggleButton
                        {
                            Content = "Modo Clase",
                            Margin = new Thickness(10, 0, 0, 0),
                            Padding = new Thickness(5, 2, 5, 2),
                            IsChecked = false
                        };
                        classModeButton.Checked += (s, e) => SetClassMode(groupKey, true);
                        classModeButton.Unchecked += (s, e) => SetClassMode(groupKey, false);

                        // Restaurar estado
                        var firstPc = group.FirstOrDefault();
                        if (firstPc != null)
                        {
                            classModeButton.IsChecked = firstPc.IsClassMode;
                        }

                        headerContainer.Children.Add(classModeButton);
                        _classModeButtons[groupKey] = classModeButton;

                        CardsPanel.Children.Add(headerContainer);

                        // === Expander SIN header (solo el contenido) ===
                        var labExpander = new Expander
                        {
                            IsExpanded = _expanderStates.GetValueOrDefault(groupKey, true),
                            Margin = new Thickness(0, 0, 0, 10)
                        };

                        var horizontalStack = new StackPanel { Orientation = Orientation.Horizontal };

                        foreach (var pc in group)
                        {
                            if (!_pcControls.TryGetValue(pc.PCName, out var pcControls))
                            {
                                var card = new Border
                                {
                                    CornerRadius = new CornerRadius(10),
                                    ClipToBounds = true,
                                    Opacity = 0,
                                    Margin = new Thickness(5),
                                    Background = new LinearGradientBrush(Colors.White, Color.FromRgb(200, 230, 255), new Point(0, 0), new Point(0, 1)),
                                    BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                                    BorderThickness = new Thickness(1),
                                    Width = 280,
                                    Tag = pc.PCName
                                };

                                var innerStack = new StackPanel { Margin = new Thickness(10) };
                                card.Child = innerStack;

                                var topPanel = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };

                                var nameText = new TextBlock
                                {
                                    Text = pc.PCName,
                                    FontSize = 18,
                                    FontWeight = FontWeights.Bold,
                                    VerticalAlignment = VerticalAlignment.Center
                                };
                                DockPanel.SetDock(nameText, Dock.Left);

                                var nicknameText = new TextBlock
                                {
                                    Text = string.IsNullOrEmpty(pc.Nickname) ? "Sin apodo" : pc.Nickname,
                                    FontSize = 12,
                                    Foreground = Brushes.Gray,
                                    FontStyle = FontStyles.Italic,
                                    Margin = new Thickness(0, 0, 0, 4)
                                };

                                var menuButton = new Button
                                {
                                    Content = "⋯",
                                    Width = 24,
                                    Height = 24,
                                    Margin = new Thickness(8, 0, 0, 0),
                                    Tag = pc.PCName,
                                    FontWeight = FontWeights.Normal,
                                    FontSize = 14
                                };
                                menuButton.Click += (s, e) => ShowContextMenu(s as Button);
                                DockPanel.SetDock(menuButton, Dock.Right);

                                topPanel.Children.Add(nameText);
                                topPanel.Children.Add(menuButton);
                                innerStack.Children.Add(topPanel);
                                innerStack.Children.Add(nicknameText);

                                var onlineText = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
                                var cpuText = new TextBlock();
                                var cpuBar = new WpfProgressBar { Height = 20, Margin = new Thickness(0, 2, 0, 2) };
                                var tempText = new TextBlock();
                                var ramText = new TextBlock();
                                var ramBar = new WpfProgressBar { Height = 20, Margin = new Thickness(0, 2, 0, 2) };
                                var diskText = new TextBlock();
                                var diskBar = new WpfProgressBar { Height = 20, Margin = new Thickness(0, 2, 0, 2) };
                                var lastText = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };

                                innerStack.Children.Add(onlineText);
                                innerStack.Children.Add(cpuText);
                                innerStack.Children.Add(cpuBar);
                                innerStack.Children.Add(tempText);
                                innerStack.Children.Add(ramText);
                                innerStack.Children.Add(ramBar);
                                innerStack.Children.Add(diskText);
                                innerStack.Children.Add(diskBar);
                                innerStack.Children.Add(lastText);

                                var forbiddenScroll = new ScrollViewer
                                {
                                    Height = 60,
                                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                                };
                                var forbiddenText = new TextBlock
                                {
                                    Foreground = Brushes.Red,
                                    FontSize = 12,
                                    TextWrapping = TextWrapping.Wrap
                                };
                                forbiddenScroll.Content = forbiddenText;
                                innerStack.Children.Add(forbiddenScroll);

                                _pcControls[pc.PCName] = new PCControls
                                {
                                    Card = card,
                                    CpuBar = cpuBar,
                                    RamBar = ramBar,
                                    DiskBar = diskBar,
                                    CpuText = cpuText,
                                    TempText = tempText,
                                    RamText = ramText,
                                    DiskText = diskText,
                                    OnlineText = onlineText,
                                    LastUpdateText = lastText,
                                    NicknameText = nicknameText,
                                    ForbiddenScroll = forbiddenScroll,
                                    ForbiddenText = forbiddenText,
                                    MenuButton = menuButton
                                };

                                card.BeginAnimation(Border.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5)));
                            }

                            // === Actualizar datos ===
                            bool isOnline = IsPCOnline(pc);
                            bool clockIssue = HasClockIssue(pc);

                            var controls = _pcControls[pc.PCName];
                            controls.OnlineText.Text = isOnline ? "ONLINE" : "OFFLINE";
                            controls.OnlineText.Foreground = isOnline ? Brushes.Green : Brushes.Red;
                            controls.NicknameText.Text = string.IsNullOrEmpty(pc.Nickname) ? "Sin apodo" : pc.Nickname;

                            if (clockIssue)
                            {
                                string toastKey = $"clock_issue_{pc.PCName}";
                                ShowToast("Error de hora", $"{pc.PCName}: Reloj del sistema incorrecto.", toastKey, 60000);
                            }

                            bool hasForbidden = isOnline && pc.ForbiddenProcesses?.Count > 0;
                            controls.ForbiddenText.Text = hasForbidden ? string.Join("\n", pc.ForbiddenProcesses) : "";
                            controls.ForbiddenScroll.Visibility = Visibility.Visible;

                            if (hasForbidden)
                            {
                                controls.Card.Background = new SolidColorBrush(Color.FromRgb(255, 230, 230));
                                controls.Card.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 50, 50));
                            }
                            else
                            {
                                controls.Card.Background = new LinearGradientBrush(Colors.White, Color.FromRgb(200, 230, 255), new Point(0, 0), new Point(0, 1));
                                controls.Card.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220));
                            }

                            controls.LastUpdateText.Text = $"Última actualización: {ToRelativeTime(pc.LastUpdate)}";
                            controls.CpuText.Text = $"CPU: {pc.CpuUsage}%";
                            controls.TempText.Text = $"Temp: {pc.CpuTemperature}°C";
                            controls.RamText.Text = $"RAM: {pc.RamUsagePercent}% ({pc.UsedRamMB}/{pc.TotalRamMB} MB)";
                            controls.DiskText.Text = $"DISCO: {pc.DiskUsagePercent}%";

                            controls.CurrentTemperature = pc.CpuTemperature;
                            controls.CurrentCpuUsage = pc.CpuUsage;

                            AnimateProgressBar(controls.CpuBar, pc.CpuUsage);
                            AnimateProgressBar(controls.RamBar, pc.RamUsagePercent);
                            AnimateProgressBar(controls.DiskBar, pc.DiskUsagePercent);

                            double cpu = pc.CpuUsage;
                            double ram = pc.RamUsagePercent;
                            double disk = pc.DiskUsagePercent;
                            bool isZeroData = Math.Abs(cpu) < 0.1 && Math.Abs(ram) < 0.1 && Math.Abs(disk) < 0.1;

                            if (isZeroData)
                            {
                                controls.ZeroDataCount++;
                                if (controls.ZeroDataCount >= 2)
                                {
                                    string toastKey = $"hardware_error_{pc.PCName}";
                                    if (!activeToasts.Contains(toastKey))
                                    {
                                        ShowToast("Error de hardware", $"{pc.PCName}: No se detectan métricas válidas.", toastKey, 10000);
                                    }
                                }
                            }
                            else
                            {
                                controls.ZeroDataCount = 0;
                            }

                            if (controls.Card.Parent != null)
                            {
                                var parent = (Panel)controls.Card.Parent;
                                parent.Children.Remove(controls.Card);
                            }
                            horizontalStack.Children.Add(controls.Card);
                        }

                        labExpander.Content = horizontalStack;
                        CardsPanel.Children.Add(labExpander);
                    }

                    StatusBlock.Text = $"Última actualización: {DateTime.Now:HH:mm:ss}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusBlock.Text = $"Error: {ex.Message}");
            }
        }

        // === ACTIVAR/DESACTIVAR MODO CLASE ===
        private async void SetClassMode(string groupKey, bool enable)
        {
            try
            {
                var machines = await firebase.GetAllMachinesAsync();
                if (machines == null) return;

                var pcsInGroup = machines.Values
                    .Where(pc => (pc.Group ?? "Sin grupo") == groupKey)
                    .ToList();

                if (!pcsInGroup.Any())
                {
                    MessageBox.Show($"No hay PCs en el laboratorio '{groupKey}'.", "Modo Clase", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Actualizar estado en Firebase
                foreach (var pc in pcsInGroup)
                {
                    pc.IsClassMode = enable;
                    await firebase.SetMachineAsync(pc.PCName, pc);
                }

                string action = enable ? "activado" : "desactivado";
                ShowToast("Modo Clase", $"Modo Clase {action} en '{groupKey}' para {pcsInGroup.Count} PCs.", $"classmode_{groupKey}", 5000);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Modo Clase", MessageBoxButton.OK, MessageBoxImage.Error);
                // Restaurar estado del botón
                if (_classModeButtons.TryGetValue(groupKey, out var button))
                {
                    button.IsChecked = !enable;
                }
            }
        }

        // === MENÚ CONTEXTUAL ===
        private void ShowContextMenu(Button sender)
        {
            if (sender?.Tag is string pcName)
            {
                var menu = new ContextMenu();

                var killItem = new MenuItem { Header = "Cerrar apps prohibidas" };
                killItem.Click += (s, e) => KillForbiddenProcesses(pcName);
                menu.Items.Add(killItem);

                var groupItem = new MenuItem { Header = "Cambiar grupo" };
                groupItem.Click += (s, e) => ChangeGroup(pcName);
                menu.Items.Add(groupItem);

                // === NUEVO: Ver gráficos ===
                var graphItem = new MenuItem { Header = "Ver gráficos" };
                graphItem.Click += (s, e) => OpenGraphWindow(pcName);
                menu.Items.Add(graphItem);

                menu.IsOpen = true;
                sender.ContextMenu = menu;
                menu.PlacementTarget = sender;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            }
        }

        private void OpenGraphWindow(string pcName)
        {
            var graphWindow = new PcGraphWindow(pcName);
            graphWindow.Show();
        }

        private async void KillForbiddenProcesses(string pcName)
        {
            string group = null;
            foreach (var kvp in _groupExpanders)
            {
                var _expander = kvp.Value;
                var stack = _expander.Content as StackPanel;
                if (stack?.Children.Cast<UIElement>().OfType<Border>().Any(b => b.Tag?.ToString() == pcName) == true)
                {
                    group = kvp.Key;
                    break;
                }
            }

            if (group != null && _groupExpanders.TryGetValue(group, out var expander) && !expander.IsExpanded)
            {
                MessageBox.Show($"El laboratorio '{group}' está colapsado.", "Acción bloqueada", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var res = MessageBox.Show($"¿Cerrar apps prohibidas en {pcName}?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;
                await firebase.SendClientCommandAsync(pcName, "KILL_FORBIDDEN");
                ShowToast("Comando enviado", $"Se envió comando a {pcName}", $"cmd_{pcName}_kill", 5000);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message);
            }
        }

        private async void ChangeGroup(string pcName)
        {
            var inputDialog = new InputDialog("Cambiar grupo", "Nuevo nombre de laboratorio:", "LAB C");
            if (inputDialog.ShowDialog() == true)
            {
                string newGroup = inputDialog.ResponseText.Trim();
                if (string.IsNullOrEmpty(newGroup)) return;

                try
                {
                    var pc = await firebase.GetMachineAsync(pcName);
                    if (pc == null)
                    {
                        MessageBox.Show($"PC '{pcName}' no encontrada.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    pc.Group = newGroup;
                    await firebase.SetMachineAsync(pcName, pc);
                    ShowToast("Grupo actualizado", $"PC '{pcName}' asignada a '{newGroup}'.", $"group_{pcName}", 5000);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error al actualizar grupo: " + ex.Message);
                }
            }
        }

        // === RESTO DE MÉTODOS (SIN CAMBIOS) ===
        private void AnimateProgressBar(WpfProgressBar bar, double value)
        {
            if (bar == null) return;
            var anim = new DoubleAnimation(Math.Max(0, Math.Min(100, value)), TimeSpan.FromSeconds(0.5))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            bar.BeginAnimation(WpfProgressBar.ValueProperty, anim);
        }

        private string ToRelativeTime(string isoDate)
        {
            if (!DateTime.TryParse(isoDate, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime utcTime))
                return "Fecha inválida";
            TimeSpan diff = DateTime.UtcNow - utcTime;
            if (diff.TotalSeconds < 60) return "hace unos segundos";
            if (diff.TotalMinutes < 60) return $"hace {(int)diff.TotalMinutes} minutos";
            if (diff.TotalHours < 24) return $"hace {(int)diff.TotalHours} horas";
            if (diff.TotalDays < 2) return "hace 1 día";
            return $"hace {(int)diff.TotalDays} días";
        }

        private bool IsPCOnline(PCInfo pc)
        {
            if (string.IsNullOrEmpty(pc.LastUpdate)) return false;
            if (!DateTime.TryParse(pc.LastUpdate, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime lastUpdate)) return false;
            if (lastUpdate > DateTime.UtcNow) return false;
            return (DateTime.UtcNow - lastUpdate).TotalSeconds <= 30;
        }

        private bool HasClockIssue(PCInfo pc)
        {
            if (string.IsNullOrWhiteSpace(pc.LastUpdate)) return false;
            if (!DateTime.TryParse(pc.LastUpdate, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime lastUpdate)) return false;
            double diffSeconds = Math.Abs((DateTime.UtcNow - lastUpdate).TotalSeconds);
            bool isRecent = diffSeconds <= 60;
            double diffHours = Math.Abs((DateTime.UtcNow - lastUpdate).TotalHours);
            return isRecent && diffHours > 2;
        }

        private async Task CheckTemperatureAlertsAsync()
        {
            var now = DateTime.UtcNow;
            foreach (var pc in _pcControls.Keys.ToList())
            {
                if (!_temperatureHistory.ContainsKey(pc))
                    _temperatureHistory[pc] = new List<TemperatureRecord>();

                var history = _temperatureHistory[pc];
                history.RemoveAll(r => (now - r.Timestamp).TotalMinutes > 10);

                if (_pcControls.TryGetValue(pc, out var controls))
                {
                    float temp = controls.CurrentTemperature;
                    if (temp > 0)
                    {
                        history.Add(new TemperatureRecord { Timestamp = now, Temp = temp });
                        var highTemp = history.Where(r => r.Temp >= 80).OrderBy(r => r.Timestamp).ToList();
                        if (highTemp.Count > 0 && (highTemp.Last().Timestamp - highTemp.First().Timestamp).TotalSeconds >= 300)
                        {
                            string toastKey = $"temp_alert_{pc}";
                            if (!activeToasts.Contains(toastKey))
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    ShowToast("Temperatura alta", $"{pc}: Temperatura > 80°C por más de 5 minutos.", toastKey, 10000);
                                });
                            }
                        }
                    }
                }
            }
        }

        private void ShowToast(string title, string message, string key, int durationMs = 10000)
        {
            if (activeToasts.Contains(key)) return;
            activeToasts.Add(key);

            const double spacing = 10;
            const double startBottom = 20;

            var toast = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 5),
                Opacity = 0
            };

            var panel = new StackPanel();
            toast.Child = panel;
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
            panel.Children.Add(new TextBlock { Text = message, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 250 });

            ToastContainer.Children.Add(toast);

            toast.Loaded += (s, e) =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    double h = toast.ActualHeight > 0 ? toast.ActualHeight : 70;
                    int index = ToastContainer.Children.Count - 1;
                    Canvas.SetRight(toast, 20);
                    Canvas.SetBottom(toast, startBottom + index * (h + spacing));
                    toast.BeginAnimation(Border.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)));
                }));
            };

            Task.Delay(durationMs).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5));
                    fadeOut.Completed += (s, e) =>
                    {
                        ToastContainer.Children.Remove(toast);
                        activeToasts.Remove(key);
                        for (int i = 0; i < ToastContainer.Children.Count; i++)
                        {
                            if (ToastContainer.Children[i] is Border t)
                            {
                                double h = t.ActualHeight > 0 ? t.ActualHeight : 70;
                                Canvas.SetBottom(t, startBottom + i * (h + spacing));
                            }
                        }
                    };
                    toast.BeginAnimation(Border.OpacityProperty, fadeOut);
                });
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            refreshTimer?.Stop();
            refreshTimer?.Dispose();
            base.OnClosed(e);
        }

        private class PCControls
        {
            public Border Card { get; set; }
            public WpfProgressBar CpuBar { get; set; }
            public WpfProgressBar RamBar { get; set; }
            public WpfProgressBar DiskBar { get; set; }
            public TextBlock CpuText { get; set; }
            public TextBlock TempText { get; set; }
            public TextBlock RamText { get; set; }
            public TextBlock DiskText { get; set; }
            public TextBlock OnlineText { get; set; }
            public TextBlock LastUpdateText { get; set; }
            public TextBlock NicknameText { get; set; }
            public TextBlock ForbiddenText { get; set; }
            public ScrollViewer ForbiddenScroll { get; set; }
            public Button MenuButton { get; set; }
            public int ZeroDataCount { get; set; } = 0;
            public float CurrentTemperature { get; set; }
            public float CurrentCpuUsage { get; set; }
        }

        public class TemperatureRecord
        {
            public DateTime Timestamp { get; set; }
            public float Temp { get; set; }
        }
    }
}