using System.Collections.ObjectModel;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Sub.NETUI.UI;

internal class LibraryListPane<T> : View where T : class
{
    private readonly ObservableCollection<T> _items = [];
    private readonly ObservableCollection<string> _labels = [];
    private readonly Func<T, string> _displayName;
    private readonly ListView _listView;

    public LibraryListPane(string title, Func<T, string> displayName)
    {
        _displayName = displayName;
        Title = title;
        BorderStyle = LineStyle.Single;
        CanFocus = true;

        _listView = new ListView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _listView.Source = new ListWrapper<string>(_labels);
        _listView.ValueChanged += (_, _) => ValueChanged?.Invoke(SelectedValue);
        _listView.Accepted += (_, _) =>
        {
            if (SelectedValue is { } selected)
            {
                ItemAccepted?.Invoke(selected);
            }
        };
        Add(_listView);
    }

    public event Action<T?>? ValueChanged;

    public event Action<T>? ItemAccepted;

    public T? SelectedValue
    {
        get
        {
            int? index = _listView.SelectedItem;
            return index is >= 0 && index < _items.Count ? _items[index.Value] : null;
        }
    }

    public int? SelectedIndex => _listView.SelectedItem;

    public bool ListHasFocus => _listView.HasFocus;

    public void SetItems(IEnumerable<T> items)
    {
        _items.Clear();
        _labels.Clear();
        foreach (T item in items)
        {
            _items.Add(item);
            _labels.Add(_displayName(item));
        }

        _listView.SelectedItem = _items.Count == 0 ? null : 0;
    }

    public void FocusList()
    {
        _listView.SetFocus();
    }
}