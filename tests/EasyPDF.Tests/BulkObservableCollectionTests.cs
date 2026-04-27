using EasyPDF.Application;
using System.Collections.Specialized;
using System.ComponentModel;
using Xunit;

namespace EasyPDF.Tests;

public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_FiresExactlyOneResetNotification()
    {
        var col = new BulkObservableCollection<int>();
        var actions = new List<NotifyCollectionChangedAction>();
        col.CollectionChanged += (_, e) => actions.Add(e.Action);

        col.ReplaceAll([1, 2, 3, 4, 5]);

        Assert.Single(actions);
        Assert.Equal(NotifyCollectionChangedAction.Reset, actions[0]);
    }

    [Fact]
    public void ReplaceAll_CollectionContainsNewItems()
    {
        var col = new BulkObservableCollection<int> { 99 };

        col.ReplaceAll([1, 2, 3]);

        Assert.Equal([1, 2, 3], col);
    }

    [Fact]
    public void ReplaceAll_WithEmptySequence_ClearsCollection_FiresOneReset()
    {
        var col = new BulkObservableCollection<int> { 1, 2, 3 };
        var actions = new List<NotifyCollectionChangedAction>();
        col.CollectionChanged += (_, e) => actions.Add(e.Action);

        col.ReplaceAll([]);

        Assert.Empty(col);
        Assert.Single(actions);
        Assert.Equal(NotifyCollectionChangedAction.Reset, actions[0]);
    }

    [Fact]
    public void ReplaceAll_FiresCountAndItemPropertyChangedNotifications()
    {
        var col = new BulkObservableCollection<int>();
        var props = new List<string?>();
        col.PropertyChanged += (_, e) => props.Add(e.PropertyName);

        col.ReplaceAll([1, 2, 3]);

        Assert.Contains("Count", props);
        Assert.Contains("Item[]", props);
    }

    [Fact]
    public void Add_AfterReplaceAll_FiresAddNotification()
    {
        var col = new BulkObservableCollection<int>();
        col.ReplaceAll([1, 2]);

        var actions = new List<NotifyCollectionChangedAction>();
        col.CollectionChanged += (_, e) => actions.Add(e.Action);

        col.Add(3);

        Assert.Single(actions);
        Assert.Equal(NotifyCollectionChangedAction.Add, actions[0]);
    }

    [Fact]
    public void ReplaceAll_SuppressesPerItemNotificationsDuringBulkAdd()
    {
        // Verify that no Add events are raised for individual items —
        // only the final Reset fires.
        var col = new BulkObservableCollection<int>();
        var addCount = 0;
        col.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) addCount++;
        };

        col.ReplaceAll(Enumerable.Range(0, 100));

        Assert.Equal(0, addCount);
        Assert.Equal(100, col.Count);
    }
}
