using OrbitFileManager.Converters;
using OrbitFileManager.Models;
using OrbitFileManager.Utilities;
using System.Collections.Specialized;
using System.Globalization;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class BigFolderListTests
{
    [Fact]
    public void ReplaceAllRaisesOneResetForThousandsOfRows()
    {
        var rows = new BulkObservableCollection<int> { 1, 2, 3 };
        var notices = new List<NotifyCollectionChangedAction>();
        var properties = new List<string?>();
        rows.CollectionChanged += (_, e) => notices.Add(e.Action);
        ((System.ComponentModel.INotifyPropertyChanged)rows).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        rows.ReplaceAll(Enumerable.Range(100, 15_000));

        Assert.Equal([NotifyCollectionChangedAction.Reset], notices);
        Assert.Contains("Count", properties);
        Assert.Equal(15_000, rows.Count);
        Assert.Equal(100, rows[0]);
        Assert.Equal(15_099, rows[^1]);
    }

    [Fact]
    public void ReplaceAllWithNothingEmptiesTheList()
    {
        var rows = new BulkObservableCollection<string> { "a", "b" };
        rows.ReplaceAll([]);
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData(BrowserViewMode.Details, "Details", true)]
    [InlineData(BrowserViewMode.Details, "LargeIcons", false)]
    [InlineData(BrowserViewMode.Thumbnails, "Thumbnails", true)]
    [InlineData(BrowserViewMode.List, "SmallIcons", false)]
    public void OnlyTheShowingViewGetsTheRows(BrowserViewMode mode, string listShows, bool getsRows)
    {
        var rows = new BulkObservableCollection<int> { 1 };
        object? result = new ItemsForViewModeConverter().Convert([rows, mode], typeof(object), listShows, CultureInfo.InvariantCulture);
        Assert.Equal(getsRows, ReferenceEquals(result, rows));
        if (!getsRows)
            Assert.Null(result);
    }
}
