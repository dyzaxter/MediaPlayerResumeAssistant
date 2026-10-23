using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace MediaPlayerResumeAssistant;

public sealed class MainWindow : Window
{
    private static readonly JsonSerializerOptions StoreJsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DiskSaveInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MediaPropertiesRefreshInterval = TimeSpan.FromSeconds(60);

    private readonly DispatcherTimer _timer;
    private readonly TextBlock _statusText;
    private readonly TextBlock _fileText;
    private readonly TextBlock _positionText;
    private readonly Button _openLatestButton;
    private readonly ListBox _recentVideosList;
    private readonly string _storePath;
    private readonly string _settingsPath;
    private TextBlock? _uiScaleValueText;
    private readonly Dictionary<string, SavedPosition> _saved = new(StringComparer.OrdinalIgnoreCase);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _currentFile;
    private DateTime _launchTimeUtc;
    private DateTime _lastDiskSaveUtc = DateTime.MinValue;
    private long _lastRecordedTicks = -1;
    private bool _restoreTried;
    private bool _polling;
    private bool _dirty;
    private bool _ignoreCurrentSessionSaving;
    private bool _sessionTimeoutReported;
    private GlobalSystemMediaTransportControlsSession? _mediaPropertiesSession;
    private string? _activeSessionTitle;
    private DateTime _lastMediaPropertiesFetchUtc = DateTime.MinValue;
    private readonly ScaleTransform _uiScale = new(1.0, 1.0);
    private Slider? _uiScaleSlider;
    private string? _lastSeenTitle;
    private string? _titleBeforeLaunch;
    private string? _fileBeforeLaunch;

    public MainWindow(string[] args)
    {
        Title = "Media Player Resume Assistant";
        using (var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream)
            Icon = BitmapFrame.Create(iconStream);
        Width = 1080;
        Height = 700;
        MinWidth = 850;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(20, 19, 23));
        Foreground = new SolidColorBrush(Color.FromRgb(244, 241, 248));
        FontFamily = new FontFamily("Segoe UI");

        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaPlayerResumeAssistant");
        _storePath = Path.Combine(appDataDirectory, "positions.json");
        _settingsPath = Path.Combine(appDataDirectory, "settings.json");
        LoadStore();
        LoadSettings();

        // Main shell: a Groove-inspired sidebar and a spacious library workspace.
        var shell = new Grid();
        shell.LayoutTransform = _uiScale;
        PreviewMouseWheel += (_, e) => { if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0) { SetUiScale(_uiScale.ScaleX + (e.Delta > 0 ? 0.05 : -0.05)); e.Handled = true; } };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = new Border
        {
            Background = new LinearGradientBrush(
                Color.FromRgb(43, 27, 46), Color.FromRgb(25, 23, 29),
                new Point(0, 0), new Point(0.8, 1)),
            Padding = new Thickness(18, 24, 16, 18)
        };
        var sideLayout = new Grid();
        sideLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sideLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sideLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sideLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sideLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 35) };
        var brandIcon = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(12),
            Background = new LinearGradientBrush(Color.FromRgb(185, 105, 242), Color.FromRgb(104, 46, 171), 45),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "▶", FontSize = 20, Foreground = Brushes.White,
                FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 1)
            }
        };
        brand.Children.Add(brandIcon);
        var brandLabels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
        brandLabels.Children.Add(new TextBlock { Text = "MEDIA PLAYER", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        brandLabels.Children.Add(new TextBlock { Text = "RESUME ASSISTANT", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(187, 167, 198)), Margin = new Thickness(0, 3, 0, 0) });
        brand.Children.Add(brandLabels);
        Grid.SetRow(brand, 0);
        sideLayout.Children.Add(brand);

        var libraryLabel = new TextBlock
        {
            Text = "BIBLIOTECA", FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(167, 150, 174)),
            Margin = new Thickness(10, 0, 0, 10)
        };
        Grid.SetRow(libraryLabel, 1);
        sideLayout.Children.Add(libraryLabel);

        var nav = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        var libraryButton = MakeNavButton("▦", "Mi biblioteca", true);
        libraryButton.Click += (_, _) =>
        {
            RefreshRecentVideos();
            // This handler can be created before the status card is assigned.
            if (_statusText is not null)
                _statusText.Text = "Biblioteca actualizada. El historial se ordena por última reproducción.";
        };
        nav.Children.Add(libraryButton);
        var openNavButton = MakeNavButton("＋", "Abrir vídeo", false);
        openNavButton.Click += (_, _) => SelectAndOpenVideo();
        nav.Children.Add(openNavButton);
        var latestNavButton = MakeNavButton("↻", "Reanudar último", false);
        latestNavButton.Click += (_, _) => OpenLatestVideo();
        nav.Children.Add(latestNavButton);
        Grid.SetRow(nav, 2);
        sideLayout.Children.Add(nav);

        var sideSpacer = new Border { Background = Brushes.Transparent };
        Grid.SetRow(sideSpacer, 3);
        sideLayout.Children.Add(sideSpacer);

        var footer = new StackPanel();
        footer.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(74, 57, 78)), Margin = new Thickness(0, 0, 0, 14) });
        footer.Children.Add(new TextBlock { Text = "REANUDACIÓN AUTOMÁTICA", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(195, 169, 209)) });
        footer.Children.Add(new TextBlock
        {
            Text = "Guarda la posición y permite continuar donde lo dejaste.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(173, 163, 179)),
            Margin = new Thickness(0, 6, 4, 0), LineHeight = 17
        });
        var scaleHeader = new Grid { Margin = new Thickness(0, 18, 0, 4) };
        scaleHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scaleHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scaleHeader.Children.Add(new TextBlock { Text = "TAMAÑO DE LA INTERFAZ", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(195, 169, 209)), VerticalAlignment = VerticalAlignment.Center });
        _uiScaleValueText = new TextBlock { Text = $"{_uiScale.ScaleX * 100:0}%", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(231, 176, 255)), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_uiScaleValueText, 1);
        scaleHeader.Children.Add(_uiScaleValueText);
        footer.Children.Add(scaleHeader);
        _uiScaleSlider = new Slider
        {
            Minimum = 0.75, Maximum = 1.5, Value = _uiScale.ScaleX,
            TickFrequency = 0.05, SmallChange = 0.05, LargeChange = 0.1,
            IsSnapToTickEnabled = false, Height = 24, Margin = new Thickness(1, 1, 1, 0),
            ToolTip = "Ajusta el tamaño de la interfaz",
            Foreground = new SolidColorBrush(Color.FromRgb(190, 105, 232))
        };
        _uiScaleSlider.ValueChanged += (_, e) =>
        {
            SetUiScale(e.NewValue, false);
            if (_uiScaleValueText is not null)
                _uiScaleValueText.Text = $"{e.NewValue * 100:0}%";
            SaveSettings();
        };
        footer.Children.Add(_uiScaleSlider);
        footer.Children.Add(new TextBlock { Text = "75%   ·   100%   ·   150%", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(150, 138, 157)), Margin = new Thickness(0, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch });
        footer.Children.Add(new TextBlock { Text = "Ctrl + rueda del ratón también cambia el tamaño.", FontSize = 9, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(150, 138, 157)), Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetRow(footer, 4);
        sideLayout.Children.Add(footer);
        sidebar.Child = sideLayout;
        Grid.SetColumn(sidebar, 0);
        shell.Children.Add(sidebar);

        var workspace = new Grid { Margin = new Thickness(30, 27, 30, 24) };
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingStack = new StackPanel();
        headingStack.Children.Add(new TextBlock
        {
            Text = "Mi biblioteca", FontSize = 31, FontWeight = FontWeights.Light,
            Foreground = new SolidColorBrush(Color.FromRgb(249, 246, 251))
        });
        headingStack.Children.Add(new TextBlock
        {
            Text = "Tu biblioteca de vídeos y posiciones guardadas", FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(166, 159, 174)), Margin = new Thickness(2, 5, 0, 0)
        });
        Grid.SetColumn(headingStack, 0);
        header.Children.Add(headingStack);

        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0, 0, 0) };
        var openButton = MakeButton("＋  Abrir vídeo", true);
        openButton.Click += (_, _) => SelectAndOpenVideo();
        headerActions.Children.Add(openButton);
        _openLatestButton = MakeButton("↻  Reanudar último", false);
        _openLatestButton.Margin = new Thickness(9, 0, 0, 0);
        _openLatestButton.Click += (_, _) => OpenLatestVideo();
        headerActions.Children.Add(_openLatestButton);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        Grid.SetRow(header, 0);
        workspace.Children.Add(header);

        var separator = new Grid { Height = 2, Margin = new Thickness(0, 0, 0, 19), Background = new SolidColorBrush(Color.FromRgb(55, 50, 60)) };
        separator.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        separator.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var accent = new Border { Background = new LinearGradientBrush(Color.FromRgb(224, 91, 160), Color.FromRgb(155, 87, 220), 0) };
        Grid.SetColumn(accent, 0);
        separator.Children.Add(accent);
        Grid.SetRow(separator, 1);
        workspace.Children.Add(separator);

        // Three compact information tiles replace a traditional player view.
        var infoCards = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        infoCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        infoCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        infoCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        var fileCard = MakeInfoCard("VÍDEO SELECCIONADO", "▸", out _fileText);
        _fileText.Text = "Todavía no has seleccionado un vídeo";
        Grid.SetColumn(fileCard, 0);
        infoCards.Children.Add(fileCard);
        var positionCard = MakeInfoCard("POSICIÓN GUARDADA", "◷", out _positionText);
        _positionText.Text = "Esperando información de reproducción…";
        Grid.SetColumn(positionCard, 1);
        infoCards.Children.Add(positionCard);
        var statusCard = MakeInfoCard("ESTADO DEL ASISTENTE", "✦", out _statusText);
        _statusText.Text = "Conectando con la sesión multimedia de Windows…";
        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(211, 167, 244));
        Grid.SetColumn(statusCard, 2);
        infoCards.Children.Add(statusCard);
        Grid.SetRow(infoCards, 2);
        workspace.Children.Add(infoCards);

        var recentHeading = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        recentHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        recentHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var recentTitleStack = new StackPanel();
        recentTitleStack.Children.Add(new TextBlock { Text = "Reproducidos recientemente", FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        recentTitleStack.Children.Add(new TextBlock { Text = "Selecciona un vídeo para abrirlo desde la última posición guardada.", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(160, 153, 168)), Margin = new Thickness(1, 4, 0, 0) });
        Grid.SetColumn(recentTitleStack, 0);
        recentHeading.Children.Add(recentTitleStack);
        var countHint = new TextBlock { Text = "HISTORIAL", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(187, 151, 208)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0) };
        Grid.SetColumn(countHint, 1);
        recentHeading.Children.Add(countHint);
        Grid.SetRow(recentHeading, 3);
        workspace.Children.Add(recentHeading);

        var recentSurface = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 26, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(57, 51, 63)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(8)
        };
        _recentVideosList = new ListBox
        {
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(240, 237, 244)),
            BorderThickness = new Thickness(0),
            FontSize = 12,
            Padding = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        // VerticalScrollBarVisibility is an attached dependency property, not a ListBox CLR property.
        ScrollViewer.SetVerticalScrollBarVisibility(_recentVideosList, ScrollBarVisibility.Auto);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _recentVideosList.ItemContainerStyle = itemStyle;
        recentSurface.Child = _recentVideosList;
        Grid.SetRow(recentSurface, 4);
        workspace.Children.Add(recentSurface);

        Grid.SetColumn(workspace, 1);
        shell.Children.Add(workspace);
        Content = shell;

        RefreshRecentVideos();

        // Poll quickly while a video is being attached/tracked, more slowly while idle.
        _timer = new DispatcherTimer { Interval = IdlePollInterval };
        _timer.Tick += async (_, _) => await PollSessionAsync();
        Loaded += async (_, _) =>
        {
            await InitializeMediaSessionAsync();
            _timer.Start();
            if (args.Length > 0 && File.Exists(args[0]))
            {
                OpenVideo(args[0]);
            }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            SaveStore(force: true);
            SaveSettings();
        };
    }

    private static Border MakeInfoCard(string title, string glyph, out TextBlock valueText)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(34, 30, 39)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 54, 70)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(14, 13, 13, 13),
            MinHeight = 114
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        titleLine.Children.Add(new TextBlock { Text = glyph, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(207, 112, 235)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        titleLine.Children.Add(new TextBlock { Text = title, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(174, 161, 184)), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(titleLine, 0);
        layout.Children.Add(titleLine);
        valueText = new TextBlock
        {
            Text = "—", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(238, 232, 244)),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top,
            LineHeight = 17, ToolTip = null
        };
        Grid.SetRow(valueText, 1);
        layout.Children.Add(valueText);
        card.Child = layout;
        return card;
    }

    private static Button MakeButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(15, 10, 15, 10),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(1),
            BorderBrush = primary ? new SolidColorBrush(Color.FromRgb(187, 105, 226)) : new SolidColorBrush(Color.FromRgb(72, 62, 81)),
            Background = primary
                ? new LinearGradientBrush(Color.FromRgb(153, 77, 200), Color.FromRgb(112, 55, 165), 90)
                : new SolidColorBrush(Color.FromRgb(43, 38, 49)),
            Foreground = Brushes.White,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(ContentPresenter.MarginProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
        template.Triggers.Add(disabled);
        button.Template = template;
        return button;
    }

    private static Button MakeNavButton(string glyph, string text, bool selected)
    {
        var button = new Button
        {
            Background = selected ? new SolidColorBrush(Color.FromRgb(62, 43, 72)) : Brushes.Transparent,
            Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(202, 192, 208)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 2, 0, 3),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
            Height = 44
        };
        var template = new ControlTemplate(typeof(Button));
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        outer.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var contentGrid = new FrameworkElementFactory(typeof(Grid));
        var stripe = new FrameworkElementFactory(typeof(Border));
        stripe.SetValue(Border.WidthProperty, 3.0);
        stripe.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        stripe.SetValue(Border.BackgroundProperty, selected ? new SolidColorBrush(Color.FromRgb(220, 83, 153)) : Brushes.Transparent);
        stripe.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        contentGrid.AppendChild(stripe);
        var contentPanel = new FrameworkElementFactory(typeof(StackPanel));
        contentPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        contentPanel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentPanel.SetValue(FrameworkElement.MarginProperty, new Thickness(15, 0, 7, 0));
        var glyphText = new FrameworkElementFactory(typeof(TextBlock));
        glyphText.SetValue(TextBlock.TextProperty, glyph);
        glyphText.SetValue(TextBlock.FontSizeProperty, 16.0);
        glyphText.SetValue(TextBlock.WidthProperty, 24.0);
        glyphText.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        glyphText.SetValue(TextBlock.ForegroundProperty, selected ? new SolidColorBrush(Color.FromRgb(229, 126, 190)) : new SolidColorBrush(Color.FromRgb(177, 158, 186)));
        contentPanel.AppendChild(glyphText);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(TextBlock.TextProperty, text);
        label.SetValue(TextBlock.FontSizeProperty, 12.0);
        label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentPanel.AppendChild(label);
        contentGrid.AppendChild(contentPanel);
        outer.AppendChild(contentGrid);
        template.VisualTree = outer;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82));
        template.Triggers.Add(hover);
        button.Template = template;
        return button;
    }

    private void SetUiScale(double value, bool updateSlider = true)
    {
        value = Math.Clamp(value, 0.75, 1.5);
        _uiScale.ScaleX = value; _uiScale.ScaleY = value;
        if (updateSlider && _uiScaleSlider is not null && Math.Abs(_uiScaleSlider.Value - value) > 0.001) _uiScaleSlider.Value = value;
        if (_uiScaleValueText is not null)
            _uiScaleValueText.Text = $"{value * 100:0}%";
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return;
            using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            if (document.RootElement.TryGetProperty("uiScale", out var scaleElement) &&
                scaleElement.TryGetDouble(out var scale) && double.IsFinite(scale))
            {
                var safeScale = Math.Clamp(scale, 0.75, 1.5);
                _uiScale.ScaleX = safeScale;
                _uiScale.ScaleY = safeScale;
            }
        }
        catch
        {
            // Ignore damaged settings and start with safe defaults.
        }
    }

    private void SaveSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporary = _settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { uiScale = _uiScale.ScaleX }, StoreJsonOptions));
            File.Move(temporary, _settingsPath, true);
        }
        catch
        {
            // A settings write failure must never interfere with media tracking.
        }
    }

    private async Task InitializeMediaSessionAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            try
            {
                var current = _manager.GetCurrentSession();
                if (current is not null)
                {
                    var media = await current.TryGetMediaPropertiesAsync();
                    _lastSeenTitle = media?.Title;
                }
            }
            catch { }
            _statusText.Text = "Preparado. Mantén Media Player como aplicación predeterminada para los formatos de vídeo que utilices.";
        }
        catch (Exception ex)
        {
            _statusText.Text = "No se pudo acceder al control multimedia de Windows: " + ex.Message;
        }
    }

    private void SelectAndOpenVideo()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecciona un vídeo",
            Filter = "Vídeos|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.webm;*.mpg;*.mpeg;*.ts;*.m2ts;*.3gp;*.flv|Todos los archivos|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            OpenVideo(dialog.FileName);
        }
    }

    private void OpenLatestVideo()
    {
        var latest = _saved.Values
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FilePath))
            .OrderByDescending(entry => entry.LastUpdatedUtc)
            .FirstOrDefault();

        if (latest is null)
        {
            _statusText.Text = "Todavía no hay vídeos recientes disponibles. Usa «Abrir vídeo» para empezar.";
            RefreshRecentVideos();
            return;
        }

        OpenVideo(latest.FilePath);
    }

    private void OpenVideo(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                var answer = MessageBox.Show(this, "Este vídeo no está disponible. Puede que la unidad USB esté desconectada o que el archivo se haya movido.\n\n" + fullPath + "\n\n¿Quieres quitarlo del historial? Si eliges No, se conservará para cuando vuelvas a conectar la unidad.", "Vídeo no disponible", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
                if (answer == MessageBoxResult.Yes) { RemoveHistoryEntryForPath(fullPath); RefreshRecentVideos(); SaveStore(force: true); _statusText.Text = "Vídeo eliminado del historial."; }
                else _statusText.Text = "El vídeo se conserva en el historial. Comprueba si la unidad está conectada.";
                return;
            }

            _fileBeforeLaunch = _currentFile;
            _titleBeforeLaunch = _lastSeenTitle;
            _currentFile = fullPath;
            _session = null;
            _mediaPropertiesSession = null;
            _activeSessionTitle = null;
            _lastMediaPropertiesFetchUtc = DateTime.MinValue;
            _sessionTimeoutReported = false;
            _timer.Interval = ActivePollInterval;
            _launchTimeUtc = DateTime.UtcNow;
            _restoreTried = false;
            _ignoreCurrentSessionSaving = false;
            _lastRecordedTicks = -1;
            _fileText.Text = "Vídeo: " + fullPath;

            // Register the opening immediately. Existing playback progress is deliberately preserved.
            MarkVideoAsRecentlyOpened(fullPath);

            if (_saved.TryGetValue(fullPath, out var previous) && previous.PositionTicks > TimeSpan.FromSeconds(3).Ticks)
            {
                _positionText.Text = "Última posición guardada: " + FormatTime(TimeSpan.FromTicks(previous.PositionTicks));
                _statusText.Text = "Abriendo el vídeo y buscando su sesión multimedia para restaurar la posición…";
            }
            else
            {
                _positionText.Text = "Posición: vídeo nuevo o sin progreso guardado.";
                _statusText.Text = "Abriendo con la aplicación predeterminada de Windows…";
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _statusText.Text = "No se pudo abrir el vídeo: " + ex.Message;
        }
    }

    private void MarkVideoAsRecentlyOpened(string fullPath)
    {
        if (!_saved.TryGetValue(fullPath, out var entry))
        {
            entry = new SavedPosition
            {
                FilePath = fullPath,
                PositionTicks = 0,
                DurationTicks = 0,
                Title = Path.GetFileNameWithoutExtension(fullPath)
            };
            _saved[fullPath] = entry;
        }

        entry.FilePath = fullPath;
        if (string.IsNullOrWhiteSpace(entry.Title))
            entry.Title = Path.GetFileNameWithoutExtension(fullPath);
        entry.LastUpdatedUtc = DateTime.UtcNow;
        _dirty = true;
        RefreshRecentVideos();
        SaveStore(force: true);
    }

    private void RefreshRecentVideos()
    {
        // Keep entries even if a drive is disconnected or a file is temporarily missing.
        _recentVideosList.Items.Clear();
        foreach (var entry in _saved.Values
            .Where(item => !string.IsNullOrWhiteSpace(item.FilePath))
            .OrderByDescending(item => item.LastUpdatedUtc)
            .Take(30))
        {
            var filePath = entry.FilePath;
            var name = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(name)) name = entry.Title;
            var isMissing = !File.Exists(filePath);
            if (isMissing) name = "[NO DISPONIBLE]  " + name;

            var when = entry.LastUpdatedUtc == default
                ? ""
                : "   ·   " + entry.LastUpdatedUtc.ToLocalTime().ToString("dd/MM HH:mm");

            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            // Keep the delete control in a dedicated first column so it is always visible,
            // even when the video filename is long or the window is narrow.
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var deleteButton = MakeButton("×", false);
            deleteButton.Width = 28;
            deleteButton.MinWidth = 28;
            deleteButton.Padding = new Thickness(0);
            deleteButton.Margin = new Thickness(2, 0, 6, 0);
            deleteButton.FontSize = 17;
            deleteButton.FontWeight = FontWeights.Bold;
            deleteButton.Background = new SolidColorBrush(Color.FromRgb(76, 43, 63));
            deleteButton.BorderBrush = new SolidColorBrush(Color.FromRgb(103, 59, 88));
            deleteButton.ToolTip = "Eliminar el archivo del disco";
            deleteButton.Click += (_, _) =>
            {
                // The X deletes the actual video when it exists. If it is missing,
                // leave the history entry untouched so it can be used when the USB returns.
                if (!File.Exists(filePath))
                {
                    MessageBox.Show(this,
                        "No se puede borrar el archivo porque no se encuentra en esa ubicación.\n\n" + filePath +
                        "\n\nLa entrada se conservará en el historial.",
                        "Archivo no encontrado", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var answer = MessageBox.Show(this,
                    "¿Seguro que quieres eliminar este archivo del disco? Esta acción no se puede deshacer.\n\n" + filePath,
                    "Eliminar archivo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;

                try
                {
                    // Recheck immediately before deleting in case the drive was disconnected.
                    if (!File.Exists(filePath))
                    {
                        MessageBox.Show(this,
                            "No se puede borrar el archivo porque ya no se encuentra en esa ubicación.\n\n" + filePath +
                            "\n\nLa entrada se conservará en el historial.",
                            "Archivo no encontrado", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    File.Delete(filePath);
                    RemoveHistoryEntryForPath(filePath);
                    RefreshRecentVideos();
                    SaveStore(force: true);
                    _statusText.Text = "Archivo eliminado del disco y del historial.";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "No se pudo eliminar el archivo. Puede que no tengas permisos o que la unidad no esté disponible.\n\n" + ex.Message,
                        "Error al eliminar", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            Grid.SetColumn(deleteButton, 0);
            row.Children.Add(deleteButton);

            var nameText = new TextBlock
            {
                Text = name + when,
                Foreground = isMissing ? new SolidColorBrush(Color.FromRgb(220, 166, 153)) : new SolidColorBrush(Color.FromRgb(240, 237, 244)),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 5, 5, 5),
                ToolTip = filePath,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            nameText.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenVideo(filePath);
            };
            Grid.SetColumn(nameText, 1);
            row.Children.Add(nameText);

            var rowCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 33, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 51, 64)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(5, 4, 5, 4),
                Margin = new Thickness(1, 2, 1, 2),
                Child = row
            };
            _recentVideosList.Items.Add(rowCard);
        }

        _openLatestButton.IsEnabled = _saved.Values.Any(entry => !string.IsNullOrWhiteSpace(entry.FilePath));
    }

    private void RemoveHistoryEntryForPath(string filePath)
    {
        var keys = _saved
            .Where(pair => PathsEqual(pair.Key, filePath) || PathsEqual(pair.Value.FilePath, filePath))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in keys)
            _saved.Remove(key);

        if (PathsEqual(_currentFile, filePath))
        {
            _currentFile = null;
            _session = null;
            _restoreTried = false;
            _lastRecordedTicks = -1;
        }

        if (keys.Count > 0)
            _dirty = true;
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;

        try
        {
            first = Path.GetFullPath(first);
            second = Path.GetFullPath(second);
        }
        catch
        {
            // Fall back to a case-insensitive comparison if either path cannot be normalized.
        }

        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    private async Task PollSessionAsync()
    {
        if (_polling)
            return;

        _polling = true;
        try
        {
            if (_manager is null)
                return;

            if (_currentFile is null)
            {
                if (_timer.Interval != IdlePollInterval)
                    _timer.Interval = IdlePollInterval;
                try
                {
                    var idleSession = _manager.GetCurrentSession();
                    if (idleSession is not null)
                    {
                        var idleMedia = await idleSession.TryGetMediaPropertiesAsync();
                        if (!string.IsNullOrWhiteSpace(idleMedia?.Title))
                            _lastSeenTitle = idleMedia.Title;
                    }
                }
                catch { }
                return;
            }

            var sessions = _manager.GetSessions();
            // Do not stop searching permanently after the startup grace period. Some Windows media
            // sessions appear late, especially when the player is being launched for the first time.
            if (_session is null)
                _session = await FindTargetSessionAsync(sessions, _currentFile);

            if (_session is null)
            {
                if (DateTime.UtcNow - _launchTimeUtc >= TimeSpan.FromSeconds(35))
                {
                    if (!_sessionTimeoutReported)
                    {
                        _statusText.Text = "No se detectó la sesión del reproductor. Se seguirá buscando periódicamente. Comprueba que Media Player de Windows sea la aplicación predeterminada para este formato.";
                        _sessionTimeoutReported = true;
                    }
                    // After the startup grace period, retry discovery less often to save resources.
                    if (_timer.Interval != IdlePollInterval)
                        _timer.Interval = IdlePollInterval;
                }
                else if (_timer.Interval != ActivePollInterval)
                {
                    _timer.Interval = ActivePollInterval;
                }
                return;
            }

            if (_timer.Interval != ActivePollInterval)
                _timer.Interval = ActivePollInterval;

            var timeline = _session.GetTimelineProperties();
            // Media metadata rarely changes during a video. Cache it rather than requesting it on every poll.
            if (!ReferenceEquals(_mediaPropertiesSession, _session)
                || DateTime.UtcNow - _lastMediaPropertiesFetchUtc >= MediaPropertiesRefreshInterval)
            {
                // Advance the cache timestamp before the async call so transient failures don't
                // cause another metadata request on every five-second polling cycle.
                _mediaPropertiesSession = _session;
                _lastMediaPropertiesFetchUtc = DateTime.UtcNow;
                try
                {
                    var media = await _session.TryGetMediaPropertiesAsync();
                    if (!string.IsNullOrWhiteSpace(media?.Title))
                        _activeSessionTitle = media.Title;
                }
                catch
                {
                    // Keep the last known title; timeline position tracking can continue without metadata.
                }
            }
            var title = string.IsNullOrWhiteSpace(_activeSessionTitle) ? Path.GetFileName(_currentFile) : _activeSessionTitle;
            _lastSeenTitle = title;

            var duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : TimeSpan.Zero;

            if (!_restoreTried && duration > TimeSpan.Zero)
            {
                _restoreTried = true;
                var restored = false;
                var hasSavedPosition = _saved.TryGetValue(_currentFile, out var previous)
                    && previous.PositionTicks > TimeSpan.FromSeconds(3).Ticks;

                if (hasSavedPosition)
                {
                    var savedPosition = TimeSpan.FromTicks(previous!.PositionTicks);
                    if (savedPosition < duration - TimeSpan.FromSeconds(5))
                    {
                        try
                        {
                            restored = await _session.TryChangePlaybackPositionAsync(savedPosition.Ticks);
                        }
                        catch
                        {
                            restored = false;
                        }

                        _statusText.Text = restored
                            ? "Vídeo reanudado desde " + FormatTime(savedPosition) + "."
                            : "El reproductor abrió el vídeo, pero no confirmó la restauración. Se conserva la última posición guardada para evitar perderla.";
                    }
                    else
                    {
                        _statusText.Text = "El vídeo anterior estaba prácticamente terminado; se iniciará desde el principio.";
                    }
                }
                else
                {
                    _statusText.Text = "Sesión detectada: " + title;
                }

                _positionText.Text = "Posición actual: " + FormatTime(timeline.Position) + " / " + FormatTime(duration);
                if (hasSavedPosition)
                {
                    // The first timeline snapshot may still show the starting position or a stale session.
                    // Never replace an existing checkpoint with that first snapshot.
                    _lastRecordedTicks = previous!.PositionTicks;
                }
                else
                {
                    RecordPosition(title, timeline.Position, duration);
                }
                SaveStore(force: false);
                return;
            }

            _positionText.Text = "Posición actual: " + FormatTime(timeline.Position) + " / " + (duration > TimeSpan.Zero ? FormatTime(duration) : "duración desconocida");
            RecordPosition(title, timeline.Position, duration);
            SaveStore(force: false);

        }
        catch (Exception ex)
        {
            _statusText.Text = "La supervisión de reproducción tuvo un error: " + ex.Message;
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> FindTargetSessionAsync(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions,
        string filePath)
    {
        // Only control sessions belonging to Windows Media Player / the Windows Media Player app.
        // Falling back to an arbitrary single media session could send a seek command to music or video
        // playing in a different application.
        var playerSessions = sessions
            .Where(session => IsWindowsMediaPlayer(session.SourceAppUserModelId))
            .ToList();
        if (playerSessions.Count == 0)
            return null;

        // Prefer title/filename matches within the expected player. Some files expose embedded metadata
        // instead of their filename.
        foreach (var session in playerSessions)
        {
            try
            {
                var properties = await session.TryGetMediaPropertiesAsync();
                if (TitleMatchesFile(properties?.Title, filePath))
                    return session;
            }
            catch { }
        }

        var current = _manager?.GetCurrentSession();
        if (current is not null && IsWindowsMediaPlayer(current.SourceAppUserModelId)
            && !await IsStalePreviousSessionAsync(current, filePath))
            return current;

        foreach (var session in playerSessions)
        {
            if (!await IsStalePreviousSessionAsync(session, filePath))
                return session;
        }

        return null;
    }

    private async Task<bool> IsStalePreviousSessionAsync(GlobalSystemMediaTransportControlsSession session, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(_titleBeforeLaunch))
            return false;

        // Reopening the same path is intentional; only reject a session that still advertises the
        // previous item's title while a different file is being opened. This also covers the first
        // file opened by the assistant when another Windows Media Player session was already active.
        if (PathsEqual(_fileBeforeLaunch, requestedPath))
            return false;

        try
        {
            var media = await session.TryGetMediaPropertiesAsync();
            return string.Equals(media?.Title?.Trim(), _titleBeforeLaunch.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWindowsMediaPlayer(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return false;

        return appId.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase)
            || appId.Contains("Media.Player", StringComparison.OrdinalIgnoreCase)
            || appId.Contains("WindowsMediaPlayer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TitleMatchesFile(string? title, string filePath)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var stem = Path.GetFileNameWithoutExtension(filePath).Trim();
        var cleanTitle = title.Trim();
        if (stem.Length == 0)
            return false;

        return string.Equals(cleanTitle, stem, StringComparison.OrdinalIgnoreCase)
            || string.Equals(cleanTitle, Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase)
            || cleanTitle.Contains(stem, StringComparison.OrdinalIgnoreCase)
            || (cleanTitle.Length >= 6 && stem.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase));
    }

    private void RecordPosition(string title, TimeSpan position, TimeSpan duration)
    {
        if (_ignoreCurrentSessionSaving || string.IsNullOrWhiteSpace(_currentFile)
            || !File.Exists(_currentFile) || duration <= TimeSpan.Zero)
            return;

        var newPosition = position;
        // Treat the final few seconds as completed so the next opening starts at the beginning.
        if (newPosition >= duration - TimeSpan.FromSeconds(5))
            newPosition = TimeSpan.Zero;
        if (newPosition < TimeSpan.FromSeconds(2))
            newPosition = TimeSpan.Zero;

        if (_lastRecordedTicks >= 0 && Math.Abs(newPosition.Ticks - _lastRecordedTicks) < TimeSpan.FromSeconds(1).Ticks)
            return;

        // The list timestamp/order represents when the file was opened, not every position sample.
        // Preserve it so the history list does not need rebuilding on every timer tick.
        _saved.TryGetValue(_currentFile, out var existingEntry);
        _saved[_currentFile] = new SavedPosition
        {
            FilePath = _currentFile,
            PositionTicks = newPosition.Ticks,
            DurationTicks = duration.Ticks,
            Title = title,
            LastUpdatedUtc = existingEntry?.LastUpdatedUtc ?? DateTime.UtcNow
        };
        _lastRecordedTicks = newPosition.Ticks;
        _dirty = true;
    }

    private void LoadStore()
    {
        try
        {
            if (!File.Exists(_storePath))
                return;

            var json = File.ReadAllText(_storePath);
            var entries = JsonSerializer.Deserialize<Dictionary<string, SavedPosition>>(json);
            if (entries is null)
                return;

            foreach (var item in entries)
            {
                if (string.IsNullOrWhiteSpace(item.Value.FilePath))
                    item.Value.FilePath = item.Key;
                _saved[item.Key] = item.Value;
            }
        }
        catch
        {
            // If the store is damaged, continue with an empty in-memory list rather than stopping playback.
        }
    }

    private void SaveStore(bool force)
    {
        if (!_dirty)
            return;
        if (!force && DateTime.UtcNow - _lastDiskSaveUtc < DiskSaveInterval)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var json = JsonSerializer.Serialize(_saved, StoreJsonOptions);
            var temporary = _storePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _storePath, true);
            _lastDiskSaveUtc = DateTime.UtcNow;
            _dirty = false;
        }
        catch (Exception ex)
        {
            _statusText.Text = "No se pudo guardar el progreso en disco: " + ex.Message;
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    private sealed class SavedPosition
    {
        public string FilePath { get; set; } = "";
        public long PositionTicks { get; set; }
        public long DurationTicks { get; set; }
        public string Title { get; set; } = "";
        public DateTime LastUpdatedUtc { get; set; }
    }
}
