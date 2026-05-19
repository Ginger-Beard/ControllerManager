using System.Windows;
using System.Windows.Controls;
using ControllerManager.ViewModels;
using DragEventArgs   = System.Windows.DragEventArgs;
using MouseEventArgs  = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using DragDrop        = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using DataObject      = System.Windows.DataObject;
using Point           = System.Windows.Point;

namespace ControllerManager.Views;

public partial class GamesView : UserControl
{
    // Drag-and-drop reordering state. Drag is initiated from the ☰ handle in each
    // assignment row; drop targets are the row Borders themselves. The dragged item
    // is moved to the position of the row it was dropped on (before or after based
    // on cursor Y within that row).
    private static readonly string DragFormat = "ControllerManager.AssignmentDrag";

    private Point _dragStart;
    private DeviceAssignmentViewModel? _dragItem;

    public GamesView() => InitializeComponent();

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

        var data = new DataObject(DragFormat, _dragItem);
        var src  = sender as FrameworkElement ?? this;
        _dragItem = null; // consumed by DoDragDrop
        DragDrop.DoDragDrop(src, data, DragDropEffects.Move);
    }

    private void AssignmentRow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
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
        var localPos = e.GetPosition(target);
        var insertAfter = localPos.Y > target.ActualHeight / 2;
        var insertIdx = insertAfter ? targetIdx + 1 : targetIdx;

        // When moving down past the source, the source's removal shifts indices left by one.
        if (srcIdx < insertIdx) insertIdx--;
        if (insertIdx < 0 || insertIdx >= editor.Assignments.Count) insertIdx = editor.Assignments.Count - 1;
        if (insertIdx == srcIdx) return;

        editor.Assignments.Move(srcIdx, insertIdx);
        e.Handled = true;
    }

    // Drops in the empty area below the last row → append to end
    private void AssignmentsHost_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return; // already handled by a row
        if (!e.Data.GetDataPresent(DragFormat)) return;
        if (e.Data.GetData(DragFormat) is not DeviceAssignmentViewModel src) return;

        var editor = ResolveEditor();
        if (editor is null) return;

        var srcIdx = editor.Assignments.IndexOf(src);
        var lastIdx = editor.Assignments.Count - 1;
        if (srcIdx < 0 || srcIdx == lastIdx) return;

        editor.Assignments.Move(srcIdx, lastIdx);
        e.Handled = true;
    }

    private ProfileEditorViewModel? ResolveEditor() =>
        (DataContext as GamesViewModel)?.Editor;
}
