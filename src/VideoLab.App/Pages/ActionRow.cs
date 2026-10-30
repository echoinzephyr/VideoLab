using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace VideoLab.App.Pages;

internal sealed class ActionRow : Panel
{
    private const double Gap = 10;
    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, lineHeight = 0, width = 0;
        foreach (var child in Children.Where(child => child.Visibility == Visibility.Visible))
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity)); var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width) { width = Math.Max(width, x - Gap); x = 0; y += lineHeight + Gap; lineHeight = 0; }
            x += size.Width + Gap; lineHeight = Math.Max(lineHeight, size.Height);
        }
        return new Size(Math.Max(width, Math.Max(0, x - Gap)), y + lineHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children.Where(child => child.Visibility == Visibility.Visible))
        {
            var size = child.DesiredSize; var width = Math.Min(size.Width, finalSize.Width);
            if (x > 0 && x + width > finalSize.Width) { x = 0; y += lineHeight + Gap; lineHeight = 0; }
            child.Arrange(new Rect(x, y, width, size.Height)); x += width + Gap; lineHeight = Math.Max(lineHeight, size.Height);
        }
        return finalSize;
    }
}
