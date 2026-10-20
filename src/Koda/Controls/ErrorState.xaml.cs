using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Koda.Controls;

/// <summary>An error or empty state in place of a page's content, with one action.</summary>
public sealed partial class ErrorState : UserControl
{
    private Action? _action;

    public ErrorState()
    {
        InitializeComponent();
    }

    public void ShowError(Exception error, Action retry) =>
        Show(Helpers.Errors.Title(error), Helpers.Errors.Describe(error), "", "Try again", retry);

    public void Show(string title, string message, string glyph, string? action = null, Action? onAction = null)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Glyph.Glyph = glyph;
        _action = onAction;
        ActionButton.Content = action;
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Visibility = Visibility.Visible;
    }

    private void OnActionClick(object sender, RoutedEventArgs e) => _action?.Invoke();
}
