using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace OrbitFileManager.Utilities;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> whose content can be replaced with one change notice. Clearing and
/// adding thousands of rows one by one makes every bound list process each row separately on the UI thread.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces everything with <paramref name="items"/> and raises a single Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (T item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
