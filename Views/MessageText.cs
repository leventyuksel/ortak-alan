using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrtakAlan.Views;

public class MessageText : RichTextBox
{
    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(nameof(Body), typeof(string), typeof(MessageText),
            new FrameworkPropertyMetadata("", (d, _) => ((MessageText)d).Rebuild()));

    public static readonly DependencyProperty MineProperty =
        DependencyProperty.Register(nameof(Mine), typeof(bool), typeof(MessageText),
            new FrameworkPropertyMetadata(false, (d, _) => ((MessageText)d).Rebuild()));

    public static readonly DependencyProperty StruckProperty =
        DependencyProperty.Register(nameof(Struck), typeof(bool), typeof(MessageText),
            new FrameworkPropertyMetadata(false, (d, _) => ((MessageText)d).Rebuild()));

    static readonly Regex LinkPattern = new(
        @"https?://[^\s<>]+|www\.[^\s<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    string _shown = "\0";
    bool _shownMine;
    bool _shownStruck;
    bool _measuring;
    Point _press;
    bool _moved;
    Uri? _pendingLink;
    DispatcherTimer? _openTimer;

    public MessageText()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsDocumentEnabled = true;
        IsUndoEnabled = false;
        IsTabStop = false;
        AcceptsTab = false;
        AllowDrop = false;
        AutoWordSelection = true;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        MinWidth = 0;
        MinHeight = 0;
        CaretBrush = Brushes.Transparent;
        SelectionOpacity = 1;
        IsInactiveSelectionHighlightEnabled = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SpellCheck.SetIsEnabled(this, false);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        AddHandler(CommandManager.PreviewCanExecuteEvent, new CanExecuteRoutedEventHandler(PreviewCanCopy));
        AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(PreviewCopy));
        Loaded += (_, _) => InvalidateMeasure();
        Unloaded += (_, _) => CancelOpen();
        Rebuild();
    }

    public string Body
    {
        get => (string)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public bool Mine
    {
        get => (bool)GetValue(MineProperty);
        set => SetValue(MineProperty, value);
    }

    public bool Struck
    {
        get => (bool)GetValue(StruckProperty);
        set => SetValue(StruckProperty, value);
    }

    public string SelectedPlain()
    {
        var text = Selection?.Text ?? "";
        if (text.Length == 0) return "";
        return Clean(text).TrimEnd('\r', '\n');
    }

    public static bool CopyToClipboard(string text, DependencyObject? owner)
    {
        text = Clean(text);
        if (text.Length == 0) return false;
        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true);
            if (Window.GetWindow(owner) is ChatWindow chat) chat.Note("Kopyalandı.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            if (Window.GetWindow(owner) is ChatWindow chat) chat.Note("Kopyalanamadı.");
            return false;
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty || e.Property == FontWeightProperty)
            Rebuild(true);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CancelOpen();
        _press = e.GetPosition(this);
        _moved = false;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _press.X) > 4 || Math.Abs(point.Y - _press.Y) > 4)
            _moved = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            Cursor = Cursors.IBeam;
            return;
        }

        Cursor = LinkAt(e.GetPosition(this)) == null ? Cursors.IBeam : Cursors.Hand;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_moved || e.ClickCount > 1) return;
        if (SelectedPlain().Length > 0) return;
        var uri = LinkAt(e.GetPosition(this));
        if (uri == null) return;
        e.Handled = true;
        ScheduleOpen(uri);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Handled) return;
        if (VisualTreeHelper.GetParent(this) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = parent
        });
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        e.Handled = true;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (_measuring) return base.MeasureOverride(constraint);
        _measuring = true;
        try
        {
            var limit = constraint.Width;
            if (double.IsInfinity(limit) || limit > 4096) limit = 256;
            if (limit < 1) limit = 1;

            Document.PagePadding = new Thickness(0);
            var text = DisplayText();
            var natural = Format(text.Length == 0 ? " " : text);
            var width = natural.WidthIncludingTrailingWhitespace <= limit
                ? Math.Min(limit, Math.Ceiling(natural.WidthIncludingTrailingWhitespace) + 4)
                : limit;
            if (width < 1) width = 1;

            if (double.IsNaN(Document.PageWidth) || Math.Abs(Document.PageWidth - width) > 0.5)
                Document.PageWidth = width;
            var measured = base.MeasureOverride(new Size(width, double.PositiveInfinity));
            return new Size(width, measured.Height);
        }
        finally
        {
            _measuring = false;
        }
    }

    void PreviewCanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Copy || !IsKeyboardFocused) return;
        e.CanExecute = (Body?.Length ?? 0) > 0;
        e.Handled = true;
    }

    void PreviewCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Copy || !IsKeyboardFocused) return;
        var selected = SelectedPlain();
        CopyToClipboard(selected.Length > 0 ? selected : Body ?? "", this);
        e.Handled = true;
    }

    void Rebuild(bool force = false)
    {
        var text = Body ?? "";
        if (!force && text == _shown && Mine == _shownMine && Struck == _shownStruck) return;
        _shown = text;
        _shownMine = Mine;
        _shownStruck = Struck;

        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            FontFamily = FontFamily,
            FontSize = FontSize,
            FontWeight = FontWeight,
            FontStyle = FontStyle
        };
        var bodyKey = Mine ? "OnAccent" : "Ink";
        var linkKey = Mine ? "OnAccent" : "Accent";
        if (text.Length == 0)
            paragraph.Inlines.Add(MakeRun("", bodyKey, null, ""));
        else
        {
            foreach (var piece in Split(text))
                paragraph.Inlines.Add(MakeRun(SoftBreak(piece.Text), piece.Uri == null ? bodyKey : linkKey, piece.Uri, piece.Text));
        }

        document.Blocks.Add(paragraph);
        Document = document;
        SelectionBrush = Mine
            ? new SolidColorBrush(Color.FromArgb(96, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(72, 10, 186, 181));
        InvalidateMeasure();
    }

    Inline MakeRun(string text, string brushKey, Uri? uri, string tip)
    {
        Run run = uri == null ? new Run(text) : new LinkRun(text, uri);
        run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
        var decorations = new TextDecorationCollection();
        if (uri != null)
        {
            decorations.Add(TextDecorations.Underline[0]);
            run.ToolTip = tip;
        }
        if (Struck) decorations.Add(TextDecorations.Strikethrough[0]);
        if (decorations.Count > 0) run.TextDecorations = decorations;
        return run;
    }

    Uri? LinkAt(Point point)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        TextPointer? pointer;
        try { pointer = GetPositionFromPoint(point, true); }
        catch (Exception) { return null; }
        var parent = pointer?.Parent;
        while (parent != null)
        {
            if (parent is LinkRun run) return run.Target;
            parent = parent is TextElement element ? element.Parent : null;
        }
        return null;
    }

    void ScheduleOpen(Uri uri)
    {
        _pendingLink = uri;
        _openTimer ??= new DispatcherTimer();
        _openTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, GetDoubleClickTime()));
        _openTimer.Stop();
        _openTimer.Tick -= OpenPending;
        _openTimer.Tick += OpenPending;
        _openTimer.Start();
    }

    void OpenPending(object? sender, EventArgs e)
    {
        _openTimer?.Stop();
        var uri = _pendingLink;
        _pendingLink = null;
        if (uri == null || SelectedPlain().Length > 0) return;
        Open(uri);
    }

    void CancelOpen()
    {
        _openTimer?.Stop();
        _pendingLink = null;
    }

    void Open(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            if (Window.GetWindow(this) is ChatWindow chat) chat.Note("Bağlantı açılamadı.");
        }
    }

    string DisplayText()
    {
        if (Document == null) return Body ?? "";
        return new TextRange(Document.ContentStart, Document.ContentEnd).Text.TrimEnd('\r', '\n');
    }

    FormattedText Format(string text)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize > 0 ? FontSize : 14,
            Brushes.Black,
            PixelsPerDip());
    }

    double PixelsPerDip()
    {
        if (PresentationSource.FromVisual(this) == null) return 1;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return dpi <= 0 ? 1 : dpi;
    }

    static string Clean(string text) => text.Replace("\u200B", "");

    static string SoftBreak(string text)
    {
        const int limit = 24;
        var builder = new StringBuilder(text.Length + 8);
        var run = 0;
        foreach (var ch in text)
        {
            builder.Append(ch);
            if (char.IsWhiteSpace(ch)) run = 0;
            else if (++run >= limit)
            {
                builder.Append('\u200B');
                run = 0;
            }
        }
        return builder.ToString();
    }

    static IEnumerable<Piece> Split(string text)
    {
        var index = 0;
        foreach (Match match in LinkPattern.Matches(text))
        {
            var shown = TrimTail(match.Value);
            if (shown.Length == 0 || !TryLink(shown, out var uri)) continue;
            if (match.Index > index)
                yield return new Piece(text[index..match.Index], null);
            yield return new Piece(shown, uri);
            index = match.Index + shown.Length;
        }

        if (index < text.Length)
            yield return new Piece(text[index..], null);
    }

    static string TrimTail(string raw)
    {
        var end = raw.Length;
        while (end > 0 && ".,;:!?\"'»”’".Contains(raw[end - 1]))
            end--;
        while (end > 0 && raw[end - 1] is ')' or ']' or '}')
        {
            var open = raw[end - 1] switch { ')' => '(', ']' => '[', _ => '{' };
            var close = raw[end - 1];
            var opened = 0;
            var closed = 0;
            for (var i = 0; i < end; i++)
            {
                if (raw[i] == open) opened++;
                else if (raw[i] == close) closed++;
            }
            if (closed <= opened) break;
            end--;
        }
        return raw[..end];
    }

    static bool TryLink(string text, out Uri uri)
    {
        uri = null!;
        var www = text.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        var candidate = www ? "https://" + text : text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) || parsed == null)
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        if (parsed.Host.Length == 0) return false;
        if (www && (!parsed.Host.Contains('.') || parsed.Host.EndsWith('.')))
            return false;
        uri = parsed;
        return true;
    }

    [DllImport("user32.dll")]
    static extern uint GetDoubleClickTime();

    readonly record struct Piece(string Text, Uri? Uri);

    sealed class LinkRun : Run
    {
        public Uri Target { get; }
        public LinkRun(string text, Uri target) : base(text) => Target = target;
    }
}
