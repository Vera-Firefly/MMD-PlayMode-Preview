internal sealed class VmdConverterPage
{
    internal void Draw(ConverterWindowContext context)
    {
        context.Owner.DrawVmdConverterPage();
    }

    internal void DrawAction(ConverterWindowContext context)
    {
        context.Owner.DrawCurrentPageAction();
    }
}
