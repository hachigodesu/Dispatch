using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace Dispatch.App.Behaviors;

/// <summary>
/// WPF's ListBox.SelectedItems can't be data-bound, so this attached property keeps a view-model collection in sync
/// with it, both ways:
/// <c>&lt;ListBox SelectionMode="Extended" b:MultiSelect.SelectedItems="{Binding SelectedMods}" /&gt;</c>
/// </summary>
public static class MultiSelect
{
    public static readonly DependencyProperty SelectedItemsProperty = DependencyProperty.RegisterAttached(
        "SelectedItems", typeof(IList), typeof(MultiSelect), new PropertyMetadata(null, OnChanged));

    private static readonly DependencyProperty SyncingProperty = DependencyProperty.RegisterAttached(
        "Syncing", typeof(bool), typeof(MultiSelect), new PropertyMetadata(false));

    public static IList? GetSelectedItems(DependencyObject d) => (IList?)d.GetValue(SelectedItemsProperty);

    public static void SetSelectedItems(DependencyObject d, IList? value) => d.SetValue(SelectedItemsProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox)
        {
            return;
        }

        if (e.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= Handler(listBox);
        }

        listBox.SelectionChanged -= OnListSelectionChanged;
        if (e.NewValue is IList list)
        {
            listBox.SelectionChanged += OnListSelectionChanged;
            if (list is INotifyCollectionChanged newCollection)
            {
                newCollection.CollectionChanged += Handler(listBox);
            }

            CopyToListBox(listBox, list);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, NotifyCollectionChangedEventHandler> Handlers = new();

    private static NotifyCollectionChangedEventHandler Handler(ListBox listBox) =>
        Handlers.GetValue(listBox, lb => (_, _) =>
        {
            if (GetSelectedItems(lb) is { } list)
            {
                CopyToListBox(lb, list);
            }
        });

    /// <summary>ListBox → view model.</summary>
    private static void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var listBox = (ListBox)sender;
        if ((bool)listBox.GetValue(SyncingProperty) || GetSelectedItems(listBox) is not { } list)
        {
            return;
        }

        listBox.SetValue(SyncingProperty, true);
        try
        {
            foreach (var removed in e.RemovedItems)
            {
                list.Remove(removed);
            }

            foreach (var added in e.AddedItems)
            {
                if (!list.Contains(added))
                {
                    list.Add(added);
                }
            }
        }
        finally
        {
            listBox.SetValue(SyncingProperty, false);
        }
    }

    /// <summary>View model → ListBox (e.g. after a command changes the selection).</summary>
    private static void CopyToListBox(ListBox listBox, IList list)
    {
        if ((bool)listBox.GetValue(SyncingProperty) || listBox.SelectionMode == SelectionMode.Single)
        {
            return;
        }

        listBox.SetValue(SyncingProperty, true);
        try
        {
            listBox.SelectedItems.Clear();
            foreach (var item in list)
            {
                listBox.SelectedItems.Add(item);
            }
        }
        finally
        {
            listBox.SetValue(SyncingProperty, false);
        }
    }
}
