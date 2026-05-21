using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ControllerManager.ViewModels;
using DragEventArgs        = System.Windows.DragEventArgs;
using MouseEventArgs       = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState     = System.Windows.Input.MouseButtonState;
using DragDrop             = System.Windows.DragDrop;
using DragDropEffects      = System.Windows.DragDropEffects;
using DataObject           = System.Windows.DataObject;
using Point                = System.Windows.Point;
using Size                 = System.Windows.Size;
using Brush                = System.Windows.Media.Brush;
using Color                = System.Windows.Media.Color;
using Pen                  = System.Windows.Media.Pen;

namespace ControllerManager.Views;

public partial class GamesView : UserControl
{
    // Drag-and-drop reordering. Drag source = the ☰ handle in each row.
    // Drop target = the row Border itself. Visual feedback:
    //   • Ghost adorner of the dragged row follows the cursor
    //   • Source row dims to 0.4 opacity for the duration of the drag
    //   • Drop target row shows a colored line (top or bottom edge) indicating
    //     where the dropped item will land
    private static readonly string DragFormat = "ControllerManager.AssignmentDrag";

    private Point _dragStart;
    private DeviceAssignmentViewModel? _dragItem;
    private FrameworkElement? _sourceRow;
    private DragAdorner? _ghostAdorner;
    private InsertionAdorner? _insertionAdorner;
    private AdornerLayer? _adornerLayer;

    public GamesView() => InitializeComponent();

    // ── Drag source ──────────────────────────────────────────────────────────

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DeviceAssignmentViewModel vm)
        {
            _dragStart = e.GetPosition(null);
            _dragItem  = vm;
        }
    }

    private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var diff = _dragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var handle = sender as FrameworkElement ?? this;
        _sourceRow = FindRowBorder(handle);
        if (_sourceRow is null) { _dragItem = null; return; }

        _adornerLayer = AdornerLayer.GetAdornerLayer(_sourceRow);
        if (_adornerLayer is not null)
        {
            _ghostAdorner = new DragAdorner(_sourceRow);
            _adornerLayer.Add(_ghostAdorner);
        }

        _sourceRow.Opacity = 0.4;

        var data = new DataObject(DragFormat, _dragItem);
        _dragItem = null;

        try
        {
            DragDrop.DoDragDrop(_sourceRow, data, DragDropEffects.Move);
        }
        finally
        {
            // Always restore visual state, even if the drag was cancelled
            CleanupDragVisuals();
        }
    }

    // ── Drop target — row ────────────────────────────────────────────────────

    private void AssignmentRow_DragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DragFormat);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;

        if (sender is FrameworkElement target)
        {
            UpdateGhostPosition(e);
            UpdateInsertionIndicator(target, e);
        }
    }

    private void AssignmentRow_DragLeave(object sender, DragEventArgs e)
    {
        // Don't clear the insertion indicator here — DragOver on the next row will
        // re-place it. Clearing here causes flicker as the cursor moves between rows.
    }

    private void AssignmentRow_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) return;
        if (e.Data.GetData(DragFormat) is not DeviceAssignmentViewModel src) return;
        if (sender is not FrameworkElement target ||
            target.DataContext is not DeviceAssignmentViewModel targetVm) return;

        var editor = ResolveEditor();
        if (editor is null) return;

        var srcIdx    = editor.Assignments.IndexOf(src);
        var targetIdx = editor.Assignments.IndexOf(targetVm);
        if (srcIdx < 0 || targetIdx < 0 || srcIdx == targetIdx) return;

        // Drop in the top half = insert before; bottom half = insert after.
        var localPos    = e.GetPosition(target);
        var insertAfter = localPos.Y > target.ActualHeight / 2;
        var insertIdx   = insertAfter ? targetIdx + 1 : targetIdx;

        // Removing the source shifts everything after it left by one
        if (srcIdx < insertIdx) insertIdx--;
        if (insertIdx < 0 || insertIdx >= editor.Assignments.Count) insertIdx = editor.Assignments.Count - 1;
        if (insertIdx == srcIdx) return;

        editor.Assignments.Move(srcIdx, insertIdx);
        e.Handled = true;
    }

    // ── Drop target — empty area below last row ──────────────────────────────

    private void AssignmentsHost_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (!e.Data.GetDataPresent(DragFormat)) return;
        if (e.Data.GetData(DragFormat) is not DeviceAssignmentViewModel src) return;

        var editor = ResolveEditor();
        if (editor is null) return;

        var srcIdx  = editor.Assignments.IndexOf(src);
        var lastIdx = editor.Assignments.Count - 1;
        if (srcIdx < 0 || srcIdx == lastIdx) return;

        editor.Assignments.Move(srcIdx, lastIdx);
        e.Handled = true;
    }

    // ── Visual helpers ───────────────────────────────────────────────────────

    private void UpdateGhostPosition(DragEventArgs e)
    {
        if (_ghostAdorner is null || _adornerLayer is null) return;
        _ghostAdorner.UpdatePosition(e.GetPosition(_adornerLayer));
    }

    private void UpdateInsertionIndicator(FrameworkElement target, DragEventArgs e)
    {
        var localPos = e.GetPosition(target);
        var insertAfter = localPos.Y > target.ActualHeight / 2;

        // Remove the previous insertion adorner before showing the new one
        RemoveInsertionAdorner();

        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null) return;
        _insertionAdorner = new InsertionAdorner(target, insertAfter);
        layer.Add(_insertionAdorner);
    }

    private void RemoveInsertionAdorner()
    {
        if (_insertionAdorner is null) return;
        var layer = AdornerLayer.GetAdornerLayer(_insertionAdorner.AdornedElement);
        layer?.Remove(_insertionAdorner);
        _insertionAdorner = null;
    }

    private void CleanupDragVisuals()
    {
        if (_sourceRow is not null)
        {
            _sourceRow.Opacity = 1.0;
            _sourceRow = null;
        }
        if (_ghostAdorner is not null && _adornerLayer is not null)
        {
            _adornerLayer.Remove(_ghostAdorner);
            _ghostAdorner = null;
        }
        _adornerLayer = null;
        RemoveInsertionAdorner();
    }

    private static FrameworkElement? FindRowBorder(DependencyObject start)
    {
        // The row template root is a Border tagged via DataContext = DeviceAssignmentViewModel.
        // Walk up the visual tree until we find it.
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is FrameworkElement fe && fe.DataContext is DeviceAssignmentViewModel
                && fe is Border)
                return fe;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private ProfileEditorViewModel? ResolveEditor() =>
        (DataContext as GamesViewModel)?.Editor;

    // Prevent enabling auto-trigger if another profile already auto-triggers
    // on the same exe. Two profiles racing on a single game would have the
    // process watcher pick one non-deterministically — better to refuse the
    // toggle and explain than to ship the user a confusing bug.
    private void AutoTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        if (cb.IsChecked != true) return;
        if (DataContext is not GamesViewModel vm) return;
        var editor = vm.Editor;
        if (editor is null) return;

        var conflict = vm.FindAutoTriggerConflict(editor.ProfileId, editor.ExePath);
        if (conflict is null) return;

        cb.IsChecked = false;

        MessageBox.Show(
            $"Can't enable auto-trigger here. Another profile (\"{conflict}\") is already set to auto-trigger when this game launches.\n\n" +
            "If two profiles auto-triggered on the same .exe, only one would run — and which one wins is unpredictable. To enable it here, first turn off auto-trigger on the other profile (or point one of them at a different .exe).",
            "Auto-trigger conflict",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    // Launch the modal timing-test dialog with the currently-edited profile's
    // exe path + name. The dialog drives its own CalibrationRunner lifecycle
    // and launches/terminates the game itself.
    private void RunTimingTest_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GamesViewModel vm || vm.Editor is null) return;

        var exePath  = vm.Editor.ExePath ?? "";
        var displayName = !string.IsNullOrWhiteSpace(vm.Editor.Name)
            ? vm.Editor.Name
            : (System.IO.Path.GetFileNameWithoutExtension(exePath) ?? "the game");

        var dlg = new CalibrationDialog(exePath, displayName)
        {
            Owner = Window.GetWindow(this),
        };
        dlg.ShowDialog();
    }

    // Double-clicking a row in the available-devices picker adds it to the
    // profile. ListBoxItem raises a direct MouseDoubleClick that the parent
    // ListBox doesn't see, so we hook the item container instead.
    private void PickerItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if (sender is not ListBoxItem item) return;
        if (item.DataContext is not Models.HidDevice device) return;

        var editor = ResolveEditor();
        if (editor is null) return;

        // The click already selected the item; AddCommand uses SelectedAvailable.
        // Belt-and-suspenders: set it explicitly in case selection lagged.
        editor.SelectedAvailable = device;

        if (editor.AddCommand.CanExecute(null))
            editor.AddCommand.Execute(null);

        e.Handled = true;
    }
}

// ── Adorners ─────────────────────────────────────────────────────────────────

/// <summary>
/// Renders a translucent copy of the source row at the current cursor position
/// so the user sees the row "floating" as they drag.
/// </summary>
internal sealed class DragAdorner : Adorner
{
    private readonly VisualBrush _brush;
    private readonly Size        _size;
    private Point                _offset;

    public DragAdorner(FrameworkElement source) : base(source)
    {
        _brush           = new VisualBrush(source) { Opacity = 0.6, Stretch = Stretch.None };
        _size            = new Size(source.ActualWidth, source.ActualHeight);
        IsHitTestVisible = false;
    }

    public void UpdatePosition(Point p)
    {
        // Offset so the ghost sits just below-right of the cursor (doesn't cover it)
        _offset = new Point(p.X + 8, p.Y + 4);
        if (Parent is AdornerLayer layer) layer.Update(AdornedElement);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(_offset, _size);
        dc.DrawRectangle(_brush, null, rect);
    }
}

/// <summary>
/// A 2px horizontal line drawn at the top or bottom edge of the adorned row,
/// indicating where the dragged item will land if dropped now.
/// </summary>
internal sealed class InsertionAdorner : Adorner
{
    private static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(60, 179, 113));

    private readonly bool _atBottom;

    public InsertionAdorner(FrameworkElement adorned, bool atBottom) : base(adorned)
    {
        _atBottom        = atBottom;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (AdornedElement is not FrameworkElement fe) return;
        var y = _atBottom ? fe.ActualHeight - 1 : 0;
        var pen = new Pen(LineBrush, 2);
        dc.DrawLine(pen, new Point(0, y), new Point(fe.ActualWidth, y));
    }
}
