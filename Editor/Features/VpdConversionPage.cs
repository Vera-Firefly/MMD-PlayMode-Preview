internal sealed class VpdConversionPage
{
    internal void Draw(ConverterWindowContext context)
    {
        context.Owner.DrawVpdConversionPage();
    }

    internal void DrawAction(ConverterWindowContext context)
    {
        context.Owner.DrawCurrentPageAction();
    }
}
