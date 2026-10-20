using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Koda.Helpers;

/// <summary>Artists are drawn round, everything else square.</summary>
public sealed partial class CardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Square { get; set; }
    public DataTemplate? Round { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) =>
        (item is CardItem { IsRound: true } ? Round : Square)!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
