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
        private Dictionary<string, ToggleButton> _classModeButtons = new Dictionary<string, ToggleButton>();
        private Dictionary<string, Button> _notificationButtons = new Dictionary<string, Button>();

        private Dictionary<string, List<TemperatureRecord>> _temperatureHistory = new Dictionary<string, List<TemperatureRecord>>();
        private Dictionary<string, bool> _expanderStates = new Dictionary<string, bool>();
        private Dictionary<string, string> _pcToGroupMap = new Dictionary<string, string>();

        // Sistema de notificaciones por laboratorio
        private Dictionary<string, List<Notification>> _labNotifications = new Dictionary<string, List<Notification>>();
        private HashSet<string> _sentNotificationKeys = new HashSet<string>(); // ✅ Para notificaciones únicas

        public MainWindow()
        {
            InitializeComponent();

            Loaded += (s, e) =>
            {
                // Notificaciones de bienvenida (solo una vez)
                AddLabNotification("Sin grupo", "Sistema iniciado", "Bienvenido al panel de monitoreo.");
            };

            FlashWindow(this, 10);
            TitleBlock.Text = $"MASTER: {machineName}";
            StartRefreshLoop();
        }

        private class Notification
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Message { get; set; }
            public DateTime Timestamp { get; set; }
            public bool IsRead { get; set; }
            public string PcName { get; set; }
        }

        private void StartRefreshLoop()
        {
            refreshTimer = new Timer(3000);
            refreshTimer.Elapsed += async (s, e) => await OnTimerElapsedAsync();
            refreshTimer.AutoReset = true;
            refreshTimer.Start();
            _ = RefreshAsync();

            var cleanupTimer = new Timer(TimeSpan.FromHours(1).TotalMilliseconds) { AutoReset = true };
            cleanupTimer.Elapsed += (s, e) => CleanupOldNotifications();
            cleanupTimer.Start();
        }

        private async Task OnTimerElapsedAsync()
        {
            if (Interlocked.Exchange(ref _refreshRunning, 1) == 1) return;
            try
            {
                await RefreshAsync();
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

        // Método principal para agregar notificaciones
        private void AddLabNotification(string groupKey, string title, string message, string pcName = null)
        {
            if (string.IsNullOrWhiteSpace(groupKey)) return;

            if (!_labNotifications.ContainsKey(groupKey))
                _labNotifications[groupKey] = new List<Notification>();

            var notification = new Notification
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                Message = message,
                Timestamp = DateTime.Now,
                IsRead = false,
                PcName = pcName
            };

            _labNotifications[groupKey].Add(notification);
            Dispatcher.Invoke(() => UpdateNotificationButton(groupKey));
        }

        // Método para agregar notificaciones únicas (evita duplicados)
        private void AddUniqueLabNotification(string groupKey, string title, string message, string pcName, string uniqueKey)
        {
            if (_sentNotificationKeys.Contains(uniqueKey)) return;

            AddLabNotification(groupKey, title, message, pcName);
            _sentNotificationKeys.Add(uniqueKey);
        }

        // Restablecer notificación cuando el estado se resuelve
        private void ResetNotificationKey(string uniqueKey)
        {
            _sentNotificationKeys.Remove(uniqueKey);
        }

        private void CleanupOldNotifications()
        {
            var cutoff = DateTime.Now.AddDays(-2);
            foreach (var group in _labNotifications.Keys.ToList())
            {
                _labNotifications[group].RemoveAll(n => n.Timestamp < cutoff);
                UpdateNotificationButton(group);
            }
        }

        private void UpdateNotificationButton(string groupKey)
        {
            if (!_notificationButtons.TryGetValue(groupKey, out var button)) return;

            var unreadCount = _labNotifications.GetValueOrDefault(groupKey, new List<Notification>())
                .Count(n => !n.IsRead);

            button.Content = unreadCount > 0 ? $"🔔 ({unreadCount})" : "🔔";

            if (unreadCount > 0)
            {
                var brush = new SolidColorBrush(Colors.Red);
                button.Foreground = brush;
                var animation = new ColorAnimation(Colors.Red, Colors.OrangeRed, TimeSpan.FromSeconds(1))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
            }
            else
            {
                button.Foreground = Brushes.Gray;
            }
        }

        private void ShowNotificationPanel(string groupKey)
        {
            var notifications = _labNotifications.GetValueOrDefault(groupKey, new List<Notification>());

            foreach (var n in notifications)
                n.IsRead = true;

            // Restablecer todas las claves de notificaciones para este laboratorio
            var keysToRemove = _sentNotificationKeys.Where(k => k.StartsWith($"{groupKey}_")).ToList();
            foreach (var key in keysToRemove)
                _sentNotificationKeys.Remove(key);

            UpdateNotificationButton(groupKey);

            var popup = new Window
            {
                Title = $"Notificaciones - {groupKey}",
                Width = 400,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = Brushes.White
            };

            var stackPanel = new StackPanel { Margin = new Thickness(10) };

            if (notifications.Count == 0)
            {
                stackPanel.Children.Add(new TextBlock { Text = "No hay notificaciones", FontSize = 16 });
            }
            else
            {
                foreach (var n in notifications.OrderByDescending(n => n.Timestamp))
                {
                    var card = new Border
                    {
                        BorderBrush = Brushes.Gray,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Margin = new Thickness(0, 0, 0, 10),
                        Padding = new Thickness(10)
                    };

                    var cardStack = new StackPanel();
                    cardStack.Children.Add(new TextBlock { Text = n.Title, FontWeight = FontWeights.Bold });
                    cardStack.Children.Add(new TextBlock { Text = n.Message, TextWrapping = TextWrapping.Wrap });
                    cardStack.Children.Add(new TextBlock
                    {
                        Text = n.Timestamp.ToString("dd/MM HH:mm"),
                        FontSize = 10,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(0, 5, 0, 0)
                    });

                    card.Child = cardStack;
                    stackPanel.Children.Add(card);
                }

                var clearButton = new Button
                {
                    Content = "Limpiar todas",
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right
                };
                clearButton.Click += (s, e) =>
                {
                    _labNotifications[groupKey].Clear();
                    popup.Close();
                    UpdateNotificationButton(groupKey);
                };
                stackPanel.Children.Add(clearButton);
            }

            popup.Content = new ScrollViewer { Content = stackPanel };
            popup.ShowDialog();
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

                // Actualizar mapeo PC → Grupo
                _pcToGroupMap.Clear();
                foreach (var pc in machines.Values)
                {
                    string group = pc.Group ?? "Sin grupo";
                    _pcToGroupMap[pc.PCName] = group;
                }

                Dispatcher.Invoke(() =>
                {
                    var groups = machines.Values
                        .GroupBy(pc => pc.Group ?? "Sin grupo")
                        .OrderBy(g => g.Key)
                        .ToList();

                    // Guardar estados de expanders existentes
                    _expanderStates.Clear();
                    foreach (var kvp in _groupExpanders)
                    {
                        _expanderStates[kvp.Key] = kvp.Value.IsExpanded;
                    }

                    CardsPanel.Children.Clear();
                    _groupExpanders.Clear();
                    _classModeButtons.Clear();
                    _notificationButtons.Clear();

                    foreach (var group in groups)
                    {
                        string groupKey = group.Key;

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

                        // ✅ Reutilizar o crear ToggleButton
                        ToggleButton classModeButton;
                        if (_classModeButtons.TryGetValue(groupKey, out var existingButton))
                        {
                            classModeButton = existingButton;
                            // Actualizar el estado desde Firebase (solo si no hay cambio pendiente)
                            var firstPc = group.FirstOrDefault();
                            if (firstPc != null)
                            {
                                classModeButton.IsChecked = firstPc.IsClassMode;
                            }
                        }
                        else
                        {
                            classModeButton = new ToggleButton
                            {
                                Content = "Modo Clase",
                                Margin = new Thickness(10, 0, 0, 0),
                                Padding = new Thickness(5, 2, 5, 2),
                                IsChecked = false
                            };
                            classModeButton.Checked += (s, e) => SetClassMode(groupKey, true);
                            classModeButton.Unchecked += (s, e) => SetClassMode(groupKey, false);

                            var firstPc = group.FirstOrDefault();
                            if (firstPc != null)
                            {
                                classModeButton.IsChecked = firstPc.IsClassMode;
                            }
                        }



                        headerContainer.Children.Add(classModeButton);

                        // === Después de crear el headerContainer y classModeButton ===

                        // ✅ Detectar cambios en el modo clase del grupo
                        string modeKey = $"modeclass_{groupKey}";
                        bool currentMode = group.FirstOrDefault()?.IsClassMode ?? false;

                        // Verificar si el estado del modo clase ha cambiado
                        bool shouldNotify = false;
                        bool wasModeActive = _sentNotificationKeys.Contains(modeKey);

                        if (currentMode && !wasModeActive)
                        {
                            // Modo clase se acaba de activar
                            shouldNotify = true;
                            _sentNotificationKeys.Add(modeKey);
                        }
                        else if (!currentMode && wasModeActive)
                        {
                            // Modo clase se acaba de desactivar
                            shouldNotify = true;
                            _sentNotificationKeys.Remove(modeKey);
                        }

                        if (shouldNotify)
                        {
                            string action = currentMode ? "activado" : "desactivado";
                            AddLabNotification(groupKey, "Modo Clase", $"Modo Clase {action} en '{groupKey}' para {group.Count()} PCs.");
                        }

                        // Botón de notificaciones
                        var notificationButton = new Button
                        {
                            Content = "🔔",
                            Margin = new Thickness(10, 0, 0, 0),
                            Padding = new Thickness(5, 2, 5, 2),
                            Background = Brushes.Transparent,
                            BorderBrush = Brushes.Transparent,
                            Foreground = Brushes.Gray // ✅ Establecer color inicial
                        };
                        notificationButton.Click += (s, e) => ShowNotificationPanel(groupKey);
                        headerContainer.Children.Add(notificationButton);

                        _notificationButtons[groupKey] = notificationButton;

                        // ✅ BOTÓN DE MENSAJE POR LABORATORIO
                        var messageButton = new Button
                        {
                            Content = "✉️",
                            Margin = new Thickness(5, 0, 0, 0),
                            Padding = new Thickness(5, 2, 5, 2),
                            Background = Brushes.Transparent,
                            BorderBrush = Brushes.Transparent,
                            ToolTip = "Enviar mensaje a este laboratorio"
                        };
                        messageButton.Click += (s, e) => SendLabMessage(groupKey);
                        headerContainer.Children.Add(messageButton);

                        // Inicializar bandeja si no existe
                        if (!_labNotifications.ContainsKey(groupKey))
                            _labNotifications[groupKey] = new List<Notification>();

                        // ✅ Forzar actualización inmediata del estado
                        UpdateNotificationButton(groupKey); // 👈 Esto evita el parpadeo

                        _classModeButtons[groupKey] = classModeButton;
                        _notificationButtons[groupKey] = notificationButton;

                        if (!_labNotifications.ContainsKey(groupKey))
                            _labNotifications[groupKey] = new List<Notification>();

                        CardsPanel.Children.Add(headerContainer);

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

                            // Actualizar datos
                            // === Actualizar datos ===
                            bool isOnline = IsPCOnline(pc);
                            bool clockIssue = HasClockIssue(pc);

                            var controls = _pcControls[pc.PCName];
                            controls.OnlineText.Text = isOnline ? "ONLINE" : "OFFLINE";
                            controls.OnlineText.Foreground = isOnline ? Brushes.Green : Brushes.Red;
                            controls.NicknameText.Text = string.IsNullOrEmpty(pc.Nickname) ? "Sin apodo" : pc.Nickname;

                            // ✅ Alerta de hora (automática)
                            if (clockIssue)
                            {
                                string clockKey = $"clock_{pc.PCName}";
                                if (!_sentNotificationKeys.Contains(clockKey))
                                {
                                    AddLabNotification(groupKey, "Error de hora", $"{pc.PCName}: Reloj del sistema incorrecto.");
                                    _sentNotificationKeys.Add(clockKey);
                                }
                            }
                            else
                            {
                                // Restablecer cuando el problema se resuelve
                                _sentNotificationKeys.Remove($"clock_{pc.PCName}");
                            }

                            bool hasForbidden = isOnline && pc.ForbiddenProcesses?.Count > 0;
                            controls.ForbiddenText.Text = hasForbidden ? string.Join("\n", pc.ForbiddenProcesses) : "";
                            controls.ForbiddenScroll.Visibility = Visibility.Visible;

                            // ✅ Alerta de apps prohibidas (automática)
                            string forbiddenKey = $"forbidden_{pc.PCName}";
                            if (hasForbidden)
                            {
                                if (!_sentNotificationKeys.Contains(forbiddenKey))
                                {
                                    AddLabNotification(groupKey, "App prohibida", $"{pc.PCName} está ejecutando apps prohibidas.", pc.PCName);
                                    _sentNotificationKeys.Add(forbiddenKey);
                                }
                            }
                            else
                            {
                                // Restablecer cuando ya no hay apps prohibidas
                                _sentNotificationKeys.Remove(forbiddenKey);
                            }

                            // ✅ Alerta de temperatura alta (automática)
                            string tempKey = $"temperature_{pc.PCName}";
                            if (pc.CpuTemperature >= 80)
                            {
                                if (!_sentNotificationKeys.Contains(tempKey))
                                {
                                    AddLabNotification(groupKey, "Temperatura alta", $"{pc.PCName}: Temperatura >= 80°C.", pc.PCName);
                                    _sentNotificationKeys.Add(tempKey);
                                }
                            }
                            else
                            {
                                // Restablecer cuando la temperatura baja
                                _sentNotificationKeys.Remove(tempKey);
                            }

                            // Resto del código de apariencia...
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

                            // ... resto del código ...

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

                            // ✅ Notificación única para hardware
                            string hardwareKey = $"hardware_{pc.PCName}";
                            if (isZeroData)
                            {
                                controls.ZeroDataCount++;
                                if (controls.ZeroDataCount >= 2)
                                {
                                    if (!_sentNotificationKeys.Contains(hardwareKey))
                                    {
                                        AddLabNotification(groupKey, "Error de hardware", $"{pc.PCName}: No se detectan métricas válidas.", pc.PCName);
                                        _sentNotificationKeys.Add(hardwareKey);
                                    }
                                }
                            }
                            else
                            {
                                controls.ZeroDataCount = 0;
                                ResetNotificationKey(hardwareKey);
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
                        _groupExpanders[groupKey] = labExpander; // ✅ Guardar referencia
                    }

                    StatusBlock.Text = $"Última actualización: {DateTime.Now:HH:mm:ss}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusBlock.Text = $"Error: {ex.Message}");
            }
        }

        // RESTO DEL CÓDIGO SIN CAMBIOS (SetClassMode, ShowContextMenu, etc.)
        // (Mantén exactamente como lo tienes, ya que no necesitan modificaciones)

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

                foreach (var pc in pcsInGroup)
                {
                    pc.IsClassMode = enable;
                    await firebase.SetMachineAsync(pc.PCName, pc);
                }

                string action = enable ? "activado" : "desactivado";
                if (_classModeButtons.TryGetValue(groupKey, out var button))
                {
                    button.IsChecked = enable;
                }


            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Modo Clase", MessageBoxButton.OK, MessageBoxImage.Error);
                if (_classModeButtons.TryGetValue(groupKey, out var button))
                {
                    button.IsChecked = !enable;
                }
            }
        }

        private async void SendLabMessage(string labName)
        {
            var inputDialog = new InputDialog($"Mensaje para {labName}", "Mensaje:", "");
            if (inputDialog.ShowDialog() == true)
            {
                string message = inputDialog.ResponseText.Trim();
                if (string.IsNullOrEmpty(message)) return;
                try
                {
                    await firebase.SendLabMessageAsync(labName, message, machineName);
                    AddLabNotification(labName, "Mensaje enviado", $"\"{message}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error al enviar mensaje: " + ex.Message);
                }
            }
        }

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

                // ✅ NUEVO: Cambiar nickname
                var nicknameItem = new MenuItem { Header = "Cambiar nickname" };
                nicknameItem.Click += (s, e) => ChangeNickname(pcName);
                menu.Items.Add(nicknameItem);

                var graphItem = new MenuItem { Header = "Ver gráficos" };
                graphItem.Click += (s, e) => OpenGraphWindow(pcName);
                menu.Items.Add(graphItem);

                menu.IsOpen = true;
                sender.ContextMenu = menu;
                menu.PlacementTarget = sender;
                menu.Placement = PlacementMode.Bottom;
            }
        }

        private async void ChangeNickname(string pcName)
        {
            // Obtener el nickname actual
            string currentNickname = "Sin apodo";
            try
            {
                var pc = await firebase.GetMachineAsync(pcName);
                currentNickname = pc?.Nickname ?? pcName;
            }
            catch { }

            var inputDialog = new InputDialog("Cambiar nickname", "Nuevo nickname:", currentNickname);
            if (inputDialog.ShowDialog() == true)
            {
                string newNickname = inputDialog.ResponseText.Trim();
                if (string.IsNullOrEmpty(newNickname)) return;

                try
                {
                    var pc = await firebase.GetMachineAsync(pcName);
                    if (pc == null)
                    {
                        MessageBox.Show($"PC '{pcName}' no encontrada.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    pc.Nickname = newNickname;
                    await firebase.SetMachineAsync(pcName, pc);

                    // ✅ Notificación para todos los Masters
                    AddLabNotification(_pcToGroupMap.GetValueOrDefault(pcName, "Sin grupo"),
                                     "Nickname actualizado",
                                     $"PC '{pcName}' ahora se llama '{newNickname}'.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error al actualizar nickname: " + ex.Message);
                }
            }
        }

        private async void SendGlobalMessage_Click(object sender, RoutedEventArgs e)
        {
            var inputDialog = new InputDialog("Mensaje global", "Mensaje para todas las PCs:", "");
            if (inputDialog.ShowDialog() == true)
            {
                string message = inputDialog.ResponseText.Trim();
                if (string.IsNullOrEmpty(message)) return;

                try
                {
                    await firebase.SendGlobalMessageAsync(message, machineName);
                    AddLabNotification("Todos", "Mensaje global enviado", $"\"{message}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error al enviar mensaje: " + ex.Message);
                }
            }
        }

        private void OpenGraphWindow(string pcName)
        {
            var graphWindow = new PcGraphWindow(pcName);
            graphWindow.Show();
        }

        private async void KillForbiddenProcesses(string pcName)
        {
            string group = _pcToGroupMap.GetValueOrDefault(pcName, "Sin grupo");

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
                // ✅ NOTIFICACIÓN MANUAL: siempre se envía
                AddLabNotification(group, "Comando enviado", $"Se envió comando a {pcName}.");
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
                    // ✅ NOTIFICACIÓN MANUAL: siempre se envía
                    AddLabNotification(newGroup, "Grupo actualizado", $"PC '{pcName}' asignada a '{newGroup}'.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error al actualizar grupo: " + ex.Message);
                }
            }
        }   

        private void AnimateProgressBar(WpfProgressBar bar, double value)
        {
            if (bar == null) return;
            var anim = new DoubleAnimation(Math.Max(0, Math.Min(100, value)), TimeSpan.FromSeconds(0.5))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            bar.BeginAnimation(WpfProgressBar.ValueProperty, anim);
        }

        private string ToRelativeTime(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "fecha inválida";

            DateTime parsedUtc;
            if (!DateTime.TryParse(
                    raw,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out parsedUtc))
            {
                return "fecha inválida";
            }

            TimeSpan diff = DateTime.UtcNow - parsedUtc;
            if (diff.TotalSeconds < 60)
                return "hace unos segundos";
            if (diff.TotalMinutes < 60)
                return $"hace {(int)diff.TotalMinutes} minutos";
            if (diff.TotalHours < 24)
                return $"hace {(int)diff.TotalHours} horas";
            return $"hace {(int)diff.TotalDays} días";
        }

        private bool IsPCOnline(PCInfo pc)
        {
            if (string.IsNullOrEmpty(pc.LastUpdate)) return false;

            // ✅ Parsear explícitamente como UTC
            if (!DateTime.TryParse(pc.LastUpdate,
                      System.Globalization.CultureInfo.InvariantCulture,
                      System.Globalization.DateTimeStyles.RoundtripKind,
                      out DateTime lastUpdate))
                return false;

            if (lastUpdate.Kind == DateTimeKind.Local)
                lastUpdate = lastUpdate.ToUniversalTime();
            else if (lastUpdate.Kind == DateTimeKind.Unspecified)
                lastUpdate = DateTime.SpecifyKind(lastUpdate, DateTimeKind.Utc);

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



        // CODIGOS DE PRUEBA

        // Al final de la clase MainWindow (antes de las clases anidadas)
        private void ForceForbiddenApps_Click(object sender, RoutedEventArgs e)
        {
            // Simular que pc-01 tiene apps prohibidas
            string groupKey = "LAB A";
            string pcName = "pc-01";
            string forbiddenKey = $"forbidden_{pcName}";

            if (!_sentNotificationKeys.Contains(forbiddenKey))
            {
                AddLabNotification(groupKey, "App prohibida", $"{pcName} está ejecutando Steam.", pcName);
                _sentNotificationKeys.Add(forbiddenKey);
            }
        }

        private void ForceHighTemp_Click(object sender, RoutedEventArgs e)
        {
            string groupKey = "LAB A";
            string pcName = "DESKTOP-O9FQ4QO";
            string tempKey = $"temperature_{pcName}";

            if (!_sentNotificationKeys.Contains(tempKey))
            {
                AddLabNotification(groupKey, "Temperatura alta", $"{pcName}: 85°C por más de 5 minutos.", pcName);
                _sentNotificationKeys.Add(tempKey);
            }
        }

        private void ForceOffline_Click(object sender, RoutedEventArgs e)
        {
            string groupKey = "LAB A";
            string pcName = "DESKTOP-O9FQ4QO";
            string offlineKey = $"offline_{pcName}";

            if (!_sentNotificationKeys.Contains(offlineKey))
            {
                AddLabNotification(groupKey, "PC Offline", $"{pcName} no responde desde hace 5 minutos.", pcName);
                _sentNotificationKeys.Add(offlineKey);
            }
        }

        //fin prueba

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