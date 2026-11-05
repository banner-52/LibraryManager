using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using LibraryManager.Models;

namespace LibraryManager.Views;

public partial class UpdateProgressWindow : ThemedWindow
{
    private readonly Action _cancel;
    private bool _finished;

    public UpdateProgressWindow(Window? owner, Action cancel)
    {
        InitializeComponent();
        Owner = owner;
        _cancel = cancel;
        Loaded += (_, _) =>
        {
            if (DataContext is OperationProgress progress)
            {
                progress.LogEntries.CollectionChanged += LogEntries_CollectionChanged;
                ScrollLogToEnd();
            }
        };
        Closed += (_, _) =>
        {
            if (DataContext is OperationProgress progress)
                progress.LogEntries.CollectionChanged -= LogEntries_CollectionChanged;
        };
    }

    private void LogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ScrollLogToEnd);

    private void ScrollLogToEnd()
    {
        if (LogListBox.Items.Count == 0) return;
        LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }

    public void Complete(string message)
    {
        _finished = true;
        if (DataContext is OperationProgress progress)
        {
            progress.Message = message;
            progress.Percentage = 100;
            progress.CanCancel = false;
            progress.IsIndeterminate = false;
            progress.LogEntries.Add($"{DateTime.Now:T} {message}");
        }
        CancelButton.Content = "Close";
        CancelButton.IsEnabled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_finished) Close();
        else RequestCancellation();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            RequestCancellation();
        }
        base.OnClosing(e);
    }

    private void RequestCancellation()
    {
        CancelButton.IsEnabled = false;
        if (DataContext is OperationProgress progress) progress.Message = "Cancelling...";
        _cancel();
    }
}
