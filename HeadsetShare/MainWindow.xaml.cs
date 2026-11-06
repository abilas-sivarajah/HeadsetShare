using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HeadsetShare.Audio;

namespace HeadsetShare;

public partial class MainWindow : Window
{
    private const double CardWidth = 248;
    private const double CardHeight = 170;
    private const double DragThreshold = 6;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AudioRouter _router = new();
    private readonly DeviceService _devices = new();
    private readonly ObservableCollection<DeviceItem> _tray = [];
    private readonly ObservableCollection<TargetCard> _cards = [];
    private readonly DispatcherTimer _deviceDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private IReadOnlyList<AudioDeviceInfo> _knownDevices = [];
    private string? _sourceId;
    private string? _defaultId;
    private bool _paused;
    private bool _redirectFailed;
    private TimeSpan _lastFrame;
    private double _sourceLevel;

    private DeviceItem? _dragCandidate;
    private Point _dragStart;
    private DeviceItem? _dragging;
    private DropZone _dragZone;

    public MainWindow()
    {
        InitializeComponent();

        _sourceId = _settings.SourceId;
        MuteSourceToggle.IsChecked = _settings.MuteSource;
        foreach (var saved in _settings.Targets)
        {
            _cards.Add(new TargetCard(saved, ScheduleSave));
        }

        TrayList.ItemsSource = _tray;
        CardsHost.ItemsSource = _cards;
        Stage.Cards = _cards;
        Stage.CardWidth = CardWidth;
        Stage.CardHeight = CardHeight;

        _deviceDebounce.Tick += (_, _) =>
        {
            _deviceDebounce.Stop();
            _ = RefreshDevicesAsync();
        };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SaveSettings();
        };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        // Windows ruft das auf einem Audio-Thread auf. Dort darf keine Audio-API benutzt werden.
        _devices.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            _deviceDebounce.Stop();
            _deviceDebounce.Start();
        });

        Loaded += async (_, _) =>
        {
            CompositionTarget.Rendering += OnFrame;
            _statusTimer.Start();
            await RefreshDevicesAsync();
        };
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : default;
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        Closing += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            SaveSettings();
            _router.Dispose();
            _devices.Dispose();
        };
    }

    private async Task RefreshDevicesAsync()
    {
        var (devices, defaultId) = await Task.Run(() =>
            (DeviceService.GetDevices(), DeviceService.GetDefaultDeviceId()));

        _knownDevices = devices;
        _defaultId = defaultId;

        _sourceId ??= ChooseInitialSource(devices, defaultId);

        RebuildTray();
        ApplyToRouter();
        UpdateStatus();
    }

    /// <summary>
    /// Am besten das Windows-Standardgerät, außer es ist ein Headset: Das soll ja mithören können.
    /// Dann ein anderes Gerät, und die App bietet an, den Windows-Ton dorthin umzustellen.
    /// </summary>
    private string? ChooseInitialSource(IReadOnlyList<AudioDeviceInfo> devices, string? defaultId)
    {
        var free = devices.Where(device => _cards.All(card => card.Id != device.Id)).ToList();
        var standard = free.FirstOrDefault(device => device.Id == defaultId);
        if (standard is not null && standard.Kind != DeviceKind.Headset)
        {
            return standard.Id;
        }

        return (free.FirstOrDefault(device => device.Kind == DeviceKind.Speaker)
            ?? free.FirstOrDefault(device => device.Kind == DeviceKind.Monitor)
            ?? standard
            ?? free.FirstOrDefault())?.Id;
    }

    private void RebuildTray()
    {
        _tray.Clear();
        foreach (var device in _knownDevices)
        {
            if (device.Id != _sourceId && _cards.All(card => card.Id != device.Id))
            {
                _tray.Add(new DeviceItem(device.Id, device.Name, device.Kind));
            }
        }

        TrayEmpty.Visibility = _tray.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var source = _knownDevices.FirstOrDefault(device => device.Id == _sourceId);
        SourceName.Text = source?.Name ?? (_sourceId is null ? "Bitte wählen" : "Nicht verbunden");
        SourceGlyph.Text = new DeviceItem(string.Empty, string.Empty, source?.Kind ?? DeviceKind.Speaker).Glyph;

        EmptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LayoutCards();
    }

    private void ApplyToRouter()
    {
        _router.Apply(new RouterPlan(
            _sourceId,
            _paused,
            MuteSourceToggle.IsChecked == true,
            _cards.Select(card => card.Settings).ToList()));
    }

    private void AddTarget(DeviceItem item)
    {
        if (_cards.Any(card => card.Id == item.Id))
        {
            return;
        }

        if (item.Id == _sourceId)
        {
            _sourceId = null;
        }

        _cards.Add(new TargetCard(
            new SavedTarget { Id = item.Id, Name = item.Name, Kind = (int)item.Kind },
            ScheduleSave));
        AfterChange();
    }

    private void RemoveTarget(string id)
    {
        var card = _cards.FirstOrDefault(candidate => candidate.Id == id);
        if (card is not null)
        {
            _cards.Remove(card);
            AfterChange();
        }
    }

    private void SetSource(DeviceItem item)
    {
        if (_cards.FirstOrDefault(card => card.Id == item.Id) is { } card)
        {
            _cards.Remove(card); // Eine Quelle darf kein Ziel sein, sonst gäbe es eine Rückkopplung.
        }

        _sourceId = item.Id;
        _redirectFailed = false;
        AfterChange();
    }

    private void AfterChange()
    {
        RebuildTray();
        ApplyToRouter();
        UpdateStatus();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void SaveSettings()
    {
        _settings.SourceId = _sourceId;
        _settings.MuteSource = MuteSourceToggle.IsChecked == true;
        _settings.Targets = _cards.Select(card => card.ToSaved()).ToList();
        _settings.Save();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // 30 Bilder pro Sekunde reichen für die Animation und halten die CPU-Last niedrig.
        if (e is not RenderingEventArgs args
            || WindowState == WindowState.Minimized
            || (args.RenderingTime - _lastFrame).TotalMilliseconds < 30)
        {
            return;
        }

        var seconds = Math.Min(0.1, (args.RenderingTime - _lastFrame).TotalSeconds);
        _lastFrame = args.RenderingTime;

        var ease = 1 - Math.Exp(-seconds * 11);
        foreach (var card in _cards)
        {
            card.X += (card.TargetX - card.X) * ease;
            card.Y += (card.TargetY - card.Y) * ease;
            card.Scale += (card.TargetScale - card.Scale) * ease;
            card.CardOpacity += (1 - card.CardOpacity) * ease;

            // Pegelanzeige: schnell hoch, langsam runter.
            var peak = _router.TakeTargetPeak(card.Id);
            card.Level = Math.Max(Shape(peak), card.Level * Math.Exp(-seconds * 5));
        }

        _sourceLevel = Math.Max(Shape(_router.TakeSourcePeak()), _sourceLevel * Math.Exp(-seconds * 4));
        Stage.SourceLevel = _sourceLevel;
        Stage.Live = _router.IsCapturing;
        Stage.Advance(seconds);
    }

    /// <summary>Audio-Pegel sind logarithmisch. So reagiert die Anzeige auch auf leisere Musik sichtbar.</summary>
    private static double Shape(float peak) => Math.Min(1, Math.Sqrt(Math.Max(0, peak)));

    private void UpdateStatus()
    {
        var active = 0;
        foreach (var card in _cards)
        {
            var status = _router.GetStatus(card.Id);
            var known = _knownDevices.Any(device => device.Id == card.Id);
            if (status.Active)
            {
                active++;
                var warn = status.Underruns > 0;
                card.State = warn ? TargetState.Warning : TargetState.Synced;
                card.Status = warn ? $"Synchron, {status.Underruns} Aussetzer" : "Synchron";
                card.StatusDetail = $"Clock-Abweichung ausgeglichen: {status.DriftPpm:+0;-0;0} ppm, Aussetzer: {status.Underruns}";
            }
            else if (_paused)
            {
                card.State = TargetState.Connecting;
                card.Status = "Pausiert";
                card.StatusDetail = string.Empty;
            }
            else if (!known || status.Error is not null)
            {
                card.State = TargetState.Offline;
                card.Status = known ? "Fehler" : "Nicht verbunden";
                card.StatusDetail = status.Error ?? "Das Gerät verbindet sich wieder, sobald es eingesteckt ist.";
            }
            else
            {
                card.State = TargetState.Connecting;
                card.Status = _sourceId is null ? "Wartet auf Quelle" : "Verbinde …";
                card.StatusDetail = string.Empty;
            }
        }

        UpdateStatusPill(active);
        UpdateBanner();
    }

    private void UpdateStatusPill(int active)
    {
        if (_paused)
        {
            SetPill("Pausiert", "Amber");
        }
        else if (_router.SourceError is not null || _sourceId is null)
        {
            SetPill("Keine Quelle", "Danger");
        }
        else if (active > 0)
        {
            SetPill(active == 1 ? "Live, 1 Gerät" : $"Live, {active} Geräte", "Teal");
        }
        else
        {
            SetPill("Bereit", "TextFaint");
        }
    }

    private void SetPill(string text, string brushKey)
    {
        StatusPillText.Text = text;
        StatusPillDot.Fill = (Brush)FindResource(brushKey);
    }

    private void UpdateBanner()
    {
        var source = _knownDevices.FirstOrDefault(device => device.Id == _sourceId);
        var defaultDevice = _knownDevices.FirstOrDefault(device => device.Id == _defaultId);

        if (_router.SourceError is { } error && !_paused)
        {
            ShowBanner($"Die Quelle konnte nicht abgegriffen werden: {error}", null);
        }
        else if (source is not null && defaultDevice is not null && source.Id != defaultDevice.Id)
        {
            ShowBanner(
                _redirectFailed
                    ? $"Windows hat die Umstellung abgelehnt. Bitte stell „{source.Name}“ in den Windows-Soundeinstellungen als Standard ein."
                    : $"Windows spielt gerade auf „{defaultDevice.Name}“. Verteilt wird nur der Ton von „{source.Name}“.",
                _redirectFailed ? null : "Windows-Ton hierher umstellen");
        }
        else
        {
            Banner.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowBanner(string text, string? action)
    {
        BannerText.Text = text;
        BannerAction.Content = action;
        BannerAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Banner.Visibility = Visibility.Visible;
        EmptyHint.Margin = new Thickness(0, 0, 0, 96);
    }

    private void LayoutCards()
    {
        var width = Stage.ActualWidth;
        var height = Stage.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var count = _cards.Count;
        var scale = count switch
        {
            <= 4 => 1.0,
            <= 6 => 0.88,
            _ => 0.76,
        };
        var cardWidth = CardWidth * scale;
        var cardHeight = CardHeight * scale;
        var radiusX = Math.Max(0, (width / 2) - (cardWidth / 2) - 18);
        var radiusY = Math.Max(0, (height / 2) - (cardHeight / 2) - 18);

        // Eins und zwei liegen rechts und links neben der Mitte, vier in den Ecken, sonst im Kreis ab oben.
        var startDegrees = count switch
        {
            1 or 2 => 0.0,
            4 => -45.0,
            _ => -90.0,
        };

        for (var i = 0; i < count; i++)
        {
            var angle = (startDegrees + (360.0 * i / count)) * Math.PI / 180;
            var card = _cards[i];
            card.TargetX = (width / 2) + (radiusX * Math.Cos(angle)) - (cardWidth / 2);
            card.TargetY = (height / 2) + (radiusY * Math.Sin(angle)) - (cardHeight / 2);
            card.TargetScale = scale;
            if (!card.HasPosition)
            {
                card.X = card.TargetX;
                card.Y = card.TargetY;
                card.HasPosition = true;
            }
        }
    }

    private void StageHost_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutCards();

    // Ziehen und Ablegen: bewusst selbst gebaut, damit das Gerät sichtbar der Maus folgt.
    private void DragSource_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceItem item } && !IsInsideInteractive(e.OriginalSource as DependencyObject))
        {
            _dragCandidate = item;
            _dragStart = e.GetPosition(Root);
        }
    }

    private static bool IsInsideInteractive(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or Slider or TextBoxBase or Thumb)
            {
                return true;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private void Root_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(Root);
        if (_dragging is null && _dragCandidate is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragCandidate = null;
                return;
            }

            if ((position - _dragStart).Length >= DragThreshold)
            {
                BeginDrag(_dragCandidate);
            }
        }

        if (_dragging is not null)
        {
            Canvas.SetLeft(Ghost, position.X + 14);
            Canvas.SetTop(Ghost, position.Y + 10);
            UpdateDropZone(ZoneAt(position));
        }
    }

    private void BeginDrag(DeviceItem item)
    {
        _dragging = item;
        _dragCandidate = null;
        Root.CaptureMouse();
        GhostGlyph.Text = item.Glyph;
        GhostName.Text = item.Name;
        Ghost.Visibility = Visibility.Visible;
        Stage.Dragging = true;
        if (item is TargetCard)
        {
            TrayDropHint.Visibility = Visibility.Visible;
        }
    }

    private void Root_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        if (_dragging is null)
        {
            return;
        }

        var item = _dragging;
        var zone = ZoneAt(e.GetPosition(Root));
        EndDrag();

        switch (zone)
        {
            case DropZone.Source:
                SetSource(item);
                break;
            case DropZone.Stage when item is not TargetCard:
                AddTarget(item);
                break;
            case DropZone.Tray when item is TargetCard:
                RemoveTarget(item.Id);
                break;
        }

        e.Handled = true;
    }

    private void Root_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging is not null)
        {
            EndDrag();
        }
    }

    private void EndDrag()
    {
        _dragging = null;
        Ghost.Visibility = Visibility.Collapsed;
        GhostHint.Visibility = Visibility.Collapsed;
        TrayDropHint.Visibility = Visibility.Collapsed;
        Stage.Dragging = false;
        Stage.HoverZone = DropZone.None;
        _dragZone = DropZone.None;
        if (Root.IsMouseCaptured)
        {
            Root.ReleaseMouseCapture();
        }
    }

    private void UpdateDropZone(DropZone zone)
    {
        if (zone == _dragZone)
        {
            return;
        }

        _dragZone = zone;
        Stage.HoverZone = zone;
        var isCard = _dragging is TargetCard;
        var hint = zone switch
        {
            DropZone.Source => "Wird zur Quelle",
            DropZone.Stage when !isCard => "Hört mit",
            DropZone.Tray when isCard => "Wird entfernt",
            _ => null,
        };
        GhostHint.Text = hint;
        GhostHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private DropZone ZoneAt(Point rootPosition)
    {
        var inStage = Root.TranslatePoint(rootPosition, Stage);
        if ((inStage - Stage.Center).Length <= StageView.OrbRadius + 14)
        {
            return DropZone.Source;
        }

        if (new Rect(0, 0, Stage.ActualWidth, Stage.ActualHeight).Contains(inStage))
        {
            return DropZone.Stage;
        }

        var inTray = Root.TranslatePoint(rootPosition, TrayPanel);
        return new Rect(0, 0, TrayPanel.ActualWidth, TrayPanel.ActualHeight).Contains(inTray)
            ? DropZone.Tray
            : DropZone.None;
    }

    private void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceItem item })
        {
            AddTarget(item);
        }
    }

    private void SetSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceItem item })
        {
            SetSource(item);
        }
    }

    private void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceItem item })
        {
            RemoveTarget(item.Id);
        }
    }

    private async void BannerAction_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceId is not { } sourceId)
        {
            return;
        }

        // Klappt es, meldet Windows die Änderung selbst, und der Hinweis verschwindet über DeviceService.Changed.
        _redirectFailed = !await Task.Run(() => WindowsDefaultDevice.TrySet(sourceId));
        UpdateStatus();
    }

    private void MuteSource_Click(object sender, RoutedEventArgs e)
    {
        ApplyToRouter();
        ScheduleSave();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _paused = PauseToggle.IsChecked == true;
        PauseGlyph.Text = _paused ? "" : "";
        PauseText.Text = _paused ? "Fortsetzen" : "Pause";
        ApplyToRouter();
        UpdateStatus();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
